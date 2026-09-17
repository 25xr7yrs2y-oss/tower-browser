namespace PrivacyBrowser.App;

public sealed class StripePaymentGatewayAdapter : HostedCheckoutPaymentGatewayAdapter
{
    public const string CanonicalGatewayName = "stripe";

    public override string GatewayName => CanonicalGatewayName;
    public override string DisplayName => "Credit / debit card (Stripe)";
    protected override string CheckoutHost => "checkout.stripe.com";
}
