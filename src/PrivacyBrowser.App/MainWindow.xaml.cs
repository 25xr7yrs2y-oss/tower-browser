using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PrivacyBrowser.App;

public partial class MainWindow : Window
{
    private readonly BackendController _backend;
    private readonly BrowserLauncher _browser;
    private readonly AppOptions _options;
    private readonly UserStateStore _stateStore;
    private readonly DispatcherTimer _timer;
    private readonly OperationFeedbackStore _feedbackStore = new();
    private BackendSnapshot _snapshot = BackendSnapshot.Offline("The native controller is starting.");
    private BrowserReadiness _browserReadiness = new(BrowserReadinessState.Checking, "Checking browser readiness…", []);
    private IReadOnlyList<ProviderProposal> _providers = [];
    private string? _selectedIdentityId;
    private bool _refreshing;
    private bool _closing;
    private bool _renderingIdentities;
    private bool _bundleValidated;
    private string _lastIssueSignature = "";
    private AppPage _currentPage;

    public MainWindow(AppOptions options)
    {
        InitializeComponent();
        _options = options;
        _stateStore = new UserStateStore(options.BundleRoot);
        _selectedIdentityId = _stateStore.LoadSelectedIdentityId();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.9";
        Title = $"Privacy Browser {version}";
        _backend = new BackendController(options);
        _browser = new BrowserLauncher(options, _backend);
        _backend.Log += message => Dispatcher.BeginInvoke(() =>
        {
            var friendly = BackendErrorTranslator.ToActivityMessage(message);
            if (friendly is not null) AppendActivity(friendly);
        });
        _backend.LifecycleChanged += state => Dispatcher.BeginInvoke(() =>
        {
            BackendLifecycleText.Text = FormatLifecycleState(state);
            UpdateControls();
        });
        _browser.BrowserExited += processId => Dispatcher.BeginInvoke(() =>
        {
            AppendActivity($"Privacy browser process {processId} exited.");
            RenderBrowserReadiness();
            UpdateControls();
        });
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshSnapshotAsync(showErrors: false);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        NavigateTo(AppPage.Home, focusHeading: false);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(OperationFeedbackArea.Home, "Starting the private backend…", "Backend is ready.", async () =>
        {
            await BundleValidator.ValidateAsync(_options);
            _bundleValidated = true;
            await _backend.StartAsync();
            await RefreshSnapshotAsync(showErrors: true);
        });
        _timer.Start();

        if (_snapshot.NodeUp)
        {
            await RunOperationAsync(OperationFeedbackArea.Connection, "Discovering WireGuard providers…", "Provider list is ready.", RefreshProvidersAsync);
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        if (_browser.IsBrowserRunning && MessageBox.Show(
                "The isolated browser is still running. Closing the controller will stop its provider connection and the browser will fail closed. Close anyway?",
                "Privacy Browser", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _closing = true;
        _timer.Stop();
        IsEnabled = false;
        FooterStatusText.Text = "Stopping the owned backend…";
        await _backend.DisposeAsync();
        Close();
    }

    private void ControlsButton_Click(object sender, RoutedEventArgs e) => NavigateTo(AppPage.Connection);

    private void NavigateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string target } &&
            Enum.TryParse<AppPage>(target, ignoreCase: false, out var page))
        {
            NavigateTo(page);
        }
    }

    private void NavigateTo(AppPage page, bool focusHeading = true)
    {
        var pages = new[]
        {
            (Page: AppPage.Home, View: HomePage, Button: HomeNavigationButton),
            (Page: AppPage.Identity, View: IdentityPage, Button: IdentityNavigationButton),
            (Page: AppPage.Wallet, View: WalletPage, Button: WalletNavigationButton),
            (Page: AppPage.Connection, View: ConnectionPage, Button: ConnectionNavigationButton),
            (Page: AppPage.BrowserAndDiagnostics, View: BrowserAndDiagnosticsPage, Button: BrowserNavigationButton),
        };

        foreach (var item in pages)
        {
            var selected = item.Page == page;
            item.View.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            item.Button.Style = (Style)FindResource(selected ? "SelectedNavigationButton" : "NavigationButton");
        }

        _currentPage = page;
        (CurrentPageTitle.Text, CurrentPageDescription.Text) = page switch
        {
            AppPage.Identity => ("Identity", "Select, create, import, unlock, and register your Mysterium identity."),
            AppPage.Wallet => ("Wallet", "Review the selected identity's balance and create a validated top-up order."),
            AppPage.Connection => ("Connection", "Discover a WireGuard provider and manage the private connection."),
            AppPage.BrowserAndDiagnostics => ("Browser & diagnostics", "Verify browser readiness and inspect the app-owned backend."),
            _ => ("Home", "A concise summary of your privacy session."),
        };

        RenderOperationFeedback();
        if (focusHeading) CurrentPageTitle.Focus();
    }

    private async void CreateIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        string? passphrase = PromptForPassphrase(
            "Protect a new identity",
            "Create a passphrase for this identity. It is required for registration and provider connections and is not stored by Privacy Browser.",
            "Create identity", requireConfirmation: true, minimumLength: 12);
        if (passphrase is null) return;
        await RunOperationAsync(OperationFeedbackArea.Identity, "Creating a local identity…", "Identity created and selected.", async () =>
        {
            var identity = await _backend.CreateIdentityAsync(passphrase);
            SelectIdentity(identity.Id);
            AppendActivity("A local Mysterium identity was created.");
            await RefreshSnapshotAsync(true);
        });
        passphrase = null;
    }

    private async void ImportIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Import encrypted Mysterium identity",
            Filter = "Mysterium identity (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true) return;
        var info = new FileInfo(picker.FileName);
        if (info.Length <= 0 || info.Length > 1024 * 1024)
        {
            PublishFeedback(OperationFeedbackArea.Identity, OperationFeedbackKind.RetryableFailure,
                "Choose a non-empty identity file smaller than 1 MB.");
            return;
        }

        string? passphrase = PromptForPassphrase(
            "Unlock imported identity",
            "Enter the passphrase that protects this encrypted key file. The imported key remains protected with the same passphrase.",
            "Import identity", requireConfirmation: true, minimumLength: 1);
        if (passphrase is null) return;
        await RunOperationAsync(OperationFeedbackArea.Identity, "Importing the encrypted identity…", "Identity imported and selected.", async () =>
        {
            var encryptedKey = await File.ReadAllBytesAsync(picker.FileName);
            try
            {
                var identity = await _backend.ImportIdentityAsync(encryptedKey, passphrase);
                SelectIdentity(identity.Id);
                AppendActivity($"Imported and selected identity {ShortId(identity.Id)}.");
                await RefreshSnapshotAsync(true);
            }
            finally
            {
                Array.Clear(encryptedKey);
            }
        });
        passphrase = null;
    }

    private async void UnlockIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot.Identity is null) return;
        string? passphrase = PromptForExistingPassphrase(_snapshot.Identity.Id, "Unlock identity");
        if (passphrase is null) return;
        await RunOperationAsync(OperationFeedbackArea.Identity, "Unlocking the selected identity…", "Identity unlocked for this backend session.", async () =>
        {
            await _backend.UnlockIdentityAsync(_snapshot.Identity.Id, passphrase);
            AppendActivity($"Unlocked identity {ShortId(_snapshot.Identity.Id)} for this backend session.");
        });
        passphrase = null;
    }

    private async void AcceptTermsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (AcceptTermsCheckBox.IsChecked != true || _snapshot.Terms is null)
        {
            AcceptTermsCheckBox.IsChecked = _snapshot.Terms?.IsCurrent == true;
            return;
        }

        await RunOperationAsync(OperationFeedbackArea.Identity, "Saving terms acceptance…", "Consumer terms accepted.", async () =>
        {
            await _backend.AcceptConsumerTermsAsync(_snapshot.Terms.CurrentVersion);
            AppendActivity($"Accepted Mysterium consumer terms {_snapshot.Terms.CurrentVersion}.");
            await RefreshSnapshotAsync(true);
        });
        AcceptTermsCheckBox.IsChecked = _snapshot.Terms?.IsCurrent == true;
    }

    private async void RegisterIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot.Identity is null) return;
        string? passphrase = PromptForExistingPassphrase(_snapshot.Identity.Id, "Register identity");
        if (passphrase is null) return;
        await RunOperationAsync(OperationFeedbackArea.Identity, "Requesting identity registration…", "Registration requested. Refresh status as it progresses.", async () =>
        {
            await _backend.RegisterIdentityAsync(_snapshot.Identity.Id, passphrase);
            AppendActivity("Identity registration started. A wallet top-up may be required to finish registration.");
            await RefreshSnapshotAsync(true);
        });
        passphrase = null;
    }

    private async void RefreshIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(OperationFeedbackArea.Identity, "Refreshing identity status…", "Identity status refreshed.", async () =>
        {
            await RefreshSnapshotAsync(showErrors: true);
            var issue = _snapshot.Issues.FirstOrDefault(item =>
                item.Area.Equals("identity", StringComparison.OrdinalIgnoreCase) ||
                item.Area.Equals("identity status", StringComparison.OrdinalIgnoreCase));
            if (issue is not null) throw new InvalidOperationException(issue.Message);
        });
    }

    private async void RefreshBalanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot.Identity is null) return;
        await RunOperationAsync(OperationFeedbackArea.Wallet, "Refreshing MYST balance…", "Wallet balance refreshed.", async () =>
        {
            var balance = await _backend.RefreshBalanceAsync(_snapshot.Identity.Id);
            AppendActivity($"Wallet balance refreshed: {balance.BalanceTokens.Display} MYST.");
            await RefreshSnapshotAsync(true);
        });
    }

    private async void TopUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot.Identity is null) return;
        var window = new TopUpWindow(_backend, _snapshot.Identity.Id) { Owner = this };
        window.ShowDialog();
        if (window.CreatedOrder is not null)
        {
            AppendActivity($"Payment order {window.CreatedOrder.Id} created; status {window.CreatedOrder.Status}.");
            PublishFeedback(OperationFeedbackArea.Wallet, OperationFeedbackKind.Success,
                "Top-up order created. Follow the payment window status until it completes.");
            await RefreshSnapshotAsync(showErrors: false);
        }
    }

    private async void RefreshProvidersButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(OperationFeedbackArea.Connection, "Discovering WireGuard providers…", "Provider list refreshed.", RefreshProvidersAsync);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot.Identity is null || ProviderComboBox.SelectedItem is not ProviderProposal provider) return;
        string? passphrase = PromptForExistingPassphrase(_snapshot.Identity.Id, "Connect to provider");
        if (passphrase is null) return;
        await RunOperationAsync(
            OperationFeedbackArea.Connection,
            $"Connecting to {provider.DisplayName}…",
            () => _browserReadiness.State == BrowserReadinessState.Ready
                ? "Provider connected. Browser readiness verified."
                : "Provider connected. Open Browser & diagnostics to complete browser readiness checks.",
            async () =>
        {
            AppendActivity($"Connecting to {provider.DisplayName}.");
            await _backend.ConnectAsync(_snapshot.Identity.Id, passphrase, provider);
            await RefreshSnapshotAsync(true);
        });
        passphrase = null;
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(OperationFeedbackArea.Connection, "Disconnecting from the provider…", "Provider disconnected.", async () =>
        {
            await _backend.DisconnectAsync();
            AppendActivity("Provider connection was disconnected.");
            await RefreshSnapshotAsync(true);
        });
    }

    private void LaunchBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process process = _browser.Launch(_snapshot);
            AppendActivity($"Privacy browser started as process {process.Id}.");
            PublishFeedback(OperationFeedbackArea.BrowserAndDiagnostics, OperationFeedbackKind.Success,
                "Privacy browser launched.");
            RenderBrowserReadiness();
            UpdateControls();
        }
        catch (Exception ex)
        {
            PublishOperationError(OperationFeedbackArea.BrowserAndDiagnostics, ex);
        }
    }

    private void ProviderComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RenderSelectedProvider();
        UpdateControls();
    }

    private void RenderIdentitySelector()
    {
        _renderingIdentities = true;
        try
        {
            IdentityComboBox.ItemsSource = _snapshot.Identities;
            IdentityComboBox.SelectedItem = _snapshot.Identity;
        }
        finally
        {
            _renderingIdentities = false;
        }
    }

    private void RenderBrowserReadiness()
    {
        _browserReadiness = _browser.EvaluateReadiness(_snapshot);
        BrowserReadinessText.Text = _browserReadiness.Summary;
        switch (_browserReadiness.State)
        {
            case BrowserReadinessState.Ready:
                BrowserReadinessIcon.Background = Brush("SuccessSoft");
                BrowserReadinessGlyph.Foreground = Brush("Success");
                BrowserReadinessGlyph.Text = "✓";
                break;
            case BrowserReadinessState.Error:
                BrowserReadinessIcon.Background = Brush("DangerSoft");
                BrowserReadinessGlyph.Foreground = Brush("Danger");
                BrowserReadinessGlyph.Text = "!";
                break;
            case BrowserReadinessState.BrowserRunning:
                BrowserReadinessIcon.Background = Brush("PrimarySoft");
                BrowserReadinessGlyph.Foreground = Brush("Primary");
                BrowserReadinessGlyph.Text = "●";
                break;
            default:
                BrowserReadinessIcon.Background = new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF0));
                BrowserReadinessGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x68, 0x73, 0x6F));
                BrowserReadinessGlyph.Text = "…";
                break;
        }
    }

    private void ApplyProviderFilter(string? selectedId = null)
    {
        if (ProviderSearchTextBox is null || ProviderComboBox is null) return;
        selectedId ??= (ProviderComboBox.SelectedItem as ProviderProposal)?.ProviderId;
        var query = ProviderSearchTextBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _providers
            : _providers.Where(provider => new[]
                {
                    provider.ProviderId,
                    provider.Location.Country,
                    provider.Location.City,
                    provider.Location.Isp,
                }.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        ProviderComboBox.ItemsSource = filtered;
        ProviderComboBox.SelectedItem = filtered.FirstOrDefault(provider =>
            provider.ProviderId.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? filtered.FirstOrDefault();
        ProviderCountText.Text = string.IsNullOrWhiteSpace(query)
            ? (_providers.Count == 1 ? "1 provider" : $"{_providers.Count} providers")
            : $"{filtered.Count} of {_providers.Count}";
    }

    private void IdentityComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_renderingIdentities || IdentityComboBox.SelectedItem is not IdentityDetails identity) return;
        SelectIdentity(identity.Id);
        _snapshot = _snapshot with { SelectedIdentityId = identity.Id };
        AppendActivity($"Selected identity {ShortId(identity.Id)}.");
        PublishFeedback(OperationFeedbackArea.Identity, OperationFeedbackKind.Success,
            "Identity selected for this session.");
        RenderSnapshot();
    }

    private void ProviderSearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyProviderFilter();

    private void ClearActivityButton_Click(object sender, RoutedEventArgs e) => ActivityText.Clear();

    private void CopyActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ActivityText.Text)) Clipboard.SetText(ActivityText.Text);
    }

    private async void RestartBackendButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(OperationFeedbackArea.BrowserAndDiagnostics, "Restarting the private backend…", "Backend restarted.", async () =>
        {
            await _backend.RestartAsync();
            await RefreshSnapshotAsync(showErrors: true);
            await RefreshProvidersAsync();
        });
    }

    private async Task RefreshProvidersAsync()
    {
        var selectedId = (ProviderComboBox.SelectedItem as ProviderProposal)?.ProviderId;
        _providers = await _backend.GetProvidersAsync();
        ApplyProviderFilter(selectedId);
        AppendActivity(_providers.Count == 0
            ? "No WireGuard providers were returned. Check network access and refresh again."
            : $"Loaded {_providers.Count} WireGuard providers. Use search to filter the list.");
        RenderSelectedProvider();
        UpdateControls();
    }

    private async Task RefreshSnapshotAsync(bool showErrors)
    {
        if (_refreshing || _closing) return;
        _refreshing = true;
        try
        {
            _snapshot = await _backend.GetSnapshotAsync(_selectedIdentityId);
            if (_snapshot.SelectedIdentityId is null)
            {
                var preferred = _snapshot.Identities.FirstOrDefault(identity =>
                    identity.Id.Equals(_snapshot.Connection.ConsumerId, StringComparison.OrdinalIgnoreCase));
                if (preferred is null && _snapshot.Identities.Count == 1) preferred = _snapshot.Identities[0];
                if (preferred is not null)
                {
                    SelectIdentity(preferred.Id);
                    _snapshot = _snapshot with { SelectedIdentityId = preferred.Id };
                }
            }
            RenderSnapshot();
        }
        catch
        {
            FooterStatusText.Text = "Backend status is temporarily unavailable.";
            if (showErrors) throw;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderSnapshot()
    {
        var connected = _snapshot.IsConnected;
        var statusUnavailable = _snapshot.Connection.Status.Equals("STATUS_UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
        RenderBrowserReadiness();

        if (!_snapshot.NodeUp)
        {
            ConnectionStatusText.Text = "Backend offline";
            ConnectionDetailText.Text = "The native controller cannot reach the Myst backend. Open Browser & diagnostics for details.";
            ConnectionStatusDot.Fill = Brush("Danger");
            MainBackendText.Text = "Offline";
            MainBackendDetailText.Text = "Control service unavailable";
        }
        else if (connected)
        {
            var privacyVerified = _browserReadiness.State is BrowserReadinessState.Ready or BrowserReadinessState.BrowserRunning;
            ConnectionStatusText.Text = privacyVerified ? "Privacy is active" : "Privacy checks incomplete";
            ConnectionDetailText.Text = privacyVerified
                ? "The app-owned browser proxy and locked policy are verified."
                : _browserReadiness.Summary;
            ConnectionStatusDot.Fill = privacyVerified ? Brush("Success") : Brush("Warning");
            MainBackendText.Text = "Online";
            MainBackendDetailText.Text = "Myst control is ready";
        }
        else if (_snapshot.IsConnecting || _backend.IsConnectOutcomeIndeterminate)
        {
            ConnectionStatusText.Text = "Connecting";
            ConnectionDetailText.Text = "The backend is still negotiating with the provider. Wait for status to settle before trying again.";
            ConnectionStatusDot.Fill = Brush("Warning");
            MainBackendText.Text = "Connecting";
            MainBackendDetailText.Text = "Provider negotiation in progress";
        }
        else if (statusUnavailable)
        {
            ConnectionStatusText.Text = "Status unavailable";
            ConnectionDetailText.Text = "The backend is online, but one status service did not respond. Retry from the relevant page.";
            ConnectionStatusDot.Fill = Brush("Warning");
            MainBackendText.Text = "Partially online";
            MainBackendDetailText.Text = "Some data unavailable";
        }
        else
        {
            ConnectionStatusText.Text = "Not connected";
            ConnectionDetailText.Text = "Complete the identity and wallet steps, then choose a provider.";
            ConnectionStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0x92));
            MainBackendText.Text = "Online";
            MainBackendDetailText.Text = "Waiting for connection";
        }

        var activeProvider = _snapshot.Connection.Proposal;
        ConnectedProviderText.Text = activeProvider is null
            ? "No active provider"
            : $"Provider: {activeProvider.DisplayName}";

        RenderIdentitySelector();
        if (_snapshot.Identity is null)
        {
            var hasIdentities = _snapshot.Identities.Count > 0;
            IdentityAddressText.Text = hasIdentities ? "Choose an identity above" : "No identity created";
            IdentityStatusBadgeText.Text = hasIdentities ? "Select one" : "Not created";
            IdentityStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xED, 0xF1, 0xF0));
            IdentityHelpText.Text = hasIdentities
                ? "Select the identity to use. Sensitive actions always apply to the explicit selection."
                : "Accept the terms, then create or import an identity to begin.";
            MainIdentityText.Text = hasIdentities ? "Selection required" : "Not created";
            MainIdentityDetailText.Text = hasIdentities ? $"{_snapshot.Identities.Count} available" : "Identity required";
            WalletBalanceText.Text = "—";
            WalletPaymentStatusText.Text = "Create an identity to view payment status.";
            MainBalanceText.Text = "— MYST";
            MainPaymentText.Text = "Identity required";
        }
        else
        {
            var identity = _snapshot.Identity;
            IdentityAddressText.Text = identity.Id;
            IdentityStatusBadgeText.Text = FormatRegistrationStatus(identity.RegistrationStatus);
            MainIdentityText.Text = FormatRegistrationStatus(identity.RegistrationStatus);
            MainIdentityDetailText.Text = ShortId(identity.Id);
            IdentityHelpText.Text = IdentityGuidance(identity);
            SetIdentityBadge(identity);

            var balance = identity.BalanceTokens.Display;
            WalletBalanceText.Text = $"{balance} MYST";
            MainBalanceText.Text = $"{balance} MYST";
            var paymentStatus = PaymentGuidance(identity);
            WalletPaymentStatusText.Text = paymentStatus;
            MainPaymentText.Text = paymentStatus;
        }

        AcceptTermsCheckBox.IsChecked = _snapshot.Terms?.IsCurrent == true;
        TermsVersionText.Text = _snapshot.Terms is null
            ? "Terms status is unavailable."
            : $"Current version: {_snapshot.Terms.CurrentVersion}";

        FooterStatusText.Text = !_snapshot.NodeUp
            ? "Native UI · Waiting for backend"
            : _snapshot.Issues.Count > 0
                ? $"Native UI · Backend online · {_snapshot.Issues.Count} status item(s) need attention"
                : $"Native UI · Control 127.0.0.1:{BackendController.ControlPort} · Proxy 127.0.0.1:{BackendController.ProxyPort}";
        LastUpdatedText.Text = $"Updated {_snapshot.ObservedAt:HH:mm:ss}";

        var issueSignature = string.Join('|', _snapshot.Issues.Select(i => $"{i.Area}:{i.Message}"));
        if (issueSignature.Length > 0 && issueSignature != _lastIssueSignature)
        {
            foreach (var issue in _snapshot.Issues)
            {
                AppendActivity($"{Capitalize(issue.Area)}: {issue.Message}");
            }
        }
        _lastIssueSignature = issueSignature;

        RenderSelectedProvider();
        UpdateControls();
    }

    private void RenderSelectedProvider()
    {
        var provider = _snapshot.Connection.Proposal ?? ProviderComboBox.SelectedItem as ProviderProposal;
        if (provider is null)
        {
            ProviderDetailText.Text = "Refresh to discover available WireGuard providers.";
            MainProviderText.Text = "None";
            MainProviderDetailText.Text = "Choose on Connection";
            return;
        }

        var type = string.IsNullOrWhiteSpace(provider.Location.IpType) ? "Network type unknown" : provider.Location.IpType;
        ProviderDetailText.Text = $"{type} · {provider.PriceSummary}";
        MainProviderText.Text = provider.Location.Country.Length > 0 ? provider.Location.Country : ShortId(provider.ProviderId);
        MainProviderDetailText.Text = _snapshot.IsConnected ? "Connected" : provider.PriceSummary;
    }

    private void UpdateControls()
    {
        var identity = _snapshot.Identity;
        var hasIdentity = identity is not null;
        var termsAccepted = _snapshot.Terms?.IsCurrent == true;
        var registrationReady = identity?.IsRegistered == true;
        var paymentEligible = identity is not null && (identity.IsRegistered || identity.RegistrationInProgress);
        var registrationKnown = identity is not null &&
            !identity.RegistrationStatus.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);
        var hasProvider = ProviderComboBox.SelectedItem is ProviderProposal;
        var available = !_feedbackStore.IsBusy && _snapshot.NodeUp;

        AcceptTermsCheckBox.IsEnabled = available && !termsAccepted && _snapshot.Terms is not null;
        CreateIdentityButton.IsEnabled = available && termsAccepted && !_snapshot.IsConnected;
        ImportIdentityButton.IsEnabled = available && !_snapshot.IsConnected;
        IdentityComboBox.IsEnabled = available && !_snapshot.IsConnected && _snapshot.Identities.Count > 0;
        UnlockIdentityButton.IsEnabled = available && hasIdentity;
        RegisterIdentityButton.IsEnabled = available && termsAccepted && hasIdentity && registrationKnown &&
            !registrationReady && identity?.RegistrationInProgress != true;
        RefreshIdentityButton.IsEnabled = available;
        RefreshBalanceButton.IsEnabled = available && hasIdentity;
        TopUpButton.IsEnabled = available && paymentEligible;
        RefreshProvidersButton.IsEnabled = available;
        ProviderComboBox.IsEnabled = available && !_snapshot.IsConnected;
        ConnectButton.IsEnabled = available && termsAccepted && registrationReady && hasProvider &&
            !_snapshot.IsConnected && !_snapshot.IsConnecting && !_backend.IsConnectOutcomeIndeterminate;
        DisconnectButton.IsEnabled = !_feedbackStore.IsBusy && _snapshot.IsConnected;
        LaunchBrowserButton.IsEnabled = !_feedbackStore.IsBusy && _browserReadiness.CanLaunch;
        RestartBackendButton.IsEnabled = !_feedbackStore.IsBusy && _bundleValidated &&
            _backend.LifecycleState != BackendLifecycleState.Starting;
        BackendLifecycleText.Text = FormatLifecycleState(_backend.LifecycleState);

        ConnectionPrerequisiteText.Text = ConnectionPrerequisite(termsAccepted, hasIdentity, registrationReady, hasProvider);
    }

    private string ConnectionPrerequisite(bool termsAccepted, bool hasIdentity, bool registrationReady, bool hasProvider)
    {
        if (!_snapshot.NodeUp) return "Backend is offline.";
        if (_snapshot.IsConnected) return "Connected. Disconnect before changing provider.";
        if (_snapshot.IsConnecting || _backend.IsConnectOutcomeIndeterminate)
            return "A provider connection attempt is still being reconciled. Wait for status to settle.";
        if (!termsAccepted) return "Accept the consumer terms before connecting.";
        if (!hasIdentity) return "Create an identity before connecting.";
        if (!registrationReady) return "Register the identity before connecting.";
        if (!hasProvider) return "Choose a provider before connecting.";
        if (_snapshot.Identity?.BalanceTokens.Value <= 0) return "Ready, but the wallet shows no MYST; paid traffic may fail.";
        return "Ready to connect.";
    }

    private Task RunOperationAsync(
        OperationFeedbackArea area,
        string progressMessage,
        string successMessage,
        Func<Task> action) =>
        RunOperationAsync(area, progressMessage, () => successMessage, action);

    private async Task RunOperationAsync(
        OperationFeedbackArea area,
        string progressMessage,
        Func<string> successMessage,
        Func<Task> action)
    {
        if (_closing) return;
        using var operation = _feedbackStore.TryStart(area, progressMessage);
        if (operation is null) return;
        RenderOperationFeedback();
        FooterStatusText.Text = progressMessage;
        UpdateControls();
        try
        {
            await action();
            operation.Complete(OperationFeedbackKind.Success, successMessage());
        }
        catch (Exception ex)
        {
            CompleteOperationError(operation, ex);
        }
        finally
        {
            operation.Dispose();
            RenderOperationFeedback();
            UpdateControls();
        }
    }

    private void CompleteOperationError(OperationFeedbackSession operation, Exception exception)
    {
        var error = BackendErrorTranslator.ToUserError(exception);
        var kind = error.Kind == UserErrorKind.Retryable
            ? OperationFeedbackKind.RetryableFailure
            : OperationFeedbackKind.BlockingFailure;
        AppendActivity($"Could not complete operation: {error.Message}");
        operation.Complete(kind, error.Message);
    }

    private void PublishOperationError(OperationFeedbackArea area, Exception exception)
    {
        var error = BackendErrorTranslator.ToUserError(exception);
        var kind = error.Kind == UserErrorKind.Retryable
            ? OperationFeedbackKind.RetryableFailure
            : OperationFeedbackKind.BlockingFailure;
        AppendActivity($"Could not complete operation: {error.Message}");
        PublishFeedback(area, kind, error.Message);
    }

    private void PublishFeedback(OperationFeedbackArea area, OperationFeedbackKind kind, string message)
    {
        _feedbackStore.Publish(area, kind, message);
        RenderOperationFeedback();
    }

    private void RenderOperationFeedback()
    {
        var feedback = _feedbackStore.Get(FeedbackAreaForPage(_currentPage));
        OperationStatusBorder.Visibility = feedback.Kind == OperationFeedbackKind.Idle
            ? Visibility.Collapsed
            : Visibility.Visible;
        OperationProgressBar.Visibility = feedback.Kind == OperationFeedbackKind.Progress
            ? Visibility.Visible
            : Visibility.Collapsed;
        OperationStatusText.Text = feedback.Kind switch
        {
            OperationFeedbackKind.Progress => $"In progress: {feedback.Message}",
            OperationFeedbackKind.Success => $"Success: {feedback.Message}",
            OperationFeedbackKind.RetryableFailure => $"Retryable issue: {feedback.Message}",
            OperationFeedbackKind.BlockingFailure => $"Action required: {feedback.Message}",
            _ => "",
        };

        switch (feedback.Kind)
        {
            case OperationFeedbackKind.Success:
                OperationStatusBorder.Background = Brush("SuccessSoft");
                OperationStatusText.Foreground = Brush("Success");
                break;
            case OperationFeedbackKind.RetryableFailure:
                OperationStatusBorder.Background = Brush("WarningSoft");
                OperationStatusText.Foreground = Brush("Warning");
                break;
            case OperationFeedbackKind.BlockingFailure:
                OperationStatusBorder.Background = Brush("DangerSoft");
                OperationStatusText.Foreground = Brush("Danger");
                break;
            default:
                OperationStatusBorder.Background = Brush("PrimarySoft");
                OperationStatusText.Foreground = Brush("Primary");
                break;
        }
    }

    private static OperationFeedbackArea FeedbackAreaForPage(AppPage page) => page switch
    {
        AppPage.Identity => OperationFeedbackArea.Identity,
        AppPage.Wallet => OperationFeedbackArea.Wallet,
        AppPage.Connection => OperationFeedbackArea.Connection,
        AppPage.BrowserAndDiagnostics => OperationFeedbackArea.BrowserAndDiagnostics,
        _ => OperationFeedbackArea.Home,
    };

    private void AppendActivity(string message)
    {
        ActivityText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        ActivityText.ScrollToEnd();
    }

    private void SelectIdentity(string id)
    {
        _selectedIdentityId = id;
        if (!_stateStore.SaveSelectedIdentityId(id))
        {
            AppendActivity("The identity selection will be used for this session but could not be saved.");
        }
    }

    private string? PromptForExistingPassphrase(string identityId, string actionLabel) =>
        PromptForPassphrase(
            actionLabel,
            $"Enter the passphrase for {ShortId(identityId)}. It is sent only to the loopback Myst backend for this operation and is never saved or logged.",
            actionLabel,
            requireConfirmation: false,
            minimumLength: 1);

    private string? PromptForPassphrase(
        string heading,
        string description,
        string actionLabel,
        bool requireConfirmation,
        int minimumLength)
    {
        var window = new PassphraseWindow(heading, description, actionLabel, requireConfirmation, minimumLength)
        {
            Owner = this,
        };
        return window.ShowDialog() == true ? window.TakePassphrase() : null;
    }

    private void SetIdentityBadge(IdentityDetails identity)
    {
        if (identity.IsRegistered)
        {
            IdentityStatusBadge.Background = Brush("SuccessSoft");
            IdentityStatusBadgeText.Foreground = Brush("Success");
        }
        else if (identity.RegistrationInProgress)
        {
            IdentityStatusBadge.Background = Brush("WarningSoft");
            IdentityStatusBadgeText.Foreground = Brush("Warning");
        }
        else
        {
            IdentityStatusBadge.Background = Brush("DangerSoft");
            IdentityStatusBadgeText.Foreground = Brush("Danger");
        }
    }

    private static string IdentityGuidance(IdentityDetails identity)
    {
        if (identity.IsRegistered) return "Registered and available for provider connections.";
        if (identity.RegistrationInProgress) return "Registration is in progress. Top up if payment is required, then refresh.";
        if (identity.RegistrationStatus.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "Registration could not be checked. Verify network access and refresh status.";
        }
        return "This identity must be registered before connecting.";
    }

    private static string PaymentGuidance(IdentityDetails identity)
    {
        if (identity.RegistrationInProgress) return "Registration pending · top up may be required";
        if (!identity.IsRegistered) return "Register the identity to enable payments";
        return identity.BalanceTokens.Value <= 0
            ? "No funds · top up before paid browsing"
            : "Funded · available for paid provider traffic";
    }

    private static string FormatRegistrationStatus(string status) => status switch
    {
        "InProgress" => "In progress",
        "RegistrationError" => "Registration error",
        "" => "Unknown",
        _ => status,
    };

    private static string FormatLifecycleState(BackendLifecycleState state) => state switch
    {
        BackendLifecycleState.Starting => "Starting",
        BackendLifecycleState.Running => "Running",
        BackendLifecycleState.Crashed => "Crashed · restart available",
        BackendLifecycleState.Failed => "Failed · restart available",
        _ => "Stopped",
    };

    private SolidColorBrush Brush(string key) => (SolidColorBrush)FindResource(key);

    private static string ShortId(string id) => id.Length > 16 ? id[..10] + "…" + id[^4..] : id;
    private static string Capitalize(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private enum AppPage
    {
        Home,
        Identity,
        Wallet,
        Connection,
        BrowserAndDiagnostics,
    }
}
