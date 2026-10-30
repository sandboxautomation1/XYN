using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using XYN.Signal;

namespace GoldDesk.Desktop;

public partial class MainWindow : Window
{
    private static readonly HttpClient signalHttpClient = new() { BaseAddress = new Uri("https://tradersview.deriv.com") };
    private static readonly HttpClient bridgeHealthClient = new() { BaseAddress = new Uri("http://127.0.0.1:8000"), Timeout = TimeSpan.FromSeconds(2) };
    private readonly GoldDeskService service;
    private readonly Mt5BridgeClient mt5Bridge = new();
    private Process? mt5ServerProcess;

    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool isRefreshing;
    private bool isInitialized;
    private bool isChangingTimeframe;
    private string selectedTimeframe = "H1";
    private string selectedSymbol = "XAUUSD.p";

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
        Closed += async (_, _) =>
        {
            StopMt5Bridge();
            await mt5Bridge.DisposeAsync();
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            ProviderBox.SelectedIndex = 0;
            selectedSymbol = string.IsNullOrWhiteSpace(service.Sources.Symbol) ? "XAUUSD.p" : service.Sources.Symbol.Trim();
            SymbolInputBox.Text = selectedSymbol;
            SymbolBox.Text = selectedSymbol;
            PriceUrlBox.Text = "MT5 bridge: http://127.0.0.1:8000";
            TwelveDataKeyBox.Password = string.Empty;
            IntervalBox.Text = selectedTimeframe;
            FeedsBox.Text = string.Join(Environment.NewLine, service.Sources.Feeds.Select(x => x.Url));
            DefaultNewsBox.IsChecked = service.Sources.UseDefaultNews;

            try
            {
                await mt5Bridge.StartAsync(selectedSymbol, selectedTimeframe, 100);
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

    private async void StartMt5Bridge_Click(object sender, RoutedEventArgs e)
    {
        if (mt5ServerProcess is { HasExited: false })
        {
            PriceMeta.Text = "MT5 bridge вече работи на 127.0.0.1:8000";
            if (!isInitialized)
                await InitializeAsync();
            return;
        }

        var bridgeDirectory = FindMt5BridgeDirectory();
        if (bridgeDirectory is null)
        {
            MessageBox.Show("Не е намерена директорията mt5-bridge с pyproject.toml.", "MT5 bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ChangeSourceButton.IsEnabled = false;
            ChangeSourceButton.Content = "Стартирам…";
            mt5ServerProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "uv",
                Arguments = "run mt5-bridge server --host 127.0.0.1 --port 8000",
                WorkingDirectory = bridgeDirectory,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            });

            PriceMeta.Text = "Стартирам MT5 bridge на 127.0.0.1:8000…";
            ChartMetaText.Text = "MT5 bridge server · uv run · 127.0.0.1:8000";

            if (!await WaitForBridgeAsync())
            {
                ChartMetaText.Text = "MT5 bridge не отговори на http://127.0.0.1:8000/health";
                PriceMeta.Text = "Bridge server-ът не е достъпен";
                return;
            }

            await InitializeAsync();
            ChangeSourceButton.Content = isInitialized ? "Bridge активен" : "Повтори bridge";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"MT5 bridge не може да бъде стартиран. Проверете дали uv е инсталиран и е в PATH.\n\n{ex.Message}", "MT5 bridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ChangeSourceButton.IsEnabled = true;
            if (ChangeSourceButton.Content?.ToString() == "Стартирам…")
                ChangeSourceButton.Content = "Стартирай bridge";
        }
    }

    private static async Task<bool> WaitForBridgeAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var response = await bridgeHealthClient.GetAsync("/health");
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            await Task.Delay(500);
        }

        return false;
    }

    private static string? FindMt5BridgeDirectory()
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "mt5-bridge");
                if (File.Exists(Path.Combine(candidate, "pyproject.toml")))
                    return candidate;
                directory = directory.Parent;
            }
        }

        return null;
    }

    private void StopMt5Bridge()
    {
        if (mt5ServerProcess is not { HasExited: false }) return;

        try
        {
            mt5ServerProcess.Kill(entireProcessTree: true);
            mt5ServerProcess.Dispose();
            mt5ServerProcess = null;
        }
        catch
        {
            // Процесът може вече да е приключил между проверката и Kill().
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshMarketAsync(true);

    private async void ApplySymbol_Click(object sender, RoutedEventArgs e)
    {
        var symbol = SymbolInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            MessageBox.Show("Въведете MT5 символ, например XAUUSD или XAUUSD.p.", "MT5 symbol", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ApplySymbolAsync(symbol);
    }

    private async Task ApplySymbolAsync(string symbol)
    {
        selectedSymbol = symbol.Trim();
        SymbolInputBox.Text = selectedSymbol;
        SymbolBox.Text = selectedSymbol;
        service.Sources.Symbol = selectedSymbol;
        service.SaveSources();
        service.ClearPriceCache();
        ApplySymbolButton.IsEnabled = false;
        ApplySymbolButton.Content = "Зареждам…";
        ChartMetaText.Text = $"MT5 REST · зареждане на {selectedSymbol}…";

        try
        {
            await mt5Bridge.RestartAsync(selectedSymbol, selectedTimeframe, 100);
            isInitialized = true;
            PriceMeta.Text = $"MT5 · {selectedSymbol}";
        }
        catch (Exception ex)
        {
            isInitialized = false;
            ChartMetaText.Text = $"Грешка при зареждане на {selectedSymbol}: {ex.Message}";
            MessageBox.Show(ex.Message, "MT5 symbol", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ApplySymbolButton.IsEnabled = true;
            ApplySymbolButton.Content = "Приложи";
        }
    }

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
            await mt5Bridge.RestartAsync(selectedSymbol, selectedTimeframe, 100);
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
            var requestedSymbol = SymbolBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(requestedSymbol))
                throw new InvalidOperationException("Въведете MT5 символ.");
            service.Sources.Symbol = requestedSymbol;
            selectedSymbol = requestedSymbol;
            SymbolInputBox.Text = selectedSymbol;
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

    private async void AnalyzeSignal_Click(object sender, RoutedEventArgs e)
    {
        if (SignalTimeframeBox.SelectedItem is not ComboBoxItem item || string.IsNullOrWhiteSpace(item.Content?.ToString()))
            return;

        var timeframe = item.Content.ToString()!;
        var requestUrl = $"/api/analysis/signal/gold?timeframe={Uri.EscapeDataString(timeframe)}";
        AnalyzeSignalButton.IsEnabled = false;
        AnalyzeSignalButton.Content = "Зареждам…";
        SignalRequestText.Text = $"GET https://tradersview.deriv.com{requestUrl}";
        ResetSignalResponseFields("Изчакване…");

        try
        {
            using var response = await signalHttpClient.GetAsync(requestUrl);
            var content = await response.Content.ReadAsStringAsync();
            SignalRequestText.Text = $"{(int)response.StatusCode} {response.ReasonPhrase} · timeframe={timeframe}";
            UpdateSignalFields(content, new SignalResponse());

            if (!response.IsSuccessStatusCode)
                SignalRequestText.Text += $" · заявката върна HTTP {(int)response.StatusCode}";
        }
        catch (Exception)
        {
            SignalRequestText.Text = $"Грешка · timeframe={timeframe}";
            ResetSignalResponseFields("Неуспешна заявка към TradersView.");
        }
        finally
        {
            AnalyzeSignalButton.IsEnabled = true;
            AnalyzeSignalButton.Content = "Анализирай";
        }
    }

    private void UpdateSignalFields(string content, SignalResponse response)
    {
        SignalActionText.Text = "--";
        SignalTotalConfidenceText.Text = "--";
        SignalEntryText.Text = "--";
        SignalStopLossText.Text = "--";
        SignalTakeProfitText.Text = "--";
        ResetSignalResponseFields();

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            SignalActionText.Text = FindJsonValue(root, "signal", "action", "direction", "recommendation") ?? "--";
            SignalTotalConfidenceText.Text = $"{response.ConfidenceLabel} ({response.TotalConfidence}%)";
            SignalEntryText.Text = FindJsonValue(root, "entry", "entryPrice", "open") ?? "--";
            SignalStopLossText.Text = FindJsonValue(root, "stopLoss", "stop_loss", "sl") ?? "--";
            SignalTakeProfitText.Text = FindJsonValue(root, "takeProfit", "take_profit", "tp", "target") ?? "--";
            ConfidenceComponent_1.Text = FormatConfidenceComponent(root, 0);
            ConfidenceComponent_2.Text = FormatConfidenceComponent(root, 1);
            ConfidenceComponent_3.Text = FormatConfidenceComponent(root, 2);
            SignalBullishFactorsText.Text = FormatSignalItems(root, "bullish_factors", "label", "explanation");
            SignalBearishFactorsText.Text = FormatSignalItems(root, "bearish_factors", "label", "explanation");
            SignalRiskFactorsText.Text = FormatSignalItems(root, "risk_factors", "label", "explanation");
            SignalTradeEntryText.Text = FindTradeLevelValue(root, "entry");
            SignalTradeTargetText.Text = FindTradeLevelValue(root, "target");
            SignalTradeStopLossText.Text = FindTradeLevelValue(root, "stop_loss");
            SignalRiskRewardRatioText.Text = FindTradeLevelValue(root, "risk_reward_ratio");
        }
        catch (JsonException)
        {
            ResetSignalResponseFields(string.IsNullOrWhiteSpace(content) ? "Празен response." : content.Trim());
        }
    }

    private void ResetSignalResponseFields(string value = "--")
    {
        SignalTotalConfidenceText.Text = value;
        ConfidenceComponent_1.Text = value;
        ConfidenceComponent_2.Text = value;
        ConfidenceComponent_3.Text = value;
        SignalBullishFactorsText.Text = value;
        SignalBearishFactorsText.Text = value;
        SignalRiskFactorsText.Text = value;
        SignalTradeEntryText.Text = value;
        SignalTradeTargetText.Text = value;
        SignalTradeStopLossText.Text = value;
        SignalRiskRewardRatioText.Text = value;
    }

    private static string FindTradeLevelValue(JsonElement root, string name)
    {
        if (TryFindJsonElement(root, out var tradeLevels, "trade_levels") &&
            FindJsonValue(tradeLevels, name) is { } nestedValue)
            return nestedValue;

        return FindJsonValue(root, name) ?? "--";
    }

    private static string FormatSignalItems(JsonElement root, string propertyName, params string[] fields)
    {
        if (!TryFindJsonElement(root, out var value, propertyName) || value.ValueKind != JsonValueKind.Array)
            return "--";

        var items = value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object)
                return JsonElementToDisplayString(item);

            var values = fields.Select(field =>
            {
                var display = FindJsonValue(item, field) ?? "--";
                return $"{field}: {display}";
            });
            return string.Join(" · ", values);
        }).ToArray();

        return items.Length == 0 ? "--" : string.Join(Environment.NewLine, items);
    }

    private static string FormatConfidenceComponent(JsonElement root, int index)
    {
        if (!TryFindJsonElement(root, out var components, "confidence_components") ||
            components.ValueKind != JsonValueKind.Array ||
            components.GetArrayLength() <= index)
            return "--";

        var component = components[index];
        if (component.ValueKind != JsonValueKind.Object)
            return JsonElementToDisplayString(component);

        var name = FindJsonValue(component, "name") ?? "--";
        var score = FindJsonValue(component, "score") ?? "--";
        var maxScore = FindJsonValue(component, "max_score") ?? "--";
        var details = FindJsonValue(component, "details") ?? "--";
        return string.Join(Environment.NewLine, name, $"{score} / {maxScore}", details);
    }

    private static string? FindJsonValue(JsonElement element, params string[] names)
    {
        return TryFindJsonElement(element, out var value, names) ? JsonElementToDisplayString(value) : null;
    }

    private static bool TryFindJsonElement(JsonElement element, out JsonElement result, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    result = property.Value;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindJsonElement(property.Value, out result, names)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindJsonElement(item, out result, names)) return true;
            }
        }

        result = default;
        return false;
    }

    private static string JsonElementToDisplayString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "--",
        JsonValueKind.Number => element.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "--",
        _ => element.GetRawText()
    };

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
