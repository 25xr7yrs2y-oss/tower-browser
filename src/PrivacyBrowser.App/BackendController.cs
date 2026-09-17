using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyBrowser.App;

internal sealed record BackendTimeouts(
    TimeSpan HealthProbe,
    TimeSpan Ordinary,
    TimeSpan ProviderDiscovery,
    TimeSpan ProviderConnect,
    TimeSpan GracefulStop,
    TimeSpan Readiness,
    TimeSpan ReadinessPollInterval)
{
    public static BackendTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(75),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(1));
}

public sealed class BackendController : IAsyncDisposable
{
    public const int ControlPort = 44050;
    public const int ProxyPort = 4449;

    private static string ConnectionPath => $"connection?id={ProxyPort}";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly AppOptions _options;
    private readonly HttpClient _client;
    private readonly BackendTimeouts _timeouts;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<bool>? _connectedStateVerifier;
    private readonly PaymentJournalStore _paymentJournal;
    private readonly SemaphoreSlim _paymentCreation = new(1, 1);
    private Process? _process;
    private bool _stopping;
    private bool _connectOutcomeIndeterminate;
    private bool _paymentCreationOutcomeUnresolved;

    public BackendController(AppOptions options)
        : this(options, new SocketsHttpHandler { UseProxy = false }, BackendTimeouts.Default)
    {
    }

    internal BackendController(
        AppOptions options,
        HttpMessageHandler handler,
        BackendTimeouts timeouts,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<bool>? connectedStateVerifier = null)
    {
        _options = options;
        _timeouts = timeouts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _timeProvider, token));
        _connectedStateVerifier = connectedStateVerifier;
        _paymentJournal = new PaymentJournalStore(options.BundleRoot);
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{ControlPort}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public event Action<string>? Log;
    public event Action<BackendLifecycleState>? LifecycleChanged;
    public int? OwnedProcessId => _process?.Id;
    public bool IsConnectOutcomeIndeterminate => _connectOutcomeIndeterminate;
    public BackendLifecycleState LifecycleState { get; private set; } = BackendLifecycleState.Stopped;

    internal TimeSpan ClientTimeoutForTesting => _client.Timeout;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetLifecycle(BackendLifecycleState.Starting);
        try
        {
            if (_options.SkipBackendLaunch)
            {
                Log?.Invoke("Using an already-running backend as requested. Browser launch remains disabled because ownership cannot be verified.");
                await WaitUntilReadyAsync(cancellationToken);
                SetLifecycle(BackendLifecycleState.Running);
                return;
            }

            if (await IsHealthyAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"A Myst control endpoint is already listening on 127.0.0.1:{ControlPort}. " +
                    "Stop it first, or use --skip-backend-launch for diagnostics only.");
            }

            var nodeExe = ResolveNodeExecutable(_options.BackendExe);
            var startInfo = new ProcessStartInfo
            {
                FileName = nodeExe,
                WorkingDirectory = Path.GetDirectoryName(nodeExe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
            {
                "--ui.enable=false",
                "--usermode",
                "--proxymode",
                "--proxy.bind.address=127.0.0.1",
                "--consumer",
                "--tequilapi.address=127.0.0.1",
                $"--tequilapi.port={ControlPort}",
                "--discovery.type=api",
                "daemon",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
            process.Exited += (_, _) =>
            {
                var exitCode = TryGetExitCode(process);
                Log?.Invoke($"Backend exited with code {exitCode}.");
                if (!_stopping) SetLifecycle(BackendLifecycleState.Crashed);
            };
            _process = process;

            if (!process.Start())
            {
                throw new InvalidOperationException("The Myst backend process could not be started.");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Log?.Invoke($"Started Myst backend process {process.Id} without the Electron web UI.");
            await WaitUntilReadyAsync(cancellationToken);
            SetLifecycle(BackendLifecycleState.Running);
        }
        catch
        {
            SetLifecycle(BackendLifecycleState.Failed);
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (_options.SkipBackendLaunch)
        {
            throw new InvalidOperationException("An adopted backend cannot be restarted safely. Start Privacy Browser without --skip-backend-launch.");
        }

        await StopOwnedProcessAsync(respectKeepRunning: false);
        await StartAsync(cancellationToken);
    }

    public async Task<BackendSnapshot> GetSnapshotAsync(
        string? selectedIdentityId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsHealthyAsync(cancellationToken))
        {
            return BackendSnapshot.Offline();
        }

        // These resources have different upstream dependencies. Capture each result
        // independently so a slow registration RPC cannot make the whole UI appear offline.
        var connectionTask = CaptureAsync("connection", async () =>
        {
            var connection = await GetAsync<ConnectionInfo>(
                BackendOperation.ConnectionStatus, "connection?id={proxy_port}", ConnectionPath,
                _timeouts.Ordinary, cancellationToken);
            ObserveConnectionState(connection);
            return connection;
        }, cancellationToken);
        var identitiesTask = CaptureAsync("identity", () => GetAsync<IdentityList>(
            BackendOperation.IdentityList, "identities", "identities", _timeouts.Ordinary, cancellationToken), cancellationToken);
        var termsTask = CaptureAsync("terms", () => GetAsync<TermsStatus>(
            BackendOperation.TermsStatus, "terms", "terms", _timeouts.Ordinary, cancellationToken), cancellationToken);
        await Task.WhenAll(connectionTask, identitiesTask, termsTask);

        var issues = new List<BackendIssue>();
        AddIssue(connectionTask.Result, issues);
        AddIssue(identitiesTask.Result, issues);
        AddIssue(termsTask.Result, issues);

        var identities = new List<IdentityDetails>();
        var references = identitiesTask.Result.Value?.Identities
            .Where(identity => !string.IsNullOrWhiteSpace(identity.Id))
            .GroupBy(identity => identity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray() ?? [];
        var detailTasks = references.Select(reference => CaptureAsync("identity status",
            () => GetAsync<IdentityDetails>(BackendOperation.IdentityStatus, "identities/{identity}",
                $"identities/{Uri.EscapeDataString(reference.Id)}", _timeouts.Ordinary, cancellationToken),
            cancellationToken)).ToArray();
        await Task.WhenAll(detailTasks);
        for (var index = 0; index < detailTasks.Length; index++)
        {
            var identityResult = detailTasks[index].Result;
            AddIssue(identityResult, issues);
            identities.Add(identityResult.Value ?? new IdentityDetails
            {
                Id = references[index].Id,
                RegistrationStatus = "Unavailable",
            });
        }

        var resolvedSelection = identities.Any(identity =>
            identity.Id.Equals(selectedIdentityId, StringComparison.OrdinalIgnoreCase))
            ? selectedIdentityId
            : null;

        return new BackendSnapshot(
            true,
            connectionTask.Result.Value ?? new ConnectionInfo { Status = "STATUS_UNAVAILABLE" },
            identities,
            resolvedSelection,
            termsTask.Result.Value,
            issues,
            DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<ProviderProposal>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<ProposalList>(BackendOperation.ProviderDiscovery, "proposals",
            "proposals?service_type=wireguard&access_policy=all", _timeouts.ProviderDiscovery, cancellationToken);
        return result.Proposals
            .Where(p => !string.IsNullOrWhiteSpace(p.ProviderId) &&
                        p.ServiceType.Equals("wireguard", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Location.Country)
            .ThenBy(p => p.Location.City)
            .ThenBy(p => p.Price.PerGiBTokens.Value)
            .Take(500)
            .ToArray();
    }

    public Task<IdentityReference> CreateIdentityAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        ValidatePassphrase(passphrase);
        return SendAsync<IdentityReference>(BackendOperation.IdentityCreate, HttpMethod.Post,
            "identities", "identities", new { passphrase }, _timeouts.Ordinary, cancellationToken);
    }

    public async Task<IdentityReference> ImportIdentityAsync(
        byte[] encryptedKey,
        string currentPassphrase,
        CancellationToken cancellationToken = default)
    {
        if (encryptedKey.Length == 0) throw new InvalidOperationException("The selected identity file is empty.");
        ValidatePassphrase(currentPassphrase);
        return await SendAsync<IdentityReference>(BackendOperation.IdentityImport, HttpMethod.Post,
            "identities-import", "identities-import", new
        {
            data = encryptedKey,
            current_passphrase = currentPassphrase,
            new_passphrase = currentPassphrase,
            set_default = true,
        }, _timeouts.Ordinary, cancellationToken);
    }

    public Task UnlockIdentityAsync(string id, string passphrase, CancellationToken cancellationToken = default)
    {
        ValidatePassphrase(passphrase);
        return SendAsync(BackendOperation.IdentityUnlock, HttpMethod.Put, "identities/{identity}/unlock",
            $"identities/{Uri.EscapeDataString(id)}/unlock", new { passphrase }, _timeouts.Ordinary, cancellationToken);
    }

    public async Task RegisterIdentityAsync(string id, string passphrase, CancellationToken cancellationToken = default)
    {
        await UnlockIdentityAsync(id, passphrase, cancellationToken);
        await SendAsync(BackendOperation.IdentityRegistration, HttpMethod.Post, "identities/{identity}/register",
            $"identities/{Uri.EscapeDataString(id)}/register", new { }, _timeouts.Ordinary, cancellationToken);
    }

    public Task AcceptConsumerTermsAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            throw new InvalidOperationException("The backend did not report a current terms version.");
        }
        return SendAsync(BackendOperation.TermsAcceptance, HttpMethod.Post, "terms", "terms", new
        {
            agreed_consumer = true,
            agreed_version = currentVersion,
        }, _timeouts.Ordinary, cancellationToken);
    }

    public async Task<BalanceStatus> RefreshBalanceAsync(string identityId, CancellationToken cancellationToken = default)
    {
        return await SendAsync<BalanceStatus>(BackendOperation.BalanceRefresh, HttpMethod.Put,
            "identities/{identity}/balance/refresh",
            $"identities/{Uri.EscapeDataString(identityId)}/balance/refresh", null, _timeouts.Ordinary, cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentGateway>> GetPaymentGatewaysAsync(CancellationToken cancellationToken = default)
    {
        var mystGatewaysTask = GetAsync<List<PaymentGateway>>(BackendOperation.PaymentGatewayDiscovery,
            "v2/payment-order-gateways", "v2/payment-order-gateways?options_currency=MYST",
            _timeouts.Ordinary, cancellationToken);
        var usdGatewaysTask = GetAsync<List<PaymentGateway>>(BackendOperation.PaymentGatewayDiscovery,
            "v2/payment-order-gateways", "v2/payment-order-gateways?options_currency=USD",
            _timeouts.Ordinary, cancellationToken);
        await Task.WhenAll(mystGatewaysTask, usdGatewaysTask);
        return PaymentGatewayRegistry.IntersectDiscoveredGateways(
            mystGatewaysTask.Result, usdGatewaysTask.Result);
    }

    public async Task<CreatedPaymentOrder> CreatePaymentOrderAsync(
        string identityId,
        PaymentGateway gateway,
        string amountText,
        string payCurrency,
        string country,
        string state,
        CancellationToken cancellationToken = default)
    {
        await _paymentCreation.WaitAsync(cancellationToken);
        try
        {
            if (!PaymentIdentity.IsValid(identityId))
            {
                throw new InvalidOperationException("The selected payment identity is invalid.");
            }
            if (_paymentCreationOutcomeUnresolved)
            {
                throw new PaymentOrderAmbiguousException();
            }

            var adapter = PaymentGatewayRegistry.GetAdapter(gateway.Name);
            if (gateway.Currencies is null || !gateway.Currencies.Contains(payCurrency, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"{gateway.DisplayName} does not accept {payCurrency}.");
            }

            var requestedAmount = adapter.ParseAndFormatAmount(amountText);
            var amount = decimal.Parse(requestedAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            if (!adapter.IsAmountAllowed(amount, gateway))
            {
                var relation = gateway.Name == CoinGatePaymentGatewayAdapter.CanonicalGatewayName ? "more than" : "at least";
                throw new InvalidOperationException(
                    $"The amount must be {relation} {adapter.EffectiveMinimum(gateway):0.00} {adapter.AmountCurrency}.");
            }

            var location = PaymentLocation.Validate(country, state);
            var existingJournal = _paymentJournal.Load();
            if (existingJournal is not null && !PaymentStatus.IsTerminal(existingJournal.LastStatus))
            {
                throw new InvalidOperationException(
                    $"Payment order {existingJournal.OrderId} is still {PaymentStatus.Display(existingJournal.LastStatus).ToLowerInvariant()}. " +
                    "Only one active payment order is permitted.");
            }

            var baseline = await RefreshBalanceAsync(identityId, cancellationToken);
            if (!PaymentBalanceEvidence.TryParseWei(baseline.BalanceTokens.Wei, out _))
            {
                throw new InvalidOperationException("The baseline wallet balance was invalid; no payment order was created.");
            }

            // Listing before POST is mandatory: it makes a timed-out/transport-ambiguous
            // request reconcilable without ever repeating the state-changing request.
            var before = await GetPaymentOrdersAsync(identityId, cancellationToken);
            var beforeIds = before.Where(order => !string.IsNullOrEmpty(order.Id))
                .Select(order => order.Id).ToHashSet(StringComparer.Ordinal);
            var intent = new PaymentOrderIntent(
                identityId, adapter.GatewayName, requestedAmount, adapter.AmountCurrency, payCurrency);
            var request = adapter.BuildRequest(
                requestedAmount, payCurrency, location.Country, location.State);

            PaymentOrder order;
            _paymentCreationOutcomeUnresolved = true;
            try
            {
                order = await SendAsync<PaymentOrder>(BackendOperation.PaymentOrderCreate, HttpMethod.Post,
                    "v2/identities/{identity}/{gateway}/payment-order",
                    $"v2/identities/{Uri.EscapeDataString(identityId)}/{Uri.EscapeDataString(adapter.GatewayName)}/payment-order",
                    request, _timeouts.Ordinary, cancellationToken);
            }
            catch (Exception exception) when (exception is BackendOperationTimeoutException or
                                               BackendCallerCanceledException or HttpRequestException)
            {
                order = await ReconcilePaymentOrderAsync(identityId, intent, beforeIds);
            }
            catch
            {
                _paymentCreationOutcomeUnresolved = false;
                throw;
            }

            adapter.ValidateOrder(order, intent);
            var target = order.GetPaymentTarget(adapter.GatewayName);
            var now = DateTimeOffset.UtcNow;
            var journal = new PaymentJournalEntry(
                identityId,
                adapter.GatewayName,
                order.Id,
                requestedAmount,
                adapter.AmountCurrency,
                payCurrency,
                order.ReceiveMyst,
                baseline.BalanceTokens.Wei,
                now,
                now,
                order.Status);
            _paymentJournal.Save(journal);
            _paymentCreationOutcomeUnresolved = false;
            return new CreatedPaymentOrder(order, target, journal);
        }
        finally
        {
            _paymentCreation.Release();
        }
    }

    public PaymentJournalEntry? GetPaymentJournal() => _paymentJournal.Load();

    public void ClearTerminalPayment() => _paymentJournal.ClearTerminal();

    public async Task<IReadOnlyList<PaymentOrder>> GetPaymentOrdersAsync(
        string identityId,
        CancellationToken cancellationToken = default)
    {
        return await GetAsync<List<PaymentOrder>>(BackendOperation.PaymentOrderList,
            "v2/identities/{identity}/payment-order",
            $"v2/identities/{Uri.EscapeDataString(identityId)}/payment-order",
            _timeouts.Ordinary, cancellationToken);
    }

    public async Task<PaymentOrder> GetPaymentOrderAsync(
        string identityId,
        string orderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(orderId) || orderId.Length > 200)
        {
            throw new InvalidOperationException("The payment order ID is invalid.");
        }
        return await GetAsync<PaymentOrder>(BackendOperation.PaymentOrderGet,
            "v2/identities/{identity}/payment-order/{order_id}",
            $"v2/identities/{Uri.EscapeDataString(identityId)}/payment-order/{Uri.EscapeDataString(orderId)}",
            _timeouts.Ordinary, cancellationToken);
    }

    public async Task<PaymentStatusSnapshot> PollPaymentOrderAsync(
        CancellationToken cancellationToken = default)
    {
        var journal = _paymentJournal.Load()
            ?? throw new InvalidOperationException("No resumable payment order exists.");
        var intent = new PaymentOrderIntent(
            journal.Identity, journal.Gateway, journal.RequestedAmount, journal.AmountCurrency, journal.PayCurrency);
        var adapter = PaymentGatewayRegistry.GetAdapter(journal.Gateway);
        var order = await GetPaymentOrderAsync(journal.Identity, journal.OrderId, cancellationToken);
        adapter.ValidateOrder(order, intent);
        if (!PaymentAmount.TryParseResponseAmount(order.ReceiveMyst, out var observedMyst) ||
            !PaymentAmount.TryParseResponseAmount(journal.ExpectedMyst, out var expectedMyst) ||
            observedMyst != expectedMyst)
        {
            throw new InvalidOperationException("The resumed payment order did not match the recorded receive amount.");
        }

        // Order status and wallet credit are independent facts. Refresh the wallet
        // even for unknown/pending states so an observed credit is never inferred.
        var balance = await RefreshBalanceAsync(journal.Identity, cancellationToken);
        if (!PaymentBalanceEvidence.TryParseWei(balance.BalanceTokens.Wei, out _))
        {
            throw new InvalidOperationException("The refreshed wallet balance was invalid.");
        }
        var observedAt = DateTimeOffset.UtcNow;
        var updated = _paymentJournal.UpdateStatus(order.Status, observedAt);
        var paid = PaymentStatus.IsPaid(order.Status);
        var balanceIncreased = PaymentBalanceEvidence.HasIncreased(
            balance.BalanceTokens.Wei, journal.BaselineBalanceWei);
        return new PaymentStatusSnapshot(
            order,
            updated,
            PaymentStatus.Display(order.Status),
            paid,
            balanceIncreased,
            TokenAmount.FromWei(journal.BaselineBalanceWei),
            balance.BalanceTokens.Value,
            paid && balanceIncreased,
            observedAt);
    }

    public async Task ConnectAsync(
        string identityId,
        string passphrase,
        ProviderProposal provider,
        CancellationToken cancellationToken = default)
    {
        if (_connectOutcomeIndeterminate)
        {
            var priorState = await GetConnectionStateAsync(cancellationToken);
            ResolveIndeterminateConnect(priorState, priorAttempt: true);
            return;
        }

        await UnlockIdentityAsync(identityId, passphrase, cancellationToken);
        try
        {
            await SendAsync(BackendOperation.ProviderConnect, HttpMethod.Put, "connection", "connection", new
            {
                consumer_id = identityId,
                provider_id = provider.ProviderId,
                service_type = string.IsNullOrWhiteSpace(provider.ServiceType) ? "wireguard" : provider.ServiceType,
                filter = new { include_monitoring_failed = true },
                connect_options = new
                {
                    dns = "provider",
                    kill_switch = true,
                    proxy_port = ProxyPort,
                },
            }, _timeouts.ProviderConnect, cancellationToken);
            _connectOutcomeIndeterminate = false;
        }
        catch (BackendOperationTimeoutException)
        {
            _connectOutcomeIndeterminate = true;
            ConnectionInfo state;
            try
            {
                state = await GetConnectionStateAsync(cancellationToken);
            }
            catch (BackendCallerCanceledException)
            {
                throw;
            }
            catch
            {
                // The PUT outcome remains unknown. The next Connect attempt must reconcile
                // through GET /connection before another state-changing request is allowed.
                throw new BackendConnectionStateException(
                    "Provider connection reached its deadline and the backend state could not be reconciled. " +
                    "Refresh connection status before trying again.", "UNAVAILABLE");
            }
            ResolveIndeterminateConnect(state, priorAttempt: false);
        }
        catch (BackendCallerCanceledException)
        {
            // Cancellation can race with the backend accepting the request. Require
            // a state reconciliation before any later Connect attempt.
            _connectOutcomeIndeterminate = true;
            throw;
        }
        catch (HttpRequestException)
        {
            // A loopback transport failure does not prove that the backend rejected
            // the already-sent PUT, so fail closed against a duplicate attempt.
            _connectOutcomeIndeterminate = true;
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(BackendOperation.ProviderDisconnect, HttpMethod.Delete,
            "connection?id={proxy_port}", ConnectionPath, null, _timeouts.Ordinary, cancellationToken);
        _connectOutcomeIndeterminate = false;
    }

    public bool IsOwnedProxyListening()
    {
        if (_process is null)
        {
            throw new InvalidOperationException(
                "Browser launch is disabled for an adopted backend because proxy process ownership cannot be verified.");
        }
        var listeners = TcpListenerInspector.GetListeners(ProxyPort);
        if (listeners.Count == 0) return false;
        if (listeners.Any(l => !IPAddress.IsLoopback(l.Address)))
        {
            throw new InvalidOperationException($"Proxy port {ProxyPort} has a non-loopback listener.");
        }
        if (listeners.Any(l => l.ProcessId != _process.Id))
        {
            throw new InvalidOperationException($"Proxy port {ProxyPort} is owned by an unexpected process.");
        }
        return true;
    }

    public async Task StopAsync()
    {
        await StopOwnedProcessAsync(respectKeepRunning: true);
    }

    private async Task StopOwnedProcessAsync(bool respectKeepRunning)
    {
        if ((respectKeepRunning && _options.KeepBackendRunning) || _process is null) return;
        _stopping = true;

        try
        {
            await SendAsync(BackendOperation.GracefulStop, HttpMethod.Post, "stop", "stop", null,
                _timeouts.GracefulStop, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Graceful backend shutdown failed: {ex.Message}");
        }

        try
        {
            if (!_process.HasExited && !await WaitForExitAsync(_process, TimeSpan.FromSeconds(5)))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The owned process exited between checks.
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _connectOutcomeIndeterminate = false;
            _stopping = false;
            SetLifecycle(BackendLifecycleState.Stopped);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _client.Dispose();
        _paymentCreation.Dispose();
    }

    internal async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException($"The Myst backend exited before it became ready (exit code {_process.ExitCode}).");
            }

            var elapsed = _timeProvider.GetElapsedTime(started);
            var remaining = _timeouts.Readiness - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new BackendOperationTimeoutException(
                    BackendOperation.BackendReadiness, _timeouts.Readiness, elapsed);
            }

            if (await IsHealthyAsync(cancellationToken, Min(_timeouts.HealthProbe, remaining)))
            {
                Log?.Invoke("Myst backend control endpoint is ready on loopback port 44050.");
                return;
            }

            elapsed = _timeProvider.GetElapsedTime(started);
            remaining = _timeouts.Readiness - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new BackendOperationTimeoutException(
                    BackendOperation.BackendReadiness, _timeouts.Readiness, elapsed);
            }
            await _delay(Min(_timeouts.ReadinessPollInterval, remaining), cancellationToken);
        }
    }

    private Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        IsHealthyAsync(cancellationToken, _timeouts.HealthProbe);

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken, TimeSpan timeout)
    {
        try
        {
            using var response = await SendCoreAsync(BackendOperation.BackendReadiness, HttpMethod.Get,
                "healthcheck", "healthcheck", null, timeout, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (BackendOperationTimeoutException) { return false; }
    }

    private async Task<T> GetAsync<T>(
        BackendOperation operation,
        string routeTemplate,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(
            operation, HttpMethod.Get, routeTemplate, path, null, timeout, cancellationToken);
        await EnsureSuccessAsync(operation, response, cancellationToken);
        return await ReadResponseAsync<T>(operation, response, cancellationToken);
    }

    private async Task SendAsync(
        BackendOperation operation,
        HttpMethod method,
        string routeTemplate,
        string path,
        object? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(
            operation, method, routeTemplate, path, body, timeout, cancellationToken);
        await EnsureSuccessAsync(operation, response, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        BackendOperation operation,
        HttpMethod method,
        string routeTemplate,
        string path,
        object? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(
            operation, method, routeTemplate, path, body, timeout, cancellationToken);
        await EnsureSuccessAsync(operation, response, cancellationToken);
        return await ReadResponseAsync<T>(operation, response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        BackendOperation operation,
        HttpMethod method,
        string routeTemplate,
        string path,
        object? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        var started = _timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await _client.SendAsync(request, linked.Token);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            LogDiagnostic(operation, method, routeTemplate, timeout, started, "caller-canceled", ex);
            throw new BackendCallerCanceledException(operation, ex, cancellationToken);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            var elapsed = _timeProvider.GetElapsedTime(started);
            LogDiagnostic(operation, method, routeTemplate, timeout, started, "deadline-expired", ex);
            throw new BackendOperationTimeoutException(operation, timeout, elapsed, ex);
        }
        catch (HttpRequestException ex)
        {
            LogDiagnostic(operation, method, routeTemplate, timeout, started, "transport-error", ex);
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(
        BackendOperation operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw BackendErrorTranslator.FromResponse(operation, response.StatusCode, response.ReasonPhrase, detail);
    }

    private static async Task<T> ReadResponseAsync<T>(
        BackendOperation operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                ?? throw new BackendMalformedResponseException(operation);
        }
        catch (JsonException ex)
        {
            throw new BackendMalformedResponseException(operation, ex);
        }
        catch (NotSupportedException ex)
        {
            throw new BackendMalformedResponseException(operation, ex);
        }
    }

    private async Task<ConnectionInfo> GetConnectionStateAsync(CancellationToken cancellationToken)
    {
        var connection = await GetAsync<ConnectionInfo>(BackendOperation.ConnectionStatus,
            "connection?id={proxy_port}", ConnectionPath, _timeouts.Ordinary, cancellationToken);
        ObserveConnectionState(connection);
        return connection;
    }

    private async Task<PaymentOrder> ReconcilePaymentOrderAsync(
        string identityId,
        PaymentOrderIntent intent,
        IReadOnlySet<string> beforeIds)
    {
        try
        {
            var candidates = (await GetPaymentOrdersAsync(identityId, CancellationToken.None))
                .Where(order => !beforeIds.Contains(order.Id))
                .Where(order => PaymentOrderValidator.IsExactIntentMatch(order, intent))
                .ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }
        }
        catch (PaymentOrderAmbiguousException)
        {
            throw;
        }
        catch
        {
            // A failed reconciliation cannot prove whether the POST created an order.
        }
        throw new PaymentOrderAmbiguousException();
    }

    private void ResolveIndeterminateConnect(ConnectionInfo state, bool priorAttempt)
    {
        var normalized = NormalizeConnectionState(state.Status);
        if (normalized == "CONNECTING")
        {
            _connectOutcomeIndeterminate = true;
            throw new BackendConnectionStateException(
                "The backend is still connecting to the provider. Wait for connection status to settle before trying again.",
                normalized);
        }

        if (normalized == "CONNECTED")
        {
            // Keep retries blocked until both backend state and the app-owned
            // loopback proxy have been verified. A retry that observes this state
            // returns successfully instead of issuing another PUT /connection.
            _connectOutcomeIndeterminate = true;
            var verified = _connectedStateVerifier?.Invoke() ?? IsOwnedProxyListening();
            if (!verified)
            {
                throw new BackendConnectionStateException(
                    "The backend reports a provider connection, but the app-owned loopback proxy is not ready. " +
                    "Browser launch remains blocked; refresh status or disconnect safely.", normalized);
            }
            _connectOutcomeIndeterminate = false;
            return;
        }

        _connectOutcomeIndeterminate = false;
        var prefix = priorAttempt
            ? "The previous provider connection attempt has finished"
            : "Provider connection reached its deadline";
        throw new BackendConnectionStateException(
            $"{prefix}; the reconciled backend state is {DisplayConnectionState(normalized)}. " +
            "Refresh providers before starting a new attempt.", normalized);
    }

    private void ObserveConnectionState(ConnectionInfo state)
    {
        if (_connectOutcomeIndeterminate &&
            !state.Status.Equals("CONNECTING", StringComparison.OrdinalIgnoreCase) &&
            !state.Status.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            _connectOutcomeIndeterminate = false;
        }
    }

    private void LogDiagnostic(
        BackendOperation operation,
        HttpMethod method,
        string routeTemplate,
        TimeSpan timeout,
        long started,
        string outcome,
        Exception exception)
    {
        var elapsed = _timeProvider.GetElapsedTime(started);
        Log?.Invoke($"Backend request: operation={operation}; method={method.Method}; route={routeTemplate}; " +
            $"budget_ms={timeout.TotalMilliseconds:0}; elapsed_ms={elapsed.TotalMilliseconds:0}; " +
            $"outcome={outcome}; exception={exception.GetType().Name}.");
    }

    private static string NormalizeConnectionState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return "UNKNOWN";
        var normalized = new string(state.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            .Take(40).ToArray()).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "UNKNOWN" : normalized;
    }

    private static string DisplayConnectionState(string state) => state switch
    {
        "NOTCONNECTED" or "NOT_CONNECTED" => "not connected",
        "DISCONNECTED" => "disconnected",
        "FAILED" => "failed",
        _ => "not connected",
    };

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static async Task<ApiResult<T>> CaptureAsync<T>(
        string area,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return new ApiResult<T>(await operation(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ApiResult<T>(default, new BackendIssue(area, BackendErrorTranslator.ToUserMessage(ex)));
        }
    }

    private static void AddIssue<T>(ApiResult<T> result, ICollection<BackendIssue> issues)
    {
        if (result.Issue is not null) issues.Add(result.Issue);
    }

    private static string ResolveNodeExecutable(string configuredPath)
    {
        if (!File.Exists(configuredPath))
        {
            throw new FileNotFoundException("Backend executable was not found.", configuredPath);
        }
        if (Path.GetFileName(configuredPath).Equals("myst.exe", StringComparison.OrdinalIgnoreCase))
        {
            return configuredPath;
        }

        var root = Path.GetDirectoryName(configuredPath)!;
        var node = Path.Combine(root, "resources", "app.asar.unpacked", "node_modules",
            "@mysteriumnetwork", "node", "bin", "win", "x64", "myst.exe");
        if (!File.Exists(node))
        {
            throw new FileNotFoundException(
                "The unpacked Myst node executable was not found. Supply --backend-exe with MysteriumVPN.exe or myst.exe.", node);
        }
        return node;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetLifecycle(BackendLifecycleState state)
    {
        LifecycleState = state;
        LifecycleChanged?.Invoke(state);
    }

    private static string TryGetExitCode(Process process)
    {
        try { return process.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("Enter the identity passphrase and try again.");
        }
    }

    private sealed record ApiResult<T>(T? Value, BackendIssue? Issue);
}
