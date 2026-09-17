using System.Diagnostics;
using System.Net;
using System.Text;
using PrivacyBrowser.App;

var tests = new (string Name, Func<Task> Run)[]
{
    ("reviewed production budgets and infinite shared timeout", ReviewedBudgetsAndInfiniteClientTimeout),
    ("provider connect may outlive ordinary 15 second budget", ProviderConnectMayOutliveOrdinaryBudget),
    ("provider connect deadline reconciles instead of issuing a duplicate", ProviderConnectDeadlineReconciles),
    ("provider discovery may outlive ordinary 15 second budget", ProviderDiscoveryMayOutliveOrdinaryBudget),
    ("payment gateway discovery and creation fail closed", PaymentGatewayDiscoveryAndCreationFailClosed),
    ("ambiguous payment POST reconciles once without duplicate creation", AmbiguousPaymentPostReconcilesWithoutDuplicate),
    ("single active payment order is enforced", SingleActivePaymentOrderIsEnforced),
    ("payment poll reports paid and balance evidence independently", PaymentPollSeparatesPaidAndBalance),
    ("ordinary operation deadline is enforced", OrdinaryDeadlineIsEnforced),
    ("caller cancellation differs from deadline expiry", CallerCancellationDiffersFromTimeout),
    ("canceled connect remains indeterminate until reconciled", CanceledConnectRequiresReconciliation),
    ("readiness uses one absolute deadline", ReadinessUsesAbsoluteDeadline),
    ("timeout labels and diagnostics contain no request secrets", DiagnosticsAreOperationLabeledAndSecretFree),
    ("connect timeout reconciliation preserves CONNECTING", ConnectTimeoutReconcilesConnecting),
    ("connect timeout reconciliation verifies CONNECTED", ConnectTimeoutReconcilesConnected),
    ("retry that reconciles CONNECTED does not issue another PUT", ConnectedRetryDoesNotDuplicatePut),
    ("connect timeout reconciliation surfaces final failure state", ConnectTimeoutReconcilesFailedState),
    ("proxy connection operations consistently use ID 4449", ProxyConnectionOperationsUseProxyPortId),
    ("malformed and backend HTTP responses remain distinct", MalformedAndHttpErrorsRemainDistinct),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception}");
    }
}

if (failures > 0) return 1;
Console.WriteLine($"PASS: all {tests.Length} backend deadline tests passed.");
return 0;

static async Task ReviewedBudgetsAndInfiniteClientTimeout()
{
    var defaults = BackendTimeouts.Default;
    Equal(TimeSpan.FromSeconds(2), defaults.HealthProbe);
    Equal(TimeSpan.FromSeconds(15), defaults.Ordinary);
    Equal(TimeSpan.FromSeconds(30), defaults.ProviderDiscovery);
    Equal(TimeSpan.FromSeconds(75), defaults.ProviderConnect);
    Equal(TimeSpan.FromSeconds(8), defaults.GracefulStop);
    Equal(TimeSpan.FromSeconds(30), defaults.Readiness);

    await using var controller = Controller(new FakeHandler((_, _) =>
        Task.FromResult(Response(HttpStatusCode.OK, "{}"))), defaults);
    Equal(Timeout.InfiniteTimeSpan, controller.ClientTimeoutForTesting);
}

static async Task ProviderConnectMayOutliveOrdinaryBudget()
{
    var timeouts = TestTimeouts(ordinaryMs: 25, discoveryMs: 100, connectMs: 140);
    var putCount = 0;
    await using var controller = Controller(new FakeHandler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/connection")
        {
            putCount++;
            await Task.Delay(65, token);
        }
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    await controller.ConnectAsync("identity", "passphrase", Provider());
    Equal(1, putCount);
}

static async Task ProviderConnectDeadlineReconciles()
{
    var timeouts = TestTimeouts(ordinaryMs: 35, discoveryMs: 100, connectMs: 45);
    var putCount = 0;
    await using var controller = Controller(new FakeHandler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/connection")
        {
            putCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/connection")
            return Response(HttpStatusCode.OK, "{\"status\":\"NOT_CONNECTED\"}");
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    var error = await ThrowsAsync<BackendConnectionStateException>(() =>
        controller.ConnectAsync("identity", "passphrase", Provider()));
    Equal("NOT_CONNECTED", error.SanitizedState);
    Equal(1, putCount);
}

static async Task PaymentGatewayDiscoveryAndCreationFailClosed()
{
    var requestCount = 0;
    var timeouts = TestTimeouts(ordinaryMs: 80, discoveryMs: 120, connectMs: 150);
    await using var controller = Controller(new FakeHandler((request, _) =>
    {
        requestCount++;
        Equal(HttpMethod.Get, request.Method);
        Equal("/v2/payment-order-gateways", request.RequestUri!.AbsolutePath);
        var json = request.RequestUri.Query == "?options_currency=MYST"
            ? """[{"name":"coingate","order_options":{"minimum":1,"suggested":[2]},"currencies":["BTC"]}]"""
            : """[{"name":"stripe","order_options":{"minimum":0.5,"suggested":[1]},"currencies":["USD"]},{"name":"paypal","order_options":{"minimum":0.5,"suggested":[1]},"currencies":["USD"]},{"name":"Stripe","order_options":{"minimum":0.5,"suggested":[1]},"currencies":["USD"]}]""";
        return Task.FromResult(Response(HttpStatusCode.OK, json));
    }), timeouts);

    var gateways = await controller.GetPaymentGatewaysAsync();
    Equal(3, gateways.Count);
    Equal(CoinGatePaymentGatewayAdapter.CanonicalGatewayName, gateways[0].Name);
    Equal(2, requestCount);

    var unsupported = new PaymentGateway
    {
        Name = "stripe",
        Currencies = ["USD"],
    };
    await ThrowsAsync<InvalidOperationException>(() => controller.CreatePaymentOrderAsync(
        "identity", unsupported, "2.00", "USD", "US", "CA"));
    Equal(2, requestCount);
}

static async Task AmbiguousPaymentPostReconcilesWithoutDuplicate()
{
    var root = TemporaryRoot();
    var postCount = 0;
    var listCount = 0;
    try
    {
        var timeouts = TestTimeouts(ordinaryMs: 30, discoveryMs: 80, connectMs: 100);
        await using var controller = Controller(new FakeHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.EndsWith("/balance/refresh", StringComparison.Ordinal))
                return Response(HttpStatusCode.OK, "{\"balance_tokens\":{\"wei\":\"100\",\"ether\":\"0\",\"human\":\"0\"}}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/payment-order", StringComparison.Ordinal))
            {
                listCount++;
                return listCount == 1
                    ? Response(HttpStatusCode.OK, "[]")
                    : Response(HttpStatusCode.OK, StripeOrders("order-reconciled", "new"));
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/stripe/payment-order", StringComparison.Ordinal))
            {
                postCount++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response(HttpStatusCode.NotFound, "{}");
        }), timeouts, bundleRoot: root);

        var created = await controller.CreatePaymentOrderAsync(
            "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StripeGateway(), "1.00", "USD", "US", "CA");
        Equal("order-reconciled", created.Order.Id);
        Equal(1, postCount);
        Equal(2, listCount);
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task SingleActivePaymentOrderIsEnforced()
{
    var root = TemporaryRoot();
    var postCount = 0;
    try
    {
        var timeouts = TestTimeouts(ordinaryMs: 80, discoveryMs: 100, connectMs: 120);
        await using var controller = Controller(new FakeHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.EndsWith("/balance/refresh", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.OK, "{\"balance_tokens\":{\"wei\":\"100\",\"ether\":\"0\",\"human\":\"0\"}}"));
            if (request.Method == HttpMethod.Get && path.EndsWith("/payment-order", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.OK, "[]"));
            if (request.Method == HttpMethod.Post && path.EndsWith("/stripe/payment-order", StringComparison.Ordinal))
            {
                postCount++;
                return Task.FromResult(Response(HttpStatusCode.OK, StripeOrder("order-one", "new")));
            }
            return Task.FromResult(Response(HttpStatusCode.NotFound, "{}"));
        }), timeouts, bundleRoot: root);

        await controller.CreatePaymentOrderAsync("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StripeGateway(), "1.00", "USD", "US", "CA");
        await ThrowsAsync<InvalidOperationException>(() =>
            controller.CreatePaymentOrderAsync("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StripeGateway(), "1.00", "USD", "US", "CA"));
        Equal(1, postCount);
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task PaymentPollSeparatesPaidAndBalance()
{
    var root = TemporaryRoot();
    try
    {
        var created = DateTimeOffset.UtcNow;
        new PaymentJournalStore(root).Save(new PaymentJournalEntry(
            "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "stripe", "order-one", "1.00", "USD", "USD", "10", "100", created, created, "confirming"));
        var timeouts = TestTimeouts(ordinaryMs: 80, discoveryMs: 100, connectMs: 120);
        await using var controller = Controller(new FakeHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/payment-order/order-one", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.OK, StripeOrder("order-one", "paid")));
            if (request.Method == HttpMethod.Put && path.EndsWith("/balance/refresh", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.OK, "{\"balance_tokens\":{\"wei\":\"100\",\"ether\":\"0\",\"human\":\"0\"}}"));
            return Task.FromResult(Response(HttpStatusCode.NotFound, "{}"));
        }), timeouts, bundleRoot: root);

        var snapshot = await controller.PollPaymentOrderAsync();
        True(snapshot.IsPaid);
        True(!snapshot.BalanceIncreased);
        True(!snapshot.CreditedSuccess);
    }
    finally { Directory.Delete(root, recursive: true); }
}

static async Task ProviderDiscoveryMayOutliveOrdinaryBudget()
{
    var timeouts = TestTimeouts(ordinaryMs: 25, discoveryMs: 130, connectMs: 150);
    await using var controller = Controller(new FakeHandler(async (_, token) =>
    {
        await Task.Delay(65, token);
        return Response(HttpStatusCode.OK, "{\"proposals\":[]}");
    }), timeouts);

    var providers = await controller.GetProvidersAsync();
    Equal(0, providers.Count);
}

static async Task OrdinaryDeadlineIsEnforced()
{
    var timeouts = TestTimeouts(ordinaryMs: 30, discoveryMs: 100, connectMs: 150);
    await using var controller = Controller(new FakeHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    var error = await ThrowsAsync<BackendOperationTimeoutException>(() =>
        controller.UnlockIdentityAsync("identity", "passphrase"));
    Equal(BackendOperation.IdentityUnlock, error.Operation);
    Equal(timeouts.Ordinary, error.Budget);
}

static async Task CallerCancellationDiffersFromTimeout()
{
    var timeouts = TestTimeouts(ordinaryMs: 90, discoveryMs: 120, connectMs: 150);
    await using var controller = Controller(new FakeHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    var canceled = await ThrowsAsync<BackendCallerCanceledException>(() =>
        controller.UnlockIdentityAsync("identity", "passphrase", caller.Token));
    Equal(BackendOperation.IdentityUnlock, canceled.Operation);

    var timeout = await ThrowsAsync<BackendOperationTimeoutException>(() =>
        controller.UnlockIdentityAsync("identity", "passphrase"));
    Equal(BackendOperation.IdentityUnlock, timeout.Operation);
}

static async Task CanceledConnectRequiresReconciliation()
{
    var putCount = 0;
    var getCount = 0;
    var timeouts = TestTimeouts(ordinaryMs: 80, discoveryMs: 120, connectMs: 150);
    await using var controller = Controller(new FakeHandler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/connection")
        {
            putCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/connection")
        {
            getCount++;
            return Response(HttpStatusCode.OK, "{\"status\":\"CONNECTING\"}");
        }
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    await ThrowsAsync<BackendCallerCanceledException>(() =>
        controller.ConnectAsync("identity", "passphrase", Provider(), caller.Token));
    True(controller.IsConnectOutcomeIndeterminate);

    await ThrowsAsync<BackendConnectionStateException>(() =>
        controller.ConnectAsync("identity", "passphrase", Provider()));
    Equal(1, putCount);
    Equal(1, getCount);
}

static async Task ReadinessUsesAbsoluteDeadline()
{
    var timeouts = new BackendTimeouts(
        TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(70),
        TimeSpan.FromMilliseconds(30));
    await using var controller = Controller(new FakeHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    var stopwatch = Stopwatch.StartNew();
    var error = await ThrowsAsync<BackendOperationTimeoutException>(() =>
        controller.WaitUntilReadyAsync(CancellationToken.None));
    stopwatch.Stop();
    Equal(BackendOperation.BackendReadiness, error.Operation);
    True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(55), "Readiness ended before its absolute budget.");
    True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(180),
        $"Readiness overran its 70 ms absolute deadline: {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
}

static async Task DiagnosticsAreOperationLabeledAndSecretFree()
{
    const string identity = "identity-secret-7346";
    const string passphrase = "passphrase-secret-9128";
    var logs = new List<string>();
    var timeouts = TestTimeouts(ordinaryMs: 25, discoveryMs: 80, connectMs: 100);
    await using var controller = Controller(new FakeHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);
    controller.Log += logs.Add;

    var error = await ThrowsAsync<BackendOperationTimeoutException>(() =>
        controller.UnlockIdentityAsync(identity, passphrase));
    var presentation = BackendErrorTranslator.ToUserError(error);
    True(presentation.Message.Contains("Identity unlock", StringComparison.Ordinal));
    True(!presentation.Message.Contains("internet", StringComparison.OrdinalIgnoreCase));
    var diagnostic = string.Join("\n", logs);
    True(diagnostic.Contains("operation=IdentityUnlock", StringComparison.Ordinal));
    True(diagnostic.Contains("route=identities/{identity}/unlock", StringComparison.Ordinal));
    True(!diagnostic.Contains(identity, StringComparison.Ordinal));
    True(!diagnostic.Contains(passphrase, StringComparison.Ordinal));
}

static async Task ConnectTimeoutReconcilesConnecting()
{
    var putCount = 0;
    var getCount = 0;
    var timeouts = TestTimeouts(ordinaryMs: 35, discoveryMs: 100, connectMs: 45);
    await using var controller = Controller(new FakeHandler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/connection")
        {
            putCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/connection")
        {
            getCount++;
            return Response(HttpStatusCode.OK, "{\"status\":\"CONNECTING\"}");
        }
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    var first = await ThrowsAsync<BackendConnectionStateException>(() =>
        controller.ConnectAsync("identity", "passphrase", Provider()));
    Equal("CONNECTING", first.SanitizedState);
    True(controller.IsConnectOutcomeIndeterminate);

    var second = await ThrowsAsync<BackendConnectionStateException>(() =>
        controller.ConnectAsync("identity", "passphrase", Provider()));
    Equal("CONNECTING", second.SanitizedState);
    Equal(1, putCount);
    Equal(2, getCount);
}

static async Task ConnectTimeoutReconcilesConnected()
{
    var verifierCalls = 0;
    var timeouts = TestTimeouts(ordinaryMs: 35, discoveryMs: 100, connectMs: 45);
    await using var controller = Controller(new FakeHandler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/connection")
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/connection")
            return Response(HttpStatusCode.OK, "{\"status\":\"CONNECTED\"}");
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts, () => { verifierCalls++; return true; });

    await controller.ConnectAsync("identity", "passphrase", Provider());
    Equal(1, verifierCalls);
    True(!controller.IsConnectOutcomeIndeterminate);
}

static async Task ConnectedRetryDoesNotDuplicatePut()
{
    var putCount = 0;
    var unlockCount = 0;
    var connectionGetCount = 0;
    var timeouts = TestTimeouts(ordinaryMs: 35, discoveryMs: 100, connectMs: 45);
    await using var controller = Controller(new FakeHandler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/connection")
        {
            putCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith("/unlock", StringComparison.Ordinal))
            unlockCount++;
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/connection")
        {
            connectionGetCount++;
            var state = connectionGetCount == 1 ? "CONNECTING" : "CONNECTED";
            return Response(HttpStatusCode.OK, $"{{\"status\":\"{state}\"}}");
        }
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts, () => true);

    await ThrowsAsync<BackendConnectionStateException>(() =>
        controller.ConnectAsync("identity", "passphrase", Provider()));
    await controller.ConnectAsync("identity", "passphrase", Provider());

    Equal(1, putCount);
    Equal(1, unlockCount);
    Equal(2, connectionGetCount);
    True(!controller.IsConnectOutcomeIndeterminate);
}

static async Task ConnectTimeoutReconcilesFailedState()
{
    const string providerSecret = "provider-secret-4419";
    var timeouts = TestTimeouts(ordinaryMs: 35, discoveryMs: 100, connectMs: 45);
    await using var controller = Controller(new FakeHandler(async (request, token) =>
    {
        if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/connection")
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/connection")
            return Response(HttpStatusCode.OK, "{\"status\":\"FAILED\"}");
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    var error = await ThrowsAsync<BackendConnectionStateException>(() =>
        controller.ConnectAsync("identity", "passphrase", Provider(providerSecret)));
    Equal("FAILED", error.SanitizedState);
    True(error.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    True(!error.Message.Contains(providerSecret, StringComparison.Ordinal));
    True(!controller.IsConnectOutcomeIndeterminate);
}

static async Task ProxyConnectionOperationsUseProxyPortId()
{
    var putCount = 0;
    var statusCount = 0;
    var deleteCount = 0;
    var observedProxyPort = false;
    var timeouts = TestTimeouts(ordinaryMs: 80, discoveryMs: 120, connectMs: 150);
    await using var controller = Controller(new FakeHandler(async (request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Put && path == "/connection")
        {
            putCount++;
            Equal(string.Empty, request.RequestUri.Query);
            var body = await request.Content!.ReadAsStringAsync();
            observedProxyPort = body.Contains("\"proxy_port\":4449", StringComparison.Ordinal);
            return Response(HttpStatusCode.OK, "{}");
        }
        if (request.Method == HttpMethod.Get && path == "/connection")
        {
            statusCount++;
            Equal("?id=4449", request.RequestUri.Query);
            return Response(HttpStatusCode.OK, "{\"status\":\"NOT_CONNECTED\"}");
        }
        if (request.Method == HttpMethod.Delete && path == "/connection")
        {
            deleteCount++;
            Equal("?id=4449", request.RequestUri.Query);
            return Response(HttpStatusCode.OK, "{}");
        }
        if (request.Method == HttpMethod.Get && path == "/healthcheck")
            return Response(HttpStatusCode.OK, "{}");
        if (request.Method == HttpMethod.Get && path == "/identities")
            return Response(HttpStatusCode.OK, "{\"identities\":[]}");
        if (request.Method == HttpMethod.Get && path == "/terms")
            return Response(HttpStatusCode.OK, "{}");
        return Response(HttpStatusCode.OK, "{}");
    }), timeouts);

    await controller.ConnectAsync("identity", "passphrase", Provider());
    await controller.GetSnapshotAsync();
    await controller.DisconnectAsync();

    Equal(1, putCount);
    Equal(1, statusCount);
    Equal(1, deleteCount);
    True(observedProxyPort);
}

static async Task MalformedAndHttpErrorsRemainDistinct()
{
    var timeouts = TestTimeouts(ordinaryMs: 40, discoveryMs: 80, connectMs: 100);
    await using (var malformed = Controller(new FakeHandler((_, _) =>
        Task.FromResult(Response(HttpStatusCode.OK, "not-json"))), timeouts))
    {
        var error = await ThrowsAsync<BackendMalformedResponseException>(() => malformed.GetProvidersAsync());
        Equal(BackendOperation.ProviderDiscovery, error.Operation);
    }

    const string secret = "response-secret-5082";
    await using var rejected = Controller(new FakeHandler((_, _) => Task.FromResult(Response(
        HttpStatusCode.BadRequest, $"{{\"code\":\"err_connect\",\"message\":\"{secret}\"}}"))), timeouts);
    var backend = await ThrowsAsync<BackendApiException>(() => rejected.GetProvidersAsync());
    Equal("err_connect", backend.Code!);
    True(!backend.DiagnosticMessage.Contains(secret, StringComparison.Ordinal));
    True(backend.DiagnosticMessage.Contains("Provider discovery", StringComparison.Ordinal));
    Equal("The selected provider could not be reached. Refresh providers and try another one.", backend.Message);

    const string secretCode = "identity-secret-3391";
    await using var unknown = Controller(new FakeHandler((_, _) => Task.FromResult(Response(
        HttpStatusCode.BadRequest, $"{{\"code\":\"{secretCode}\",\"message\":\"rejected\"}}"))), timeouts);
    var unknownError = await ThrowsAsync<BackendApiException>(() => unknown.GetProvidersAsync());
    True(!unknownError.Message.Contains(secretCode, StringComparison.Ordinal));
    True(!unknownError.DiagnosticMessage.Contains(secretCode, StringComparison.Ordinal));
}

static BackendController Controller(
    HttpMessageHandler handler,
    BackendTimeouts timeouts,
    Func<bool>? connectedVerifier = null,
    string? bundleRoot = null) =>
    new(new AppOptions(bundleRoot ?? ".", "browser.exe", "myst.exe", "profile", "about:blank", [], false, true),
        handler, timeouts, connectedStateVerifier: connectedVerifier);

static string TemporaryRoot()
{
    var root = Path.Combine(Path.GetTempPath(), "privacy-browser-backend-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

static PaymentGateway StripeGateway() => new()
{
    Name = "stripe",
    Currencies = ["USD"],
    OrderOptions = new PaymentOrderOptions { Minimum = 0.50m, Suggested = [1.00m] },
};

static string StripeOrders(string id, string status) => $"[{StripeOrder(id, status)}]";

static string StripeOrder(string id, string status) =>
    "{\"id\":\"" + id + "\",\"status\":\"" + status +
    "\",\"identity\":\"0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"gateway_name\":\"stripe\"," +
    "\"receive_myst\":\"10\",\"pay_amount\":\"1.00\",\"pay_currency\":\"USD\"," +
    "\"public_gateway_data\":{\"checkout_url\":\"https://checkout.stripe.com/c/pay/test\"}}";

static BackendTimeouts TestTimeouts(int ordinaryMs, int discoveryMs, int connectMs) => new(
    TimeSpan.FromMilliseconds(20),
    TimeSpan.FromMilliseconds(ordinaryMs),
    TimeSpan.FromMilliseconds(discoveryMs),
    TimeSpan.FromMilliseconds(connectMs),
    TimeSpan.FromMilliseconds(30),
    TimeSpan.FromMilliseconds(100),
    TimeSpan.FromMilliseconds(10));

static ProviderProposal Provider(string id = "provider") => new() { ProviderId = id, ServiceType = "wireguard" };

static HttpResponseMessage Response(HttpStatusCode status, string content) => new(status)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json"),
};

static async Task<T> ThrowsAsync<T>(Func<Task> operation) where T : Exception
{
    try
    {
        await operation();
    }
    catch (T expected)
    {
        return expected;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void True(bool condition, string message = "Condition was false.")
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}

sealed class FakeHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => send(request, cancellationToken);
}
