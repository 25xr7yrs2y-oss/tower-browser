using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyBrowser.App;

public sealed record PaymentJournalEntry(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("gateway")] string Gateway,
    [property: JsonPropertyName("order_id")] string OrderId,
    [property: JsonPropertyName("requested_amount")] string RequestedAmount,
    [property: JsonPropertyName("amount_currency")] string AmountCurrency,
    [property: JsonPropertyName("pay_currency")] string PayCurrency,
    [property: JsonPropertyName("expected_myst")] string ExpectedMyst,
    [property: JsonPropertyName("baseline_balance_wei")] string BaselineBalanceWei,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_observed_at")] DateTimeOffset LastObservedAt,
    [property: JsonPropertyName("last_status")] string LastStatus);

/// <summary>
/// Stores only the redacted fields required to resume a payment status check.
/// Checkout URLs and authentication/payment credentials are never members of this model.
/// </summary>
public sealed class PaymentJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _sync = new();

    public PaymentJournalStore(string bundleRoot)
    {
        _path = Path.Combine(bundleRoot, "state", "payment-journal.json");
    }

    public PaymentJournalEntry? Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                var entry = JsonSerializer.Deserialize<PaymentJournalEntry>(File.ReadAllText(_path), JsonOptions)
                    ?? throw new InvalidOperationException("The saved payment journal is empty.");
                Validate(entry);
                return entry;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "The saved payment journal could not be read safely. Move it aside before creating another order.",
                    exception);
            }
        }
    }

    public void Save(PaymentJournalEntry entry)
    {
        Validate(entry);
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_path)!;
            var temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, JsonSerializer.Serialize(entry, JsonOptions));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "The redacted payment journal could not be saved; no further order will be created.", exception);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }

    public PaymentJournalEntry UpdateStatus(string status, DateTimeOffset observedAt)
    {
        var entry = Load() ?? throw new InvalidOperationException("No resumable payment order exists.");
        var updated = entry with { LastStatus = status, LastObservedAt = observedAt };
        Save(updated);
        return updated;
    }

    public void ClearTerminal()
    {
        lock (_sync)
        {
            var entry = Load();
            if (entry is null) return;
            if (!PaymentStatus.IsTerminal(entry.LastStatus))
            {
                throw new InvalidOperationException("A pending payment order cannot be cleared.");
            }
            File.Delete(_path);
        }
    }

    private static void Validate(PaymentJournalEntry entry)
    {
        var intent = new PaymentOrderIntent(
            entry.Identity, entry.Gateway, entry.RequestedAmount, entry.AmountCurrency, entry.PayCurrency);
        if (!PaymentGatewayRegistry.SupportsGateway(entry.Gateway) ||
            !PaymentIdentity.IsValid(entry.Identity) ||
            string.IsNullOrEmpty(entry.OrderId) || entry.OrderId.Length > 200 ||
            intent.AmountCurrency != PaymentGatewayRegistry.GetAdapter(entry.Gateway).AmountCurrency ||
            string.IsNullOrEmpty(entry.PayCurrency) || entry.PayCurrency.Length > 20 ||
            !PaymentAmount.TryParseResponseAmount(entry.RequestedAmount, out _) ||
            !PaymentAmount.TryParseResponseAmount(entry.ExpectedMyst, out _) ||
            !PaymentBalanceEvidence.TryParseWei(entry.BaselineBalanceWei, out _) ||
            string.IsNullOrEmpty(entry.LastStatus) || entry.LastStatus.Length > 64 ||
            entry.CreatedAt == default || entry.LastObservedAt == default ||
            entry.LastObservedAt < entry.CreatedAt)
        {
            throw new InvalidOperationException("The saved payment journal failed validation.");
        }
    }
}
