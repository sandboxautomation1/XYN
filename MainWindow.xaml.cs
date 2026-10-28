using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GoldDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly GoldDeskService service;
    private readonly Mt5BridgeClient mt5Bridge = new();

    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool isRefreshing;
    private bool isInitialized;
    private bool isChangingTimeframe;
    private string selectedTimeframe = "M30";

    public MainWindow() : this(new GoldDeskService()) { }

    public MainWindow(GoldDeskService service)
    {
        this.service = service;
        InitializeComponent();
        ProviderBox.SelectedIndex = 0;
        mt5Bridge.OhlcReceived += OnMt5Ohlc;
        mt5Bridge.PriceReceived += OnMt5Price;
        liveTimer.Tick += async (_, _) => await RefreshMarketAsync(false);
        liveTimer.Start();
        Closed += async (_, _) => await mt5Bridge.DisposeAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            ProviderBox.SelectedIndex = 0;
            SymbolBox.Text = "XAUUSD";
            PriceUrlBox.Text = "MT5 bridge: http://localhost:8000";
            TwelveDataKeyBox.Password = string.Empty;
            IntervalBox.Text = selectedTimeframe;
            FeedsBox.Text = string.Join(Environment.NewLine, service.Sources.Feeds.Select(x => x.Url));
            DefaultNewsBox.IsChecked = service.Sources.UseDefaultNews;

            try
            {
                await mt5Bridge.StartAsync("XAUUSD", selectedTimeframe, 100);
                var price = service.CurrentMt5Price ?? throw new InvalidOperationException("MT5 не върна live tick за XAUUSD.");
                await service.GenerateAnalysisAsync(price);
                UpdatePrice(price);
                Render();
                ChartMetaText.Text = $"MT5 · {selectedTimeframe} · live tick";
                isInitialized = true;
            }
            catch (Exception ex)
            {
                ChartMetaText.Text = $"MT5 не е достъпен: {ex.Message}";
                PriceMeta.Text = "Няма live данни от MT5";
                isInitialized = false;
            }
        }
        catch (Exception ex)
        {
            PriceMeta.Text = "Цена: " + ex.Message;
        }
    }

    private void ChangeSource_Click(object sender, RoutedEventArgs e) { SourcesTab.IsSelected = true; SourcesTab.Focus(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshMarketAsync(true);

    private async Task RefreshMarketAsync(bool includeNews)
    {
        if (isRefreshing) return;
        isRefreshing = true;
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = includeNews ? "Обновявам анализа…" : "Обновявам…";
        try
        {
            if (includeNews) await service.RefreshNewsAsync();
            var price = service.CurrentMt5Price ?? throw new InvalidOperationException("Няма live цена от MT5. Проверете дали MT5 terminal и bridge-ът работят.");
            await service.GenerateAnalysisAsync(price);
            UpdatePrice(price);
            Render();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Gold Desk", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "Обнови анализа";
            isRefreshing = false;
        }
    }

    private void UpdatePrice(GoldPrice price)
    {
        PriceText.Text = price.Price.ToString("0.00");
        PriceMeta.Text = $"{price.Symbol} · {price.Source} · {price.ChangePct:+0.00;-0.00;0.00}%";
        var snapshot = service.MarketSnapshot;
        OpenText.Text = snapshot?.Open.ToString("0.00") ?? "--";
        HighText.Text = snapshot?.High.ToString("0.00") ?? "--";
        LowText.Text = snapshot?.Low.ToString("0.00") ?? "--";
        CloseText.Text = snapshot?.Close.ToString("0.00") ?? price.Price.ToString("0.00");
        LastUpdatedText.Text = $"{price.Source} · последно обновяване {price.UpdatedAt:HH:mm:ss}";
        var daily = service.Forecasts.FirstOrDefault(x => x.Horizon.Contains("24"));
        var weekly = service.Forecasts.FirstOrDefault(x => x.Horizon.Contains("седмица"));
        DailyRangeText.Text = daily is null ? "--" : $"{daily.Low:0.00} – {daily.High:0.00}";
        DailyAverageText.Text = daily is null ? "Средно: --" : $"Средно: {daily.Average:0.00} ({daily.ChangePct:+0.00;-0.00;0.00}%)";
        WeeklyRangeText.Text = weekly is null ? "--" : $"{weekly.Low:0.00} – {weekly.High:0.00}";
        WeeklyAverageText.Text = weekly is null ? "Средно: --" : $"Средно: {weekly.Average:0.00} ({weekly.ChangePct:+0.00;-0.00;0.00}%)";
        ShortTermRangeText.Text = daily is null ? "--" : $"{daily.Low:0.00} – {daily.High:0.00}";
        ShortTermAverageText.Text = daily is null ? "Средно: --" : $"Средно: {daily.Average:0.00}";
        ShortTermChangeText.Text = daily is null ? "Промяна: --" : $"Промяна: {daily.ChangePct:+0.00;-0.00;0.00}%";
        ShortTermChangeText.Foreground = daily is null || daily.ChangePct >= 0 ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73D49A")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F17C65"));
        LongTermRangeText.Text = weekly is null ? "--" : $"{weekly.Low:0.00} – {weekly.High:0.00}";
        LongTermAverageText.Text = weekly is null ? "Средно: --" : $"Средно: {weekly.Average:0.00}";
        LongTermChangeText.Text = weekly is null ? "Промяна: --" : $"Промяна: {weekly.ChangePct:+0.00;-0.00;0.00}%";
        LongTermChangeText.Foreground = weekly is null || weekly.ChangePct >= 0 ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73D49A")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F17C65"));
        PriceChart.Candles = service.MarketHistory;
        PriceChart.CurrentPrice = price.Price;
        PriceChart.BuyAbove = service.CurrentSignal?.Levels.BuyAbove;
        PriceChart.SellBelow = service.CurrentSignal?.Levels.SellBelow;
        ChartMetaText.Text = $"{price.Source} · {service.MarketHistory.Count} свещи · обновено {price.UpdatedAt:HH:mm:ss}";
        UpdateTechnicalIndicators();
        PriceChart.InvalidateVisual();
    }

    private void OnMt5Ohlc(IReadOnlyList<MarketCandle> candles, string symbol)
    {
        Dispatcher.Invoke(() => { 
            service.ApplyMt5History(candles); 
            PriceChart.Candles = service.MarketHistory;
            UpdateTechnicalIndicators(); 
                ChartMetaText.Text = $"MT5 REST · {symbol} · {selectedTimeframe} · {candles.Count} свещи"; PriceChart.InvalidateVisual(); 
        });
    }

    private async void TimeframeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!isInitialized || isChangingTimeframe || TimeframeBox.SelectedItem is not ComboBoxItem item)
            return;

        var timeframe = item.Content?.ToString();
        if (string.IsNullOrWhiteSpace(timeframe) || timeframe == selectedTimeframe)
            return;

        isChangingTimeframe = true;
        TimeframeBox.IsEnabled = false;
        selectedTimeframe = timeframe;
        ChartMetaText.Text = $"MT5 REST · зареждане на {selectedTimeframe} данни…";
        try
        {
            await mt5Bridge.RestartAsync("XAUUSD", selectedTimeframe, 100);
            PriceChart.Candles = [];
            PriceChart.InvalidateVisual();
        }
        catch (Exception ex)
        {
            ChartMetaText.Text = $"Грешка при зареждане на {selectedTimeframe}: {ex.Message}";
        }
        finally
        {
            TimeframeBox.IsEnabled = true;
            isChangingTimeframe = false;
        }
    }

    private void OnMt5Price(Mt5PriceUpdate update)
    {
        Dispatcher.Invoke(() =>
        {
            var price = service.ApplyMt5Quote(update);
            service.GenerateAnalysisAsync(price).GetAwaiter().GetResult();
            UpdatePrice(price);
            Render();
        });
    }

    private void UpdateTechnicalIndicators()
    {
        var indicators = TechnicalIndicatorCalculator.Calculate(service.MarketHistory);
        SmaText.Text = FormatIndicator(indicators.Sma20);
        RsiText.Text = indicators.Rsi14 is null ? "--" : $"{indicators.Rsi14:0.0}";
        MacdText.Text = indicators.Macd is null
            ? "--"
            : $"{indicators.Macd:0.00} / {FormatIndicator(indicators.MacdSignal)}";
        BollingerText.Text = indicators.BollingerMiddle is null
            ? "--"
            : $"{indicators.BollingerLower:0.00} – {indicators.BollingerMiddle:0.00} – {indicators.BollingerUpper:0.00}";
    }

    private static string FormatIndicator(decimal? value) => value is null ? "--" : value.Value.ToString("0.00");

    private async void Manual_Click(object sender, RoutedEventArgs e)
    {
        try { await service.AnalyzeManualAsync(ManualBox.Text.Trim()); ManualBox.Clear(); Render(); MessageBox.Show("Анализът е добавен към новините.", "Gold Desk"); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Gold Desk", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SaveSources_Click(object sender, RoutedEventArgs e)
    {
        _ = SaveSourcesAsync();
    }

    private async Task SaveSourcesAsync()
    {
        try
        {
            service.Sources.Provider = "mt5";
            service.Sources.Symbol = "XAUUSD";
            service.Sources.PriceUrl = "MT5 bridge: http://localhost:8000";
            service.Sources.TwelveDataApiKey = string.Empty;
            service.Sources.TwelveDataInterval = selectedTimeframe;
            service.Sources.UseDefaultNews = DefaultNewsBox.IsChecked == true;
            service.Sources.Feeds = FeedsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => new NewsFeed { Name = "Custom", Url = x.Trim() }).Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out _)).ToList();
            service.SaveSources();
            var price = service.CurrentMt5Price ?? throw new InvalidOperationException("Настройките са запазени, но няма live цена от MT5.");
            await service.GenerateAnalysisAsync(price);
            UpdatePrice(price); Render();
            MessageBox.Show("Източниците са запазени и приложени.", "Gold Desk");
        }
        catch (Exception ex) { MessageBox.Show("Източникът не може да бъде приложен: " + ex.Message, "Gold Desk", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Logout_Click(object sender, RoutedEventArgs e) { service.Logout(); var loginWindow = new LoginWindow(service); Application.Current.MainWindow = loginWindow; loginWindow.Show(); Close(); }

    private void Render()
    {
        var signal = service.CurrentSignal;
        if (signal != null)
        {
            CurrentSignalText.Text = GoldDeskService.ActionLabel(signal.Action);
            CurrentSignalText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(signal.Action == "buy" ? "#73D49A" : signal.Action == "sell" ? "#F17C65" : "#AEB8D9"));
            SignalMeta.Text = $"увереност {signal.Confidence}% · риск {(signal.Risk == "medium" ? "среден" : "висок")} · {signal.Horizon}";
            ConfidenceText.Text = $"{signal.Confidence}%";
            PriceChart.BuyAbove = signal.Levels.BuyAbove; PriceChart.SellBelow = signal.Levels.SellBelow; PriceChart.InvalidateVisual();
        }
        BriefingText.Text = service.Briefing;
        NewsList.Items.Clear();
        foreach (var news in service.News)
        {
            var textBlock = new TextBlock { Text = $"{(news.Sentiment == "bullish" ? "БИЧИ" : news.Sentiment == "bearish" ? "МЕЧИ" : "НЕУТРАЛНО")} · въздействие {news.Impact}/5 · {news.Source}\n{news.Title}\n{news.Summary}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
            textBlock.MouseLeftButtonUp += (_, _) => { if (Uri.TryCreate(news.Url, UriKind.Absolute, out var uri) && uri.Scheme != "manual") Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); };
            NewsList.Items.Add(textBlock);
        }
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
}
