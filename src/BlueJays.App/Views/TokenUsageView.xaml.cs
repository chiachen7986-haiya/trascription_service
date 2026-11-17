using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlueJays.App.Services;
using BlueJays.Core.Translation;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Views;

public partial class TokenUsageView : UserControl
{
    private const int ChartDays = 14;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TokenUsageStore? usageStore;
    private ApiTokenUsage totals;
    private sealed record ChartBar(double Height, string Label);

    public TokenUsageView()
    {
        InitializeComponent();
        ProviderBox.ItemsSource = ApiPricing.Models.Select(price => price.Provider).Distinct().ToArray();
        ProviderBox.SelectedIndex = 0;
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { if (usageStore is not null) timer.Start(); Refresh(); };
        Unloaded += (_, _) => timer.Stop();
        Refresh();
    }

    public void Initialize(TokenUsageStore store)
    {
        usageStore = store;
        Refresh();
    }

    private void Refresh()
    {
        var (days, warning) = usageStore?.Snapshot() ?? ([], false);
        totals = new(days.Sum(day => day.Input), days.Sum(day => day.CachedInput), days.Sum(day => day.Output));
        TotalText.Text = totals.Total.ToString("N0");
        BreakdownText.Text = L.F("{0:N0} input · {1:N0} cached input · {2:N0} output", totals.Input, totals.CachedInput, totals.Output);
        CoverageText.Text = L.F("Translation and summaries since tracking began · {0:N0} responses without usage data",
            days.Sum(day => day.Unreported));
        StorageWarning.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = today.AddDays(1 - ChartDays);
        var byDate = days.ToDictionary(day => day.Day);
        var daily = Enumerable.Range(0, ChartDays).Select(first.AddDays)
            .Select(date => byDate.GetValueOrDefault(date) ?? new(date)).ToArray();
        var maximum = daily.Max(day => day.Total);
        Chart.ItemsSource = daily.Select(day => new ChartBar(day.Total == 0 ? 0 : Math.Max(2, 72d * day.Total / maximum),
            L.F("{0} · {1:N0} tokens", day.Day.ToString("d"), day.Total))).ToArray();
        ChartScale.Text = L.F("{0:N0} tokens", maximum);
        ChartStart.Text = first.ToString("M");
        ChartEnd.Text = today.ToString("M");
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not string provider) return;
        ModelBox.ItemsSource = ApiPricing.Models.Where(price => price.Provider == provider).ToArray();
        ModelBox.SelectedIndex = 0;
    }

    private void UseRecorded_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        InputBox.Text = totals.Input.ToString(CultureInfo.CurrentCulture);
        CachedBox.Text = totals.CachedInput.ToString(CultureInfo.CurrentCulture);
        OutputBox.Text = totals.Output.ToString(CultureInfo.CurrentCulture);
    }

    private void Quote_Changed(object sender, RoutedEventArgs e)
    {
        if (QuoteText is null || ModelBox.SelectedItem is not ApiModelPrice price) return;
        RatesText.Text = L.F("USD per million tokens · {0:0.####} input · {1:0.####} cached input · {2:0.####} output",
            price.Input, price.CachedInput, price.Output);
        SourceLabel.Text = L.F("Provider pricing · checked {0}", ApiPricing.VerifiedOn.ToString("d"));
        if (!TryCount(InputBox.Text, out var input) || !TryCount(CachedBox.Text, out var cached) ||
            !TryCount(OutputBox.Text, out var output) || cached > input)
        {
            QuoteText.Text = L.T("Use whole numbers. Cached input must be within total input.");
            return;
        }
        var quote = price.Quote(new(input, cached, output));
        QuoteText.Text = L.F("Estimated cost · USD {0}", quote is > 0 and < 0.000001m
            ? "< 0.000001" : quote.ToString("0.00####", CultureInfo.CurrentCulture));
    }

    private static bool TryCount(string text, out long value) =>
        long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value) && value >= 0;

    private void PricingSource_Click(object sender, RoutedEventArgs e)
    {
        if (ModelBox.SelectedItem is ApiModelPrice price)
            Process.Start(new ProcessStartInfo(price.Source) { UseShellExecute = true });
    }
}
