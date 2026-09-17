namespace PrivacyBrowser.App;

public sealed class PayPalPaymentGatewayAdapter : HostedCheckoutPaymentGatewayAdapter
{
    public const string CanonicalGatewayName = "paypal";

    public override string GatewayName => CanonicalGatewayName;
    public override string DisplayName => "PayPal";

    // PayPal's production Orders API documents payer approval at this exact host/path.
    // The app deliberately excludes sandbox, localized, subdomain, and suffix matches.
    protected override string CheckoutHost => "www.paypal.com";
    protected override string CheckoutPath => "/checkoutnow";
}
