using System.Text.Json;

namespace PrivacyBrowser.App;

public sealed class CoinGatePaymentGatewayAdapter : IPaymentGatewayAdapter
{
    public const string CanonicalGatewayName = "coingate";
    private const string PaymentUrlField = "paymentUrl";

    public string GatewayName => CanonicalGatewayName;
    public string DisplayName => "CoinGate (crypto)";
    public string OptionsCurrency => "MYST";
    public string AmountCurrency => "MYST";

    public decimal EffectiveMinimum(PaymentGateway gateway) => gateway.OrderOptions.Minimum;

    public IReadOnlyList<decimal> SuggestedAmounts(PaymentGateway gateway) => (gateway.OrderOptions.Suggested ?? [])
        .Where(value => value > EffectiveMinimum(gateway))
        .OrderBy(value => value)
        .Distinct()
        .ToArray();

    public string ParseAndFormatAmount(string value) => PaymentAmount.ParseAndFormatMyst(value);

    // Preserve the existing CoinGate contract: its reported MYST minimum is exclusive.
    public bool IsAmountAllowed(decimal amount, PaymentGateway gateway) =>
        amount > 0m && (EffectiveMinimum(gateway) <= 0m || amount > EffectiveMinimum(gateway));

    public PaymentOrderCreateRequest BuildRequest(
        string requestedAmount,
        string payCurrency,
        string country,
        string state) => new()
    {
        MystAmount = requestedAmount,
        PayCurrency = payCurrency,
        Country = country,
        State = state,
        ProjectId = null,
        GatewayCallerData = new Dictionary<string, object>(),
    };

    public void ValidateOrder(PaymentOrder order, PaymentOrderIntent intent)
    {
        PaymentOrderValidator.ValidateCommon(order, intent);
        if (!string.Equals(order.PayCurrency, intent.PayCurrency, StringComparison.Ordinal) ||
            !PaymentAmount.TryParseResponseAmount(order.ReceiveMyst, out var received) ||
            !PaymentAmount.TryParseResponseAmount(intent.RequestedAmount, out var requested) ||
            received != requested ||
            !PaymentAmount.TryParseResponseAmount(order.PayAmount, out _))
        {
            throw new InvalidOperationException("The CoinGate order did not match the requested payment intent.");
        }
    }

    public PaymentTarget ParsePaymentTarget(JsonElement publicGatewayData)
    {
        if (publicGatewayData.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The payment response did not contain valid gateway data.");
        }

        JsonElement paymentUrl = default;
        var matchingFields = 0;
        foreach (var property in publicGatewayData.EnumerateObject())
        {
            if (!property.Name.Equals(PaymentUrlField, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matchingFields++;
            if (!property.NameEquals(PaymentUrlField))
            {
                throw new InvalidOperationException("The payment response contained an ambiguous payment URL field.");
            }
            paymentUrl = property.Value;
        }

        if (matchingFields != 1 || paymentUrl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The payment response did not contain one valid payment URL field.");
        }

        return new PaymentTarget(GatewayName, PaymentUriValidator.ParseAbsoluteHttps(paymentUrl.GetString()!));
    }
}
