using System.Globalization;
using System.Text.Json;

namespace PrivacyBrowser.App;

public abstract class HostedCheckoutPaymentGatewayAdapter : IPaymentGatewayAdapter
{
    private const string CheckoutUrlField = "checkout_url";
    private const decimal ApplicationMinimumUsd = 1.00m;

    public abstract string GatewayName { get; }
    public abstract string DisplayName { get; }
    protected abstract string CheckoutHost { get; }
    protected virtual string? CheckoutPath => null;

    public string OptionsCurrency => "USD";
    public string AmountCurrency => "USD";

    public decimal EffectiveMinimum(PaymentGateway gateway) =>
        Math.Max(ApplicationMinimumUsd, gateway.OrderOptions.Minimum);

    public IReadOnlyList<decimal> SuggestedAmounts(PaymentGateway gateway)
    {
        var minimum = EffectiveMinimum(gateway);
        var values = (gateway.OrderOptions.Suggested ?? [])
            .Where(value => value >= minimum && value <= PaymentAmount.MaximumUsd && PaymentAmount.Scale(value) <= 2)
            .ToList();
        if (gateway.OrderOptions.Minimum <= ApplicationMinimumUsd)
        {
            values.Add(ApplicationMinimumUsd);
        }
        if (values.Count == 0)
        {
            values.Add(minimum);
        }
        return values.OrderBy(value => value).Distinct().ToArray();
    }

    public string ParseAndFormatAmount(string value) => PaymentAmount.ParseAndFormatUsd(value);

    public bool IsAmountAllowed(decimal amount, PaymentGateway gateway) =>
        amount >= EffectiveMinimum(gateway) && amount <= PaymentAmount.MaximumUsd && PaymentAmount.Scale(amount) <= 2;

    public PaymentOrderCreateRequest BuildRequest(
        string requestedAmount,
        string payCurrency,
        string country,
        string state)
    {
        if (!string.Equals(payCurrency, "USD", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{DisplayName} accepts USD only.");
        }
        return new PaymentOrderCreateRequest
        {
            AmountUsd = requestedAmount,
            PayCurrency = "USD",
            Country = country,
            State = state,
            ProjectId = "",
            GatewayCallerData = new Dictionary<string, object>(),
        };
    }

    public void ValidateOrder(PaymentOrder order, PaymentOrderIntent intent)
    {
        PaymentOrderValidator.ValidateCommon(order, intent);
        if (!string.Equals(intent.AmountCurrency, "USD", StringComparison.Ordinal) ||
            !string.Equals(intent.PayCurrency, "USD", StringComparison.Ordinal) ||
            !string.Equals(order.PayCurrency, "USD", StringComparison.Ordinal) ||
            !PaymentAmount.TryParseResponseAmount(order.PayAmount, out var paid) ||
            !PaymentAmount.TryParseResponseAmount(intent.RequestedAmount, out var requested) ||
            paid != requested)
        {
            throw new InvalidOperationException("The hosted-checkout order did not match the requested USD payment intent.");
        }
    }

    public PaymentTarget ParsePaymentTarget(JsonElement publicGatewayData)
    {
        if (publicGatewayData.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The payment response did not contain valid gateway data.");
        }

        JsonElement checkoutUrl = default;
        var matchingFields = 0;
        foreach (var property in publicGatewayData.EnumerateObject())
        {
            if (!property.Name.Equals(CheckoutUrlField, StringComparison.OrdinalIgnoreCase)) continue;
            matchingFields++;
            if (!property.NameEquals(CheckoutUrlField))
            {
                throw new InvalidOperationException("The payment response contained an ambiguous checkout URL field.");
            }
            checkoutUrl = property.Value;
        }

        if (matchingFields != 1 || checkoutUrl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The payment response must contain exactly one top-level checkout_url string.");
        }

        var uri = PaymentUriValidator.ParseHostedCheckout(
            checkoutUrl.GetString()!, CheckoutHost, CheckoutPath);
        return new PaymentTarget(GatewayName, uri);
    }
}
