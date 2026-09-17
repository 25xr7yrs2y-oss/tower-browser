using System.Text.Json;
using PrivacyBrowser.App;

var tests = new (string Name, Action Run)[]
{
    ("multi-currency discovery intersects exact registered adapters", GatewayIntersection),
    ("USD application floor wins over lower live minimum", ApplicationFloor),
    ("higher live USD minimum wins and removes one-dollar preset", LiveMinimum),
    ("fiat parser and serializer are exact", ExactUsd),
    ("gateway request denominations remain separated", RequestDenominations),
    ("order response matching binds exact intent", OrderIntent),
    ("status and wallet evidence remain independent", StatusAndBalance),
    ("journal resumes safely and contains no checkout material", JournalResume),
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS: {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}"); }
}
if (failures > 0) return 1;
Console.WriteLine($"PASS: all {tests.Length} payment lifecycle tests passed.");
return 0;

static void GatewayIntersection()
{
    var myst = Gateways("""
        [{"name":"coingate","order_options":{"minimum":7.75,"suggested":[9.49]},"currencies":["BTC","MYST"]},
         {"name":"stripe","order_options":{"minimum":1,"suggested":[2]},"currencies":["USD"]}]
        """);
    var usd = Gateways("""
        [{"name":"stripe","order_options":{"minimum":0.5,"suggested":[3.99]},"currencies":["USD"]},
         {"name":"paypal","order_options":{"minimum":0.5,"suggested":[3.99]},"currencies":["USD"]},
         {"name":"google","order_options":{"minimum":0,"suggested":[3.99]},"currencies":["USD"]},
         {"name":"Stripe","order_options":{"minimum":0.5,"suggested":[3.99]},"currencies":["USD"]}]
        """);
    var result = PaymentGatewayRegistry.IntersectDiscoveredGateways(myst, usd);
    Equal("coingate,stripe,paypal", string.Join(',', result.Select(gateway => gateway.Name)));
}

static void ApplicationFloor()
{
    var gateway = Gateway("stripe", 0.50m, [0.50m, 3.99m]);
    Equal(1.00m, gateway.EffectiveMinimum);
    Equal(1.00m, gateway.SuggestedAmounts[0]);
    True(PaymentGatewayRegistry.GetAdapter("stripe").IsAmountAllowed(1.00m, gateway));
    True(!PaymentGatewayRegistry.GetAdapter("stripe").IsAmountAllowed(0.99m, gateway));
}

static void LiveMinimum()
{
    var gateway = Gateway("paypal", 2.50m, [1.00m, 2.50m, 5.00m]);
    Equal(2.50m, gateway.EffectiveMinimum);
    True(!gateway.SuggestedAmounts.Contains(1.00m));
    Equal(2.50m, gateway.SuggestedAmounts[0]);
}

static void ExactUsd()
{
    Equal("1.00", PaymentAmount.ParseAndFormatUsd("1"));
    Equal("1.20", PaymentAmount.ParseAndFormatUsd("1.2"));
    Equal("1000.00", PaymentAmount.ParseAndFormatUsd("1000.00"));
    foreach (var rejected in new[] { "", "0", "-1", "+1", "1e2", "1E2", ".50", "1.", "1.001", "1000.01", " 1", "1 " })
    {
        Throws(() => PaymentAmount.ParseAndFormatUsd(rejected));
    }
}

static void RequestDenominations()
{
    var stripe = PaymentGatewayRegistry.GetAdapter("stripe").BuildRequest("1.00", "USD", "US", "CA");
    Equal("1.00", stripe.AmountUsd!);
    True(stripe.MystAmount is null);
    Equal("", stripe.ProjectId!);
    Equal(0, stripe.GatewayCallerData.Count);
    var coinGate = PaymentGatewayRegistry.GetAdapter("coingate").BuildRequest("20", "BTC", "DE", "");
    Equal("20", coinGate.MystAmount!);
    True(coinGate.AmountUsd is null);
    True(coinGate.ProjectId is null);
}

static void OrderIntent()
{
    var intent = new PaymentOrderIntent("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "stripe", "1.00", "USD", "USD");
    var order = Order("stripe", "1.00", "USD", "10", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "new");
    PaymentGatewayRegistry.GetAdapter("stripe").ValidateOrder(order, intent);
    foreach (var mutated in new[]
    {
        Order("Stripe", "1.00", "USD", "10", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "new"),
        Order("stripe", "1.01", "USD", "10", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "new"),
        Order("stripe", "1.00", "usd", "10", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "new"),
        Order("stripe", "1.00", "USD", "0", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "new"),
        Order("stripe", "1.00", "USD", "10", "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "new"),
        Order("stripe", "1.00", "USD", "10", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ""),
    })
    {
        Throws(() => PaymentGatewayRegistry.GetAdapter("stripe").ValidateOrder(mutated, intent));
    }
}

static void StatusAndBalance()
{
    True(!PaymentStatus.IsTerminal("confirming"));
    Equal("Confirming (not final)", PaymentStatus.Display("confirming"));
    Equal("Pending", PaymentStatus.Display("future-status"));
    True(PaymentStatus.IsPaid("paid"));
    True(!PaymentBalanceEvidence.HasIncreased("100", "100"));
    True(PaymentBalanceEvidence.HasIncreased("101", "100"));
}

static void JournalResume()
{
    var root = Path.Combine(Path.GetTempPath(), "privacy-browser-payment-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var created = DateTimeOffset.UtcNow;
        var store = new PaymentJournalStore(root);
        store.Save(new PaymentJournalEntry(
            "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "paypal", "order-1", "1.00", "USD", "USD", "10", "100", created, created, "new"));
        var resumed = new PaymentJournalStore(root).Load() ?? throw new InvalidOperationException("Journal did not resume.");
        Equal("order-1", resumed.OrderId);
        var persisted = File.ReadAllText(Path.Combine(root, "state", "payment-journal.json"));
        True(!persisted.Contains("checkout_url", StringComparison.OrdinalIgnoreCase));
        True(!persisted.Contains("paypal.com", StringComparison.OrdinalIgnoreCase));
        Throws(store.ClearTerminal);
        store.UpdateStatus("paid", created.AddSeconds(1));
        store.ClearTerminal();
        True(store.Load() is null);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static List<PaymentGateway> Gateways(string json) =>
    JsonSerializer.Deserialize<List<PaymentGateway>>(json) ?? [];

static PaymentGateway Gateway(string name, decimal minimum, List<decimal> suggested) => new()
{
    Name = name,
    Currencies = ["USD"],
    OrderOptions = new PaymentOrderOptions { Minimum = minimum, Suggested = suggested },
};

static PaymentOrder Order(string gateway, string payAmount, string payCurrency, string receiveMyst, string identity, string status) => new()
{
    Id = "order-1",
    Status = status,
    Identity = identity,
    GatewayName = gateway,
    PayAmount = payAmount,
    PayCurrency = payCurrency,
    ReceiveMyst = receiveMyst,
};

static void Throws(Action action)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected rejection.");
}

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Condition was false.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}
