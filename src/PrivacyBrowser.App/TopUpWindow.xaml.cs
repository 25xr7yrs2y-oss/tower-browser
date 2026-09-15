using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PrivacyBrowser.App;

public partial class TopUpWindow : Window
{
    private readonly BackendController _backend;
    private readonly string _identityId;
    private readonly DispatcherTimer _pollTimer;
    private bool _busy;
    private Uri? _paymentUri;
    private DateTimeOffset? _pollingStarted;
    private int _pollAttempt;
    private bool _continuePolling;

    public TopUpWindow(BackendController backend, string identityId)
    {
        InitializeComponent();
        _backend = backend;
        _identityId = identityId;
        try
        {
            CountryTextBox.Text = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
            CountryTextBox.Text = "US";
        }
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await PollPaymentAsync(automatic: true);
        Loaded += TopUpWindow_Loaded;
        Closed += (_, _) => _pollTimer.Stop();
    }

    public PaymentOrder? CreatedOrder { get; private set; }

    private async void TopUpWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var gateways = await _backend.GetPaymentGatewaysAsync();
            GatewayComboBox.ItemsSource = gateways;
            GatewayComboBox.SelectedItem = gateways.FirstOrDefault();
            if (gateways.Count == 0)
            {
                throw new InvalidOperationException("No payment gateways are currently available from Mysterium.");
            }

        });
        PaymentJournalEntry? journal;
        try
        {
            journal = _backend.GetPaymentJournal();
        }
        catch (Exception ex)
        {
            ShowError(PaymentErrorMessage(ex));
            return;
        }
        if (journal is null) return;
        if (!journal.Identity.Equals(_identityId, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Another identity has an active or retained payment order. Reopen Wallet with that identity selected.");
            return;
        }
        await PollPaymentAsync(automatic: false);
        if (_continuePolling) StartPolling();
    }

    private void GatewayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GatewayComboBox.SelectedItem is not PaymentGateway gateway) return;
        var adapter = PaymentGatewayRegistry.GetAdapter(gateway.Name);
        CurrencyComboBox.ItemsSource = gateway.Currencies ?? [];
        CurrencyComboBox.SelectedItem = (gateway.Currencies ?? []).FirstOrDefault();
        AmountLabelText.Text = $"Amount ({adapter.AmountCurrency})";
        var relation = gateway.Name == CoinGatePaymentGatewayAdapter.CanonicalGatewayName ? "more than" : "at least";
        GatewayHelpText.Text = $"Effective minimum: {relation} {gateway.EffectiveMinimum:0.00} {adapter.AmountCurrency}.";
        var presets = gateway.SuggestedAmounts.Select(value => new PaymentAmountPreset(
            value.ToString(adapter.AmountCurrency == "USD" ? "0.00" : "0.##################", CultureInfo.InvariantCulture),
            adapter.AmountCurrency == "USD" ? $"${value:0.00}" : $"{value:0.####} MYST")).ToArray();
        SuggestedAmountComboBox.ItemsSource = presets;
        SuggestedAmountComboBox.SelectedItem = presets.FirstOrDefault();
        if (presets.Length > 0) AmountTextBox.Text = presets[0].Value;
    }

    private void SuggestedAmountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestedAmountComboBox.SelectedItem is PaymentAmountPreset preset)
        {
            AmountTextBox.Text = preset.Value;
        }
    }

    private async void CreateOrderButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
        if (GatewayComboBox.SelectedItem is not PaymentGateway gateway)
        {
            ShowError("Choose a payment gateway.");
            return;
        }
        if (CurrencyComboBox.SelectedItem is not string currency)
        {
            ShowError("Choose a payment currency.");
            return;
        }

        await RunAsync(async () =>
        {
            _paymentUri = null;
            CreatedOrder = null;
            OpenPaymentButton.Visibility = Visibility.Collapsed;
            ResultBorder.Visibility = Visibility.Collapsed;

            var created = await _backend.CreatePaymentOrderAsync(
                _identityId, gateway, AmountTextBox.Text, currency, CountryTextBox.Text, StateTextBox.Text);
            CreatedOrder = created.Order;
            _paymentUri = created.PaymentTarget.PaymentUri;
            RenderCreated(created.Order);
            ResultBorder.Visibility = Visibility.Visible;
            OpenPaymentButton.Visibility = Visibility.Visible;
            CreateOrderButton.IsEnabled = PaymentStatus.IsTerminal(created.Order.Status);
            _continuePolling = true;
            StartPolling(resetDeadline: true);
        });
    }

    private void OpenPaymentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_paymentUri is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_paymentUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(PaymentErrorMessage(ex));
        }
    }

    private void CopyPaymentButton_Click(object sender, RoutedEventArgs e)
    {
        if (CreatedOrder is null) return;
        Clipboard.SetText($"Mysterium payment order: {CreatedOrder.Id}\nStatus: {CreatedOrder.Status}\n" +
            $"Receive: {CreatedOrder.ReceiveMyst} MYST\nPay: {CreatedOrder.PayAmount} {CreatedOrder.PayCurrency}");
        CopyPaymentButton.Content = "Copied";
    }

    private async void RefreshStatusButton_Click(object sender, RoutedEventArgs e) =>
        await PollPaymentAsync(automatic: false);

    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _backend.ClearTerminalPayment();
            _pollTimer.Stop();
            _pollingStarted = null;
            _continuePolling = false;
            CreatedOrder = null;
            _paymentUri = null;
            ResultBorder.Visibility = Visibility.Collapsed;
            OpenPaymentButton.Visibility = Visibility.Collapsed;
            CreateOrderButton.IsEnabled = true;
            CreateOrderButton.Content = "Create payment order";
        }
        catch (Exception ex)
        {
            ShowError(PaymentErrorMessage(ex));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        LoadingProgress.Visibility = Visibility.Visible;
        CreateOrderButton.IsEnabled = false;
        GatewayComboBox.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(PaymentErrorMessage(ex));
        }
        finally
        {
            _busy = false;
            LoadingProgress.Visibility = Visibility.Collapsed;
            try
            {
                var journal = _backend.GetPaymentJournal();
                CreateOrderButton.IsEnabled = journal is null || PaymentStatus.IsTerminal(journal.LastStatus);
            }
            catch
            {
                CreateOrderButton.IsEnabled = false;
            }
            GatewayComboBox.IsEnabled = true;
        }
    }

    private async Task PollPaymentAsync(bool automatic)
    {
        if (_busy) return;
        if (automatic && _pollingStarted is { } started && DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(15))
        {
            _pollTimer.Stop();
            ShowError("Automatic payment checks stopped after 15 minutes. Use Refresh status to check again.");
            return;
        }

        await RunAsync(async () =>
        {
            var snapshot = await _backend.PollPaymentOrderAsync();
            CreatedOrder = snapshot.Order;
            RenderSnapshot(snapshot);
            ResultBorder.Visibility = Visibility.Visible;
            OpenPaymentButton.Visibility = _paymentUri is null ? Visibility.Collapsed : Visibility.Visible;
            ClearCompletedButton.Visibility = PaymentStatus.IsTerminal(snapshot.Order.Status)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (snapshot.CreditedSuccess ||
                (PaymentStatus.IsTerminal(snapshot.Order.Status) && !snapshot.IsPaid))
            {
                _continuePolling = false;
                _pollTimer.Stop();
            }
            else
            {
                _continuePolling = true;
                if (automatic)
                {
                    _pollAttempt++;
                    _pollTimer.Interval = TimeSpan.FromSeconds(_pollAttempt switch
                    {
                        <= 1 => 5,
                        2 => 10,
                        3 => 15,
                        _ => 30,
                    });
                }
            }
        });
    }

    private void StartPolling(bool resetDeadline = false)
    {
        if (resetDeadline || _pollingStarted is null) _pollingStarted = DateTimeOffset.UtcNow;
        _pollAttempt = 0;
        _pollTimer.Interval = TimeSpan.FromSeconds(5);
        _pollTimer.Start();
    }

    private void RenderCreated(PaymentOrder order)
    {
        ResultTitleText.Text = "Payment order created";
        ResultDetailText.Text = $"Order {order.Id}\nOrder status: {PaymentStatus.Display(order.Status)}\n" +
            $"Receive: {order.ReceiveMyst} MYST\nPay: {order.PayAmount} {order.PayCurrency}\n" +
            "Wallet credit observed: not checked yet";
    }

    private void RenderSnapshot(PaymentStatusSnapshot snapshot)
    {
        ResultTitleText.Text = snapshot.CreditedSuccess ? "Payment credited" : "Payment status";
        ResultDetailText.Text = $"Order {snapshot.Order.Id}\n" +
            $"Order status: {snapshot.DisplayStatus}\n" +
            $"Mysterium reports paid: {(snapshot.IsPaid ? "Yes" : "No")}\n" +
            $"Wallet balance increased: {(snapshot.BalanceIncreased ? "Yes" : "No")}\n" +
            $"Baseline/current: {snapshot.BaselineBalance:0.######} / {snapshot.CurrentBalance:0.######} MYST\n" +
            $"End-to-end credited success: {(snapshot.CreditedSuccess ? "Yes" : "No")}";
        CreateOrderButton.IsEnabled = PaymentStatus.IsTerminal(snapshot.Order.Status);
        CreateOrderButton.Content = PaymentStatus.IsTerminal(snapshot.Order.Status)
            ? "Create another order"
            : "Payment pending";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private static string PaymentErrorMessage(Exception exception) => exception switch
    {
        PaymentOrderAmbiguousException => exception.Message,
        InvalidOperationException when exception is not BackendApiException &&
                                       exception is not BackendMalformedResponseException => exception.Message,
        _ => BackendErrorTranslator.ToUserMessage(exception),
    };

    private sealed record PaymentAmountPreset(string Value, string Display);
}
