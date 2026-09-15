using System.Text.Json;

namespace PrivacyBrowser.App;

public interface IPaymentGatewayAdapter
{
    string GatewayName { get; }

    string DisplayName { get; }

    string OptionsCurrency { get; }

    string AmountCurrency { get; }

    decimal EffectiveMinimum(PaymentGateway gateway);

    IReadOnlyList<decimal> SuggestedAmounts(PaymentGateway gateway);

    string ParseAndFormatAmount(string value);

    bool IsAmountAllowed(decimal amount, PaymentGateway gateway);

    PaymentOrderCreateRequest BuildRequest(
        string requestedAmount,
        string payCurrency,
        string country,
        string state);

    void ValidateOrder(PaymentOrder order, PaymentOrderIntent intent);

    PaymentTarget ParsePaymentTarget(JsonElement publicGatewayData);
}
