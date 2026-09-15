using System.Text.Json;

namespace PrivacyBrowser.App;

public static class PaymentGatewayRegistry
{
    private static readonly IReadOnlyDictionary<string, IPaymentGatewayAdapter> Adapters =
        new Dictionary<string, IPaymentGatewayAdapter>(StringComparer.Ordinal)
        {
            [CoinGatePaymentGatewayAdapter.CanonicalGatewayName] = new CoinGatePaymentGatewayAdapter(),
            [StripePaymentGatewayAdapter.CanonicalGatewayName] = new StripePaymentGatewayAdapter(),
            [PayPalPaymentGatewayAdapter.CanonicalGatewayName] = new PayPalPaymentGatewayAdapter(),
        };

    public static bool SupportsGateway(string? gatewayName) =>
        gatewayName is not null && Adapters.ContainsKey(gatewayName);

    public static IPaymentGatewayAdapter GetAdapter(string gatewayName) =>
        Adapters.TryGetValue(gatewayName, out var adapter)
            ? adapter
            : throw new InvalidOperationException("The selected payment gateway is not supported.");

    public static IReadOnlyList<PaymentGateway> IntersectDiscoveredGateways(
        IReadOnlyList<PaymentGateway> mystGateways,
        IReadOnlyList<PaymentGateway> usdGateways)
    {
        var discovered = new List<PaymentGateway>();
        foreach (var adapter in Adapters.Values)
        {
            var source = adapter.OptionsCurrency == "USD" ? usdGateways : mystGateways;
            var exactMatches = source.Where(gateway =>
                gateway is not null &&
                string.Equals(gateway.Name, adapter.GatewayName, StringComparison.Ordinal)).ToArray();
            if (exactMatches.Length != 1) continue;

            var gateway = exactMatches[0];
            if (gateway.OrderOptions is null || gateway.OrderOptions.Minimum < 0m ||
                gateway.Currencies is not { Count: > 0 } ||
                (adapter.OptionsCurrency == "USD" &&
                 (gateway.OrderOptions.Minimum > PaymentAmount.MaximumUsd ||
                  !gateway.Currencies.Contains("USD", StringComparer.Ordinal) ||
                  PaymentAmount.Scale(gateway.OrderOptions.Minimum) > 2)))
            {
                continue;
            }
            discovered.Add(gateway);
        }
        return discovered;
    }

    public static PaymentTarget ParsePaymentTarget(
        string expectedGatewayName,
        string responseGatewayName,
        JsonElement publicGatewayData)
    {
        var adapter = GetAdapter(expectedGatewayName);
        if (!string.Equals(responseGatewayName, adapter.GatewayName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The payment response did not match the selected gateway.");
        }

        var target = adapter.ParsePaymentTarget(publicGatewayData);
        if (!string.Equals(target.GatewayName, adapter.GatewayName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The payment gateway adapter returned a mismatched target.");
        }

        return target;
    }
}
