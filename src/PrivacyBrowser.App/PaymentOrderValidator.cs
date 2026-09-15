namespace PrivacyBrowser.App;

public static class PaymentOrderValidator
{
    public static void ValidateCommon(PaymentOrder order, PaymentOrderIntent intent)
    {
        if (string.IsNullOrEmpty(order.Id) || order.Id.Length > 200 || HasUnsafeCharacters(order.Id) ||
            string.IsNullOrEmpty(order.Status) || order.Status.Length > 64 || HasUnsafeCharacters(order.Status) ||
            !PaymentIdentity.IsValid(intent.Identity) ||
            !PaymentIdentity.IsValid(order.Identity) ||
            !string.Equals(order.Identity, intent.Identity, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(order.GatewayName, intent.Gateway, StringComparison.Ordinal) ||
            !PaymentAmount.TryParseResponseAmount(order.ReceiveMyst, out _))
        {
            throw new InvalidOperationException("The returned payment order did not match the requested identity and gateway.");
        }
    }

    public static bool IsExactIntentMatch(PaymentOrder order, PaymentOrderIntent intent)
    {
        try
        {
            PaymentGatewayRegistry.GetAdapter(intent.Gateway).ValidateOrder(order, intent);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasUnsafeCharacters(string value) =>
        value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}
