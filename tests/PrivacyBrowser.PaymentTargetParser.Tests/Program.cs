using System.Text.Json;
using PrivacyBrowser.App;

var tests = new (string Name, Action Run)[]
{
    ("registered adapter matching is case-exact", RegisteredAdapterMatchingIsExact),
    ("Stripe production checkout accepted", () => Accept("stripe", "https://checkout.stripe.com/c/pay/test")),
    ("PayPal production checkout accepted", () => Accept("paypal", "https://www.paypal.com/checkoutnow?token=test")),
    ("CoinGate response remains paymentUrl", CoinGateContractIsPreserved),
    ("HTTP rejected", () => RejectUrl("stripe", "http://checkout.stripe.com/c/pay/test")),
    ("user-info rejected", () => RejectUrl("stripe", "https://user:password@checkout.stripe.com/c/pay/test")),
    ("fragment rejected", () => RejectUrl("stripe", "https://checkout.stripe.com/c/pay/test#done")),
    ("empty fragment rejected", () => RejectUrl("stripe", "https://checkout.stripe.com/c/pay/test#")),
    ("non-default port rejected", () => RejectUrl("stripe", "https://checkout.stripe.com:444/c/pay/test")),
    ("explicit empty port rejected", () => RejectUrl("stripe", "https://checkout.stripe.com:/c/pay/test")),
    ("leading whitespace rejected", () => RejectUrl("stripe", " https://checkout.stripe.com/c/pay/test")),
    ("embedded control rejected", () => Reject("stripe", "stripe", "{\"checkout_url\":\"https://checkout.stripe.com/c/pay/\\u0001\"}")),
    ("backslash rejected", () => RejectUrl("stripe", "https://checkout.stripe.com\\@evil.example/c/pay/test")),
    ("Stripe subdomain rejected", () => RejectUrl("stripe", "https://evil.checkout.stripe.com/c/pay/test")),
    ("Stripe suffix lookalike rejected", () => RejectUrl("stripe", "https://checkout.stripe.com.evil.example/c/pay/test")),
    ("Stripe Unicode lookalike rejected", () => RejectUrl("stripe", "https://checkout.stripe.c\u043Em/c/pay/test")),
    ("PayPal subdomain rejected", () => RejectUrl("paypal", "https://evil.www.paypal.com/checkoutnow?token=test")),
    ("PayPal suffix lookalike rejected", () => RejectUrl("paypal", "https://www.paypal.com.evil.example/checkoutnow?token=test")),
    ("PayPal sandbox host rejected", () => RejectUrl("paypal", "https://www.sandbox.paypal.com/checkoutnow?token=test")),
    ("PayPal unapproved path rejected", () => RejectUrl("paypal", "https://www.paypal.com/signin")),
    ("missing checkout field rejected", () => Reject("stripe", "stripe", "{}")),
    ("null checkout field rejected", () => Reject("stripe", "stripe", "{\"checkout_url\":null}")),
    ("nested checkout field rejected", () => Reject("stripe", "stripe", "{\"nested\":{\"checkout_url\":\"https://checkout.stripe.com/c/pay/test\"}}")),
    ("duplicate checkout field rejected", () => Reject("stripe", "stripe", "{\"checkout_url\":\"https://checkout.stripe.com/c/pay/one\",\"checkout_url\":\"https://checkout.stripe.com/c/pay/two\"}")),
    ("case-shadowed checkout field rejected", () => Reject("stripe", "stripe", "{\"checkout_url\":\"https://checkout.stripe.com/c/pay/test\",\"Checkout_Url\":\"https://evil.example/\"}")),
    ("wrong root type rejected", () => Reject("stripe", "stripe", "[]")),
    ("response gateway mismatch rejected", () => Reject("stripe", "paypal", "{\"checkout_url\":\"https://checkout.stripe.com/c/pay/test\"}")),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

if (failures > 0) return 1;
Console.WriteLine($"PASS: all {tests.Length} payment target tests passed.");
return 0;

static void RegisteredAdapterMatchingIsExact()
{
    foreach (var supported in new[] { "coingate", "stripe", "paypal" })
    {
        if (!PaymentGatewayRegistry.SupportsGateway(supported)) throw new InvalidOperationException($"Missing {supported}.");
    }
    foreach (var unsupported in new string?[] { null, "", "CoinGate", "Stripe", "PayPal", "google" })
    {
        if (PaymentGatewayRegistry.SupportsGateway(unsupported))
            throw new InvalidOperationException($"Unexpected adapter: {unsupported}");
    }
}

static void Accept(string gateway, string url)
{
    var target = PaymentGatewayRegistry.ParsePaymentTarget(
        gateway, gateway, Parse($"{{\"checkout_url\":{JsonSerializer.Serialize(url)}}}"));
    if (!target.GatewayName.Equals(gateway, StringComparison.Ordinal) || !target.PaymentUri.IsAbsoluteUri)
        throw new InvalidOperationException("Validated target did not retain its exact gateway.");
}

static void CoinGateContractIsPreserved()
{
    var target = PaymentGatewayRegistry.ParsePaymentTarget(
        "coingate", "coingate", Parse("{\"paymentUrl\":\"https://pay.example/order\"}"));
    if (target.PaymentUri.Host != "pay.example") throw new InvalidOperationException("CoinGate contract changed.");
    Reject("coingate", "coingate", "{\"checkout_url\":\"https://pay.example/order\"}");
}

static void RejectUrl(string gateway, string url) =>
    Reject(gateway, gateway, $"{{\"checkout_url\":{JsonSerializer.Serialize(url)}}}");

static void Reject(string expectedGateway, string responseGateway, string json)
{
    try
    {
        PaymentGatewayRegistry.ParsePaymentTarget(expectedGateway, responseGateway, Parse(json));
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException("Untrusted payment target was accepted.");
}

static JsonElement Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}
