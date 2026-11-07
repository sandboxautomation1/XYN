using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using RestSharp;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using XYN.Signal;
using System.Net.Http;

namespace GoldDesk.Desktop;

public partial class MainWindow : Window
{
    private static readonly RestClient signalClient = new(new RestClientOptions("https://tradersview.deriv.com") { Timeout = TimeSpan.FromSeconds(20) });
    private static readonly RestClient tradersUnionClient = new(new RestClientOptions("https://quotes.tradersunion.com") { Timeout = TimeSpan.FromSeconds(20) });
    private static readonly RestClient bridgeHealthClient = new(new RestClientOptions("http://127.0.0.1:8000") { Timeout = TimeSpan.FromSeconds(2) });
    private readonly GoldDeskService service;
    private readonly Mt5BridgeClient mt5Bridge = new();
    private Process? mt5ServerProcess;

    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool isRefreshing;
    private bool isInitialized;
    private bool isChangingTimeframe;
    private bool tradersUnionLoaded;
    private bool tradersUnionTechnicalLoaded;
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
                ChartMetaText.Text = $"MT5  ·  {selectedTimeframe}  ·  live tick";
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
            ChartMetaText.Text = "MT5 bridge server  ·  uv run  ·  127.0.0.1:8000";

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
                var response = await bridgeHealthClient.ExecuteAsync(new RestRequest("/health", Method.Get));
                if (response.IsSuccessful) return true;
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
        ChartMetaText.Text = $"MT5 REST   ·   зареждане на {selectedSymbol}…";

        try
        {
            await mt5Bridge.RestartAsync(selectedSymbol, selectedTimeframe, 100);
            isInitialized = true;
            PriceMeta.Text = $"MT5  ·  {selectedSymbol}";
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
 
        PriceMeta.Text = $"{price.Symbol}  ·  {price.Source}  ·  {price.ChangePct:+0.00;-0.00;0.00}%";
        var snapshot = service.MarketSnapshot;
        OpenText.Text = snapshot?.Open.ToString("0.00") ?? "--";
        HighText.Text = snapshot?.High.ToString("0.00") ?? "--";
        LowText.Text = snapshot?.Low.ToString("0.00") ?? "--";
        CloseText.Text = snapshot?.Close.ToString("0.00") ?? price.Price.ToString("0.00");
        LastUpdatedText.Text = $"{price.Source}   ·   последно обновяване {price.UpdatedAt:HH:mm:ss}";
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
        ChartMetaText.Text = $"{price.Source}   ·   {service.MarketHistory.Count} свещи   ·   обновено {price.UpdatedAt:HH:mm:ss}";
        UpdateTechnicalIndicators();
        PriceChart.InvalidateVisual();
    }

    private void OnMt5Ohlc(IReadOnlyList<MarketCandle> candles, string symbol)
    {
        Dispatcher.Invoke(() => { 
            service.ApplyMt5History(candles); 
            PriceChart.Candles = service.MarketHistory;
            UpdateTechnicalIndicators(); 
                ChartMetaText.Text = $"MT5 REST   ·   {symbol}   ·   {selectedTimeframe}   ·   {candles.Count} свещи"; PriceChart.InvalidateVisual(); 
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
        ChartMetaText.Text = $"MT5 REST   ·   зареждане на {selectedTimeframe} данни…";
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
            var response = await signalClient.ExecuteAsync(new RestRequest(requestUrl, Method.Get));
            var content = response.Content ?? string.Empty;
            SignalRequestText.Text = $"{(int?)response.StatusCode ?? 0} {response.StatusDescription}  ·  timeframe={timeframe}";
            UpdateSignalFields(content, new SignalResponse());

            if (!response.IsSuccessful)
                SignalRequestText.Text += $"   ·   заявката върна HTTP {(int?)response.StatusCode ?? 0}";
        }
        catch (Exception)
        {
            SignalRequestText.Text = $"Грешка   ·   timeframe={timeframe}";
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
            
            var root = JObject.Parse(content);
            SignalActionText.Text = FindJsonValue(root, "signal", "action", "direction", "recommendation") ?? "--";
            SignalTotalConfidenceText.Text = $"{response.ConfidenceLabel} ({response.TotalConfidence}%)";
            SignalEntryText.Text = FindJsonValue(root, "entry", "entryPrice", "open") ?? "--";
            SignalStopLossText.Text = FindJsonValue(root, "stopLoss", "stop_loss", "sl") ?? "--";
            SignalTakeProfitText.Text = FindJsonValue(root, "takeProfit", "take_profit", "tp", "target") ?? "--";
            ConfidenceComponent_1.Text = FormatConfidenceComponent(root, 0);
            ConfidenceComponent_2.Text = FormatConfidenceComponent(root, 1);
            ConfidenceComponent_3.Text = FormatConfidenceComponent(root, 2);
            BullishFactor_1.Text = FormatBullishFactor(root, 0);
            BullishFactor_2.Text = FormatBullishFactor(root, 1);
            BullishFactor_3.Text = FormatBullishFactor(root, 2);
            BearishFactor_1.Text = FormatBearishFactor(root, 0);
            BearishFactor_2.Text = FormatBearishFactor(root, 1);
            BearishFactor_3.Text = FormatBearishFactor(root, 2);
            RiskFactor_1.Text = FormatRiskFactor(root, 0);
            RiskFactor_2.Text = FormatRiskFactor(root, 1);
            RiskFactor_3.Text = FormatRiskFactor(root, 2);
            SignalTradeEntryText.Text = FindTradeLevelValue(root, "entry");
            SignalTradeTargetText.Text = FindTradeLevelValue(root, "target");
            SignalTradeStopLossText.Text = FindTradeLevelValue(root, "stop_loss");
            SignalRiskRewardRatioText.Text = FindTradeLevelValue(root, "risk_reward_ratio");
        }
        catch (Newtonsoft.Json.JsonException)
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
        BullishFactor_1.Text = value;
        BullishFactor_2.Text = value;
        BullishFactor_3.Text = value;
        BearishFactor_1.Text = value;
        BearishFactor_2.Text = value;
        BearishFactor_3.Text = value;
        RiskFactor_1.Text = value;
        RiskFactor_2.Text = value;
        RiskFactor_3.Text = value;
        SignalTradeEntryText.Text = value;
        SignalTradeTargetText.Text = value;
        SignalTradeStopLossText.Text = value;
        SignalRiskRewardRatioText.Text = value;
    }

    private static string FindTradeLevelValue(JObject root, string name)
    {
        if (TryFindJsonToken(root, out var tradeLevels, "trade_levels") && FindJsonValue(tradeLevels, name) is { } nestedValue)
            return nestedValue;
        return FindJsonValue(root, name) ?? "--";
    }

    private static string FormatSignalItems(JObject root, string propertyName, params string[] fields)
    {
        if (!TryFindJsonToken(root, out var value, propertyName) || value is not JArray array)
            return "--";
        var items = array.Select(item =>
        {
            if (item is not JObject obj) return JTokenToDisplayString(item);
            return string.Join(" Р’ ·  ", fields.Select(field => $"{field}: {FindJsonValue(obj, field) ?? "--"}"));
        }).ToArray();
        return items.Length == 0 ? "--" : string.Join(Environment.NewLine, items);
    }

    private static string FormatBullishFactor(JObject root, int index)
    {
        if (!TryFindJsonToken(root, out var value, "bullish_factors") || value is not JArray array || array.Count <= index)
            return "--";

        if (array[index] is not JObject factor)
            return JTokenToDisplayString(array[index]);

        var label = FindJsonValue(factor, "label") ?? "--";
        var explanation = FindJsonValue(factor, "explanation") ?? "--";
        var strength = FindJsonValue(factor, "strength") ?? "--";
        return string.Join(Environment.NewLine, label, explanation, strength);
    }

    private static string FormatBearishFactor(JObject root, int index)
    {
        if (!TryFindJsonToken(root, out var value, "bearish_factors") || value is not JArray array || array.Count <= index)
            return "--";

        if (array[index] is not JObject factor)
            return JTokenToDisplayString(array[index]);

        var label = FindJsonValue(factor, "label") ?? "--";
        var explanation = FindJsonValue(factor, "explanation") ?? "--";
        var strength = FindJsonValue(factor, "strength") ?? "--";
        return string.Join(Environment.NewLine, label, explanation, strength);
    }

    private static string FormatRiskFactor(JObject root, int index)
    {
        if (!TryFindJsonToken(root, out var value, "risk_factors") || value is not JArray array || array.Count <= index)
            return "--";

        if (array[index] is not JObject factor)
            return JTokenToDisplayString(array[index]);

        var label = FindJsonValue(factor, "label") ?? "--";
        var explanation = FindJsonValue(factor, "explanation") ?? "--";
        var strength = FindJsonValue(factor, "strength") ?? "--";
        return string.Join(Environment.NewLine, label, explanation, strength);
    }

    private static string FormatConfidenceComponent(JObject root, int index)
    {
        if (!TryFindJsonToken(root, out var components, "confidence_components") || components is not JArray array || array.Count <= index)
            return "--";
        var component = array[index];
        if (component is not JObject obj) return JTokenToDisplayString(component);
        var name = FindJsonValue(obj, "name") ?? "--";
        var score = FindJsonValue(obj, "score") ?? "--";
        var maxScore = FindJsonValue(obj, "max_score") ?? "--";
        var details = FindJsonValue(obj, "details") ?? "--";
        return string.Join(Environment.NewLine, name, $"{score} / {maxScore}", details);
    }

    private static string? FindJsonValue(JToken element, params string[] names) =>
        TryFindJsonToken(element, out var value, names) ? JTokenToDisplayString(value) : null;

    private static bool TryFindJsonToken(JToken element, out JToken result, params string[] names)
    {
        if (element is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    result = property.Value;
                    return true;
                }
            }
            foreach (var property in obj.Properties())
                if (TryFindJsonToken(property.Value, out result, names)) return true;
        }
        else if (element is JArray array)
        {
            foreach (var item in array)
                if (TryFindJsonToken(item, out result, names)) return true;
        }
        result = JValue.CreateNull();
        return false;
    }

    private static string JTokenToDisplayString(JToken token) => token.Type switch
    {
        JTokenType.String => token.Value<string>() ?? "--",
        JTokenType.Integer or JTokenType.Float or JTokenType.Boolean => token.ToString(),
        JTokenType.Null or JTokenType.Undefined => "--",
        _ => token.ToString(Newtonsoft.Json.Formatting.None)
    };
    private void Render()
    {
        var signal = service.CurrentSignal;
        if (signal != null)
        {
            CurrentSignalText.Text = GoldDeskService.ActionLabel(signal.Action);
            CurrentSignalText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(signal.Action == "buy" ? "#73D49A" : signal.Action == "sell" ? "#F17C65" : "#AEB8D9"));
            SignalMeta.Text = $"увереност {signal.Confidence}%   ·   риск {(signal.Risk == "medium" ? "среден" : "висок")}   ·   {signal.Horizon}";
            ConfidenceText.Text = $"{signal.Confidence}%";
            PriceChart.BuyAbove = signal.Levels.BuyAbove; PriceChart.SellBelow = signal.Levels.SellBelow; PriceChart.InvalidateVisual();
        }
        BriefingText.Text = service.Briefing;
        NewsList.Items.Clear();
        foreach (var news in service.News)
        {
            var textBlock = new TextBlock { Text = $"{(news.Sentiment == "bullish" ? "БИЧИ" : news.Sentiment == "bearish" ? "МЕЧИ" : "НЕУТРАЛНО")}   ·   въздействие {news.Impact}/5   ·   {news.Source}\n{news.Title}\n{news.Summary}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
            textBlock.MouseLeftButtonUp += (_, _) => { if (Uri.TryCreate(news.Url, UriKind.Absolute, out var uri) && uri.Scheme != "manual") Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); };
            NewsList.Items.Add(textBlock);
        }
    }

    private async void LoadTradersUnion_Click(object sender, RoutedEventArgs e) => await LoadTradersUnionAsync();

    private async Task LoadTradersUnionAsync()
    {
        var symbol = TradersUnionSymbolBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            TradersUnionRequestText.Text = "Въведете symbol ID, например 775.";
            return;
        }

        var requestUrl = $"/api/v3/quote/?symbol={Uri.EscapeDataString(symbol)}&locale=en";
        LoadTradersUnionButton.IsEnabled = false;
        LoadTradersUnionButton.Content = "Зареждам…";
        TradersUnionRequestText.Text = $"GET https://quotes.tradersunion.com{requestUrl}";
        TradersUnionIndicatorsPanel.Children.Clear();

        try
        {
            var response = await tradersUnionClient.ExecuteAsync(new RestRequest(requestUrl, Method.Get));
            var content = response.Content ?? string.Empty;
            TradersUnionRequestText.Text = $"GET https://quotes.tradersunion.com{requestUrl}   ·   {(int?)response.StatusCode ?? 0} {response.StatusDescription}";

            if (!response.IsSuccessful)
            {
                AddTradersUnionField("HTTP response", content.Length == 0 ? "Няма съдържание" : content);
                return;
            }

            var root = JToken.Parse(content);
            var fields = FlattenTradersUnionValues(root, string.Empty).ToList();
            if (fields.Count == 0)
            {
                AddTradersUnionField("response", "Празен JSON response.");
                return;
            }

            foreach (var field in fields)
                AddTradersUnionField(field.Path, field.Value);

            tradersUnionLoaded = true;
            TradersUnionRequestText.Text += $"   ·   {fields.Count} полета";
        }
        catch (Newtonsoft.Json.JsonException)
        {
            AddTradersUnionField("response", "Отговорът не е валиден JSON.");
        }
        catch (Exception ex)
        {
            AddTradersUnionField("Грешка", ex.Message);
        }
        finally
        {
            LoadTradersUnionButton.IsEnabled = true;
            LoadTradersUnionButton.Content = "Зареди индикаторите";
        }
    }

    private async void LoadTradersUnionTechnical_Click(object sender, RoutedEventArgs e) => await LoadTradersUnionTechnicalAsync();

    private async Task LoadTradersUnionTechnicalAsync()
    {
        var symbol = TradersUnionTechnicalSymbolBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            TradersUnionTechnicalRequestText.Text = "Въведете symbol ID, например 775.";
            return;
        }

        var requestUrl = $"/api/v3/informer/technical-analysis/detailed/?symbol={Uri.EscapeDataString(symbol)}";
        LoadTradersUnionTechnicalButton.IsEnabled = false;
        LoadTradersUnionTechnicalButton.Content = "Зареждам…";
        TradersUnionTechnicalRequestText.Text = $"GET https://quotes.tradersunion.com{requestUrl}";
        TradersUnionTechnicalIndicatorsPanel.Children.Clear();

        try
        {
            var response = await tradersUnionClient.ExecuteAsync(new RestRequest(requestUrl, Method.Get));
            var content = response.Content ?? string.Empty;
            TradersUnionTechnicalRequestText.Text = $"GET https://quotes.tradersunion.com{requestUrl}   ·   {(int?)response.StatusCode ?? 0} {response.StatusDescription}";

            if (!response.IsSuccessful)
            {
                AddTechnicalAnalysisField("HTTP response", content.Length == 0 ? "Няма съдържание" : content);
                return;
            }

            var root = JToken.Parse(content);
            var fields = FlattenTradersUnionValues(root, string.Empty).ToList();
            if (fields.Count == 0)
            {
                AddTechnicalAnalysisField("response", "Празен JSON response.");
                return;
            }

            foreach (var field in fields)
                AddTechnicalAnalysisField(field.Path, field.Value);

            tradersUnionTechnicalLoaded = true;
            TradersUnionTechnicalRequestText.Text += $"   ·   {fields.Count} полета";
        }
        catch (Newtonsoft.Json.JsonException)
        {
            AddTechnicalAnalysisField("response", "Отговорът не е валиден JSON.");
        }
        catch (Exception ex)
        {
            AddTechnicalAnalysisField("Грешка", ex.Message);
        }
        finally
        {
            LoadTradersUnionTechnicalButton.IsEnabled = true;
            LoadTradersUnionTechnicalButton.Content = "Зареди анализа";
        }
    }

    private void AddTradersUnionField(string name, string value)
    {
        AddIndicatorField(TradersUnionIndicatorsPanel, name, value);
    }

    private void AddTechnicalAnalysisField(string name, string value)
    {
        AddIndicatorField(TradersUnionTechnicalIndicatorsPanel, name, value);
    }

    private void AddIndicatorField(Panel panel, string name, string value)
    {
        var card = new Border
        {
            Width = 260,
            MinHeight = 82,
            Margin = new Thickness(0, 0, 10, 10),
            Style = (Style)FindResource("DashboardCardStyle")
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "--" : value,
            Foreground = (Brush)FindResource("TextBrush"),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 15,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        card.Child = content;
        panel.Children.Add(card);
    }

    private static IEnumerable<(string Path, string Value)> FlattenTradersUnionValues(JToken token, string path)
    {
        if (token is JObject obj)
        {
            if (!obj.Properties().Any())
            {
                yield return (path, "{}");
                yield break;
            }

            foreach (var property in obj.Properties())
            {
                var propertyPath = string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}";
                foreach (var field in FlattenTradersUnionValues(property.Value, propertyPath))
                    yield return field;
            }
            yield break;
        }

        if (token is JArray array)
        {
            if (array.Count == 0)
            {
                yield return (path, "[]");
                yield break;
            }

            for (var index = 0; index < array.Count; index++)
            {
                var itemPath = string.IsNullOrWhiteSpace(path) ? $"[{index + 1}]" : $"{path}[{index + 1}]";
                foreach (var field in FlattenTradersUnionValues(array[index], itemPath))
                    yield return field;
            }
            yield break;
        }

        yield return (path, JTokenToDisplayString(token));
    }

    private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl && TradersUnionTab is not null && TradersUnionTab.IsSelected && !tradersUnionLoaded)
            await LoadTradersUnionAsync();
        else if (e.Source is TabControl && TradersUnionTechnicalTab is not null && TradersUnionTechnicalTab.IsSelected && !tradersUnionTechnicalLoaded)
            await LoadTradersUnionTechnicalAsync();
    }
}


