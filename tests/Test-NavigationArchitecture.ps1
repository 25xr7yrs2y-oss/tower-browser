$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $root "src\PrivacyBrowser.App"
$windowPath = Join-Path $sourceRoot "MainWindow.xaml"
$window = Get-Content $windowPath -Raw
$windowCode = Get-Content (Join-Path $sourceRoot "MainWindow.xaml.cs") -Raw

# Parsing as XML catches duplicate x:Name values and malformed page markup before WPF compilation.
[xml](Get-Content $windowPath -Raw) | Out-Null

$pages = @{
    HomePage = @('ConnectionStatusText', 'MainBackendText', 'MainIdentityText', 'MainBalanceText', 'MainProviderText')
    IdentityPage = @('AcceptTermsCheckBox', 'IdentityComboBox', 'CreateIdentityButton', 'ImportIdentityButton',
        'UnlockIdentityButton', 'RegisterIdentityButton', 'RefreshIdentityButton')
    WalletPage = @('WalletBalanceText', 'RefreshBalanceButton', 'TopUpButton')
    ConnectionPage = @('ProviderSearchTextBox', 'ProviderComboBox', 'RefreshProvidersButton', 'ConnectButton',
        'DisconnectButton', 'ConnectionPrerequisiteText')
    BrowserAndDiagnosticsPage = @('BrowserReadinessIcon', 'BrowserReadinessText', 'LaunchBrowserButton',
        'RestartBackendButton', 'BackendLifecycleText', 'ActivityText')
}

foreach ($pageName in $pages.Keys) {
    $match = [regex]::Match($window, "(?s)<ScrollViewer\s+x:Name=`"$pageName`".*?</ScrollViewer>")
    if (-not $match.Success) { throw "Navigation page is missing: $pageName" }
    foreach ($controlName in $pages[$pageName]) {
        $declarations = [regex]::Matches($window, "x:Name=`"$controlName`"")
        if ($declarations.Count -ne 1) {
            throw "Control must have exactly one declaration: $controlName (found $($declarations.Count))"
        }
        if (-not $match.Value.Contains("x:Name=`"$controlName`"")) {
            throw "Control $controlName is not owned by $pageName"
        }
    }
}

foreach ($page in @('Home', 'Identity', 'Wallet', 'Connection', 'BrowserAndDiagnostics')) {
    if (-not $window.Contains("Tag=`"$page`"") -or -not $windowCode.Contains("AppPage.$page")) {
        throw "Page is not reachable through the shared navigation shell: $page"
    }
}

foreach ($constructor in @('new BackendController(', 'new BrowserLauncher(', 'new UserStateStore(', 'new DispatcherTimer')) {
    $count = [regex]::Matches($windowCode, [regex]::Escape($constructor)).Count
    if ($count -ne 1) { throw "Shared application dependency must be constructed once: $constructor (found $count)" }
}

$navigateMethod = [regex]::Match($windowCode, '(?s)private void NavigateTo\(.*?\n    \}')
if (-not $navigateMethod.Success) { throw "Shared in-window navigation method is missing." }
foreach ($forbidden in @('new BackendController(', 'new BrowserLauncher(', 'new UserStateStore(', 'new DispatcherTimer',
        'StartAsync(', 'RestartAsync(', 'DisposeAsync(')) {
    if ($navigateMethod.Value.Contains($forbidden)) {
        throw "Navigation must not recreate or restart shared application state: $forbidden"
    }
}

foreach ($securityMarker in @('PromptForExistingPassphrase', 'TakePassphrase()', '_browserReadiness.CanLaunch',
        'PaymentGatewayRegistry.IntersectDiscoveredGateways(')) {
    $allRelevantSource = $windowCode + (Get-Content (Join-Path $sourceRoot "BackendController.cs") -Raw) +
        (Get-Content (Join-Path $sourceRoot "TopUpWindow.xaml.cs") -Raw)
    if (-not $allRelevantSource.Contains($securityMarker)) {
        throw "Security-sensitive call path marker is missing: $securityMarker"
    }
}

Write-Host "PASS: all actions have one focused page, every page is reachable, and shared/security-sensitive state remains intact."
