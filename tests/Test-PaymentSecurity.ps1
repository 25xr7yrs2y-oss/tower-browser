$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $root "src\PrivacyBrowser.App"
$allSource = (Get-ChildItem $sourceRoot -Recurse -File -Include @("*.cs", "*.xaml") | Get-Content -Raw) -join "`n"
$backend = Get-Content (Join-Path $sourceRoot "BackendController.cs") -Raw
$journal = Get-Content (Join-Path $sourceRoot "PaymentJournalStore.cs") -Raw
$hosted = Get-Content (Join-Path $sourceRoot "HostedCheckoutPaymentGatewayAdapter.cs") -Raw
$topUp = Get-Content (Join-Path $sourceRoot "TopUpWindow.xaml.cs") -Raw

foreach ($forbidden in @('WebView', 'secure_form', 'Stripe.net', 'PayPalCheckoutSdk', 'api.stripe.com',
        'api-m.paypal.com', 'mysterium-card-client')) {
    if ($allSource.Contains($forbidden)) { throw "Forbidden payment architecture reference found: $forbidden" }
}
foreach ($needle in @(
        'v2/payment-order-gateways?options_currency=MYST',
        'v2/payment-order-gateways?options_currency=USD',
        'amount_usd',
        'myst_amount',
        'project_id',
        'gateway_caller_data',
        'GetPaymentOrdersAsync(identityId, cancellationToken)',
        'ReconcilePaymentOrderAsync(identityId, intent, beforeIds)',
        'GetPaymentOrderAsync(journal.Identity, journal.OrderId',
        'paid && balanceIncreased')) {
    if (-not ($allSource + $backend).Contains($needle)) { throw "Payment lifecycle invariant missing: $needle" }
}
foreach ($needle in @('CheckoutUrlField = "checkout_url"', 'property.NameEquals(CheckoutUrlField)',
        'matchingFields != 1', 'ParseHostedCheckout')) {
    if (-not $hosted.Contains($needle)) { throw "Hosted checkout parser invariant missing: $needle" }
}
foreach ($forbidden in @('PaymentUri', 'checkout_url', 'CheckoutUrl', 'Credential', 'Passphrase')) {
    if ($journal.Contains($forbidden)) { throw "Payment journal contains forbidden field/material: $forbidden" }
}
if ($topUp -match 'Clipboard\.SetText\([^\)]*_paymentUri') {
    throw "Checkout URL must never be copied to the clipboard"
}
if ($backend -match 'Log\?\.Invoke\([^\)]*(checkout|PaymentUri|public_gateway_data)') {
    throw "Checkout material must never be logged"
}

Write-Host "PASS: hosted payments remain native, gateway-bound, redacted, and free of direct merchant SDK/API integration."
