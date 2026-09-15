using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;

namespace PrivacyBrowser.App;

public static class PaymentAmount
{
    public const decimal MaximumUsd = 1000.00m;

    public static string ParseAndFormatUsd(string value)
    {
        if (!TryParsePlainDecimal(value, 2, MaximumUsd, out var amount))
        {
            throw new InvalidOperationException(
                "Enter a USD amount from 0.01 through 1000.00 using digits and at most two decimal places.");
        }
        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static string ParseAndFormatMyst(string value)
    {
        if (!TryParsePlainDecimal(value, 18, 1_000_000m, out var amount))
        {
            throw new InvalidOperationException(
                "Enter a positive MYST amount using digits and a decimal point.");
        }
        return amount.ToString("0.##################", CultureInfo.InvariantCulture);
    }

    public static bool TryParseResponseAmount(string? value, out decimal amount) =>
        TryParsePlainDecimal(value, 18, decimal.MaxValue, out amount);

    public static int Scale(decimal value) =>
        (decimal.GetBits(value)[3] >> 16) & 0x7F;

    public static bool TryParsePlainDecimal(
        string? value,
        int maximumScale,
        decimal maximum,
        out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;

        var separator = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '.')
            {
                if (separator >= 0) return false;
                separator = index;
            }
            else if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        if (separator == 0 || separator == value.Length - 1) return false;
        if (separator >= 0 && value.Length - separator - 1 > maximumScale) return false;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            return false;
        }
        return amount > 0m && amount <= maximum;
    }
}

public sealed class PaymentOrderCreateRequest
{
    [JsonPropertyName("myst_amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MystAmount { get; init; }

    [JsonPropertyName("amount_usd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AmountUsd { get; init; }

    [JsonPropertyName("pay_currency")]
    public string PayCurrency { get; init; } = "";

    [JsonPropertyName("country")]
    public string Country { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; init; }

    [JsonPropertyName("gateway_caller_data")]
    public IReadOnlyDictionary<string, object> GatewayCallerData { get; init; } =
        new Dictionary<string, object>();
}

public sealed record PaymentOrderIntent(
    string Identity,
    string Gateway,
    string RequestedAmount,
    string AmountCurrency,
    string PayCurrency);

public sealed record CreatedPaymentOrder(
    PaymentOrder Order,
    PaymentTarget PaymentTarget,
    PaymentJournalEntry Journal);

public sealed record PaymentStatusSnapshot(
    PaymentOrder Order,
    PaymentJournalEntry Journal,
    string DisplayStatus,
    bool IsPaid,
    bool BalanceIncreased,
    decimal BaselineBalance,
    decimal CurrentBalance,
    bool CreditedSuccess,
    DateTimeOffset CheckedAt);

public static class PaymentStatus
{
    public static bool IsPaid(string? status) =>
        string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase);

    public static bool IsTerminal(string? status) => status?.ToLowerInvariant() switch
    {
        "paid" or "failed" or "invalid" or "expired" or "canceled" or "cancelled" => true,
        _ => false,
    };

    public static string Display(string? status) => status?.ToLowerInvariant() switch
    {
        "paid" => "Paid",
        "confirming" => "Confirming (not final)",
        "failed" or "invalid" => "Failed",
        "expired" => "Expired",
        "canceled" or "cancelled" => "Cancelled",
        _ => "Pending",
    };
}

public static class PaymentBalanceEvidence
{
    public static bool HasIncreased(string? currentWei, string? baselineWei)
    {
        return TryParseWei(currentWei, out var current) &&
            TryParseWei(baselineWei, out var baseline) && current > baseline;
    }

    public static bool TryParseWei(string? value, out BigInteger amount)
    {
        amount = BigInteger.Zero;
        if (string.IsNullOrEmpty(value) || value.Length > 100 || value.Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }
        return BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount >= 0;
    }
}

public static class PaymentLocation
{
    public static (string Country, string State) Validate(string country, string state)
    {
        var normalizedCountry = country.Trim().ToUpperInvariant();
        var normalizedState = state.Trim().ToUpperInvariant();
        if (!IsCode(normalizedCountry) ||
            (normalizedCountry == "US" && !IsCode(normalizedState)) ||
            (normalizedCountry != "US" && normalizedState.Length != 0))
        {
            throw new InvalidOperationException(
                "Use a two-letter country code; a two-letter state is required only for US payments.");
        }
        return (normalizedCountry, normalizedState);
    }

    private static bool IsCode(string value) =>
        value.Length == 2 && value.All(char.IsAsciiLetter);
}

public static class PaymentIdentity
{
    public static bool IsValid(string? value)
    {
        if (value is not { Length: 42 } || !value.StartsWith("0x", StringComparison.Ordinal)) return false;
        for (var index = 2; index < value.Length; index++)
        {
            if (!char.IsAsciiHexDigit(value[index])) return false;
        }
        return true;
    }
}

public sealed class PaymentOrderAmbiguousException : InvalidOperationException
{
    public PaymentOrderAmbiguousException()
        : base("The payment request outcome is ambiguous. No duplicate order was created; close this dialog and inspect order status before trying again.")
    {
    }
}
