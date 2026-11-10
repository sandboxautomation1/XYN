using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using RestSharp;

namespace GoldDesk.Desktop;

public sealed class GoldDeskService
{
    private readonly RestClient http = new(new RestClientOptions { Timeout = TimeSpan.FromSeconds(20) });
    private readonly string sourcesFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoldDesk", "sources.json");
    private readonly string url = "https://c--79d25a68-5661-49cf-95ac-988291b20873-prod.lovable.cloud";
    private readonly string key = "sb_publishable_gLhzmPmIa9vh8OFrNL63RQ_vAWsXY2I";
    private string? token;
    private List<MarketCandle> marketHistory = [];

    public SourceSettings Sources { get; private set; } = new();
    public IReadOnlyList<MarketCandle> MarketHistory => marketHistory;
    public MarketSnapshot? MarketSnapshot { get; private set; }
    public Mt5Quote? CurrentMt5Quote { get; private set; }
    public GoldPrice? CurrentMt5Price { get; private set; }
    public GoldPrice? CurrentTwelveDataPrice { get; private set; }
    public IReadOnlyList<ForecastPoint> Forecasts { get; private set; } = [];
    public List<NewsItem> News { get; } = [];
    public List<Alert> Alerts { get; } = [];
    public Signal? CurrentSignal { get; private set; }
    public string Briefing { get; private set; } = "Брифингът се генерира при обновяване на анализа.";

    public GoldDeskService() => LoadSources();

    private void LoadSources()
    {
        try
        {
            if (File.Exists(sourcesFile))
                Sources = JsonConvert.DeserializeObject<SourceSettings>(File.ReadAllText(sourcesFile)) ?? new SourceSettings();
        }
        catch { Sources = new SourceSettings(); }
    }

    public void SaveSources()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sourcesFile)!);
        File.WriteAllText(sourcesFile, JsonConvert.SerializeObject(Sources, Formatting.Indented));
    }

    public void ClearPriceCache()
    {
        marketHistory = [];
        MarketSnapshot = null;
        Forecasts = [];
        CurrentMt5Quote = null;
        CurrentMt5Price = null;
        CurrentTwelveDataPrice = null;
    }

    public void ApplyMt5History(IEnumerable<MarketCandle> candles) => marketHistory = candles.OrderBy(x => x.Timestamp).ToList();

    public GoldPrice ApplyMt5Quote(Mt5PriceUpdate update)
    {
        var live = update.Last > 0 ? update.Last : (update.Bid + update.Ask) / 2;
        var previous = marketHistory.Count > 1 ? marketHistory[^2].Close : marketHistory.LastOrDefault()?.Open;
        var change = previous is null or 0 ? null : (live - previous) / previous * 100;
        var candle = marketHistory.LastOrDefault();
        var open = candle?.Open ?? live;
        var high = Math.Max(candle?.High ?? live, live);
        var low = Math.Min(candle?.Low ?? live, live);
        MarketSnapshot = new MarketSnapshot(open, high, low, live, change, update.Timestamp);
        CurrentMt5Quote = new Mt5Quote(update.Symbol, update.Bid, update.Ask, update.Last, update.Volume, update.Timestamp);
        CurrentMt5Price = new GoldPrice(update.Symbol, live, change, "MT5", update.Timestamp);
        Forecasts = BuildForecasts(live, marketHistory);
        return CurrentMt5Price;
    }

    private RestRequest Request(Method method, string path, object? body = null)
    {
        var request = new RestRequest(url + path, method).AddHeader("apikey", key);
        if (token != null) request.AddOrUpdateHeader("Authorization", "Bearer " + token);
        if (body != null) request.AddStringBody(JsonConvert.SerializeObject(body), DataFormat.Json);
        return request;
    }

    public async Task<(bool Ok, string Message)> LoginAsync(string email, string password, bool signup)
    {
        var request = signup ? Request(Method.Post, "/auth/v1/signup", new { email, password }) : Request(Method.Post, "/auth/v1/token?grant_type=password", new { email, password });
        var response = await http.ExecuteAsync(request);
        var text = response.Content ?? string.Empty;
        if (!response.IsSuccessful) return (false, signup ? "Регистрацията не успя. Проверете данните." : "Грешен имейл или парола.");
        var json = JObject.Parse(text);
        token = json.Value<string>("access_token");
        return (true, signup && token == null ? "Проверете пощата си за потвърждение." : "");
    }

    public void Logout() => token = null;

    private async Task<string> GetExternalStringAsync(string requestUrl)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var request = new RestRequest(requestUrl, Method.Get).AddHeader("User-Agent", "GoldDesk/1.0");
            var response = await http.ExecuteAsync(request);
            if (response.IsSuccessful) return response.Content ?? string.Empty;
            if ((int?)response.StatusCode != 429 || attempt == 2)
                throw new InvalidOperationException($"Доставчикът върна HTTP {(int?)response.StatusCode ?? 0}: {response.StatusDescription ?? "неизвестна грешка"}.");
            await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
        }
        throw new InvalidOperationException("Доставчикът временно не отговаря.");
    }

    public async Task<GoldPrice> RefreshTwelveDataAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = Sources.TwelveDataApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Въведете Twelve Data API key в таба „Източници“.");

        var symbol = string.IsNullOrWhiteSpace(Sources.Symbol) ? "XAU/USD" : Sources.Symbol.Trim();
        var interval = NormalizeTwelveInterval(Sources.TwelveDataInterval);
        var query = $"https://api.twelvedata.com/time_series?symbol={Uri.EscapeDataString(symbol)}&interval={Uri.EscapeDataString(interval)}&outputsize=288&timezone=UTC&apikey={Uri.EscapeDataString(apiKey)}";
        var text = await GetExternalStringAsync(query);
        cancellationToken.ThrowIfCancellationRequested();
        var root = JObject.Parse(text);
        ThrowIfTwelveDataError(root);
        var history = ReadTwelveDataHistory(root);
        if (history.Count == 0) throw new InvalidOperationException("Twelve Data не върна OHLC свещи за XAU/USD.");

        marketHistory = history;
        var latest = history[^1];
        var previous = history.Count > 1 ? history[^2].Close : latest.Open;
        decimal? change = previous == 0 ? null : (latest.Close - previous) / previous * 100;
        MarketSnapshot = new MarketSnapshot(latest.Open, latest.High, latest.Low, latest.Close, change, latest.Timestamp);
        CurrentTwelveDataPrice = new GoldPrice("XAU/USD", latest.Close, change, "Twelve Data", latest.Timestamp);
        Forecasts = BuildForecasts(latest.Close, history);
        return CurrentTwelveDataPrice;
    }

    private static string NormalizeTwelveInterval(string? interval) => interval?.Trim().ToLowerInvariant() switch
    {
        "1min" or "1m" => "1min",
        "5min" or "5m" => "5min",
        "15min" or "15m" => "15min",
        "30min" or "30m" => "30min",
        "1h" or "60min" => "1h",
        _ => "5min"
    };

    private static void ThrowIfTwelveDataError(JObject root)
    {
        var status = root.Value<string>("status");
        var message = root.Value<string>("message") ?? root.Value<string>("code");
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) || root["values"] is null)
            throw new InvalidOperationException("Twelve Data: " + (message ?? "невалиден или недостъпен отговор"));
    }

    private static List<MarketCandle> ReadTwelveDataHistory(JObject root)
    {
        var values = root["values"] as JArray;
        if (values is null) return [];
        var output = new List<MarketCandle>();
        foreach (var item in values.OfType<JObject>())
        {
            if (!DateTime.TryParse(item.Value<string>("datetime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) continue;
            if (!decimal.TryParse(item.Value<string>("open"), NumberStyles.Any, CultureInfo.InvariantCulture, out var open) ||
                !decimal.TryParse(item.Value<string>("high"), NumberStyles.Any, CultureInfo.InvariantCulture, out var high) ||
                !decimal.TryParse(item.Value<string>("low"), NumberStyles.Any, CultureInfo.InvariantCulture, out var low) ||
                !decimal.TryParse(item.Value<string>("close"), NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
            output.Add(new MarketCandle(timestamp.ToLocalTime(), open, high, low, close));
        }
        return output.OrderBy(x => x.Timestamp).ToList();
    }

    private static IReadOnlyList<ForecastPoint> BuildForecasts(decimal current, IReadOnlyList<MarketCandle> candles)
    {
        if (candles.Count == 0 || current <= 0) return [];
        return [
            BuildForecast("30m", current, candles, 6),
            BuildForecast("1h", current, candles, 12),
            BuildForecast("4h", current, candles, 48)
        ];
    }

    private static ForecastPoint BuildForecast(string horizon, decimal current, IReadOnlyList<MarketCandle> candles, int barsAhead)
    {
        var sample = candles.TakeLast(Math.Min(96, candles.Count)).ToList();
        var first = sample[0].Close;
        var last = sample[^1].Close;
        var driftPerBar = sample.Count < 2 ? 0m : (last - first) / first / (sample.Count - 1);
        var drift = Math.Clamp(driftPerBar * barsAhead, -0.025m, 0.025m);
        var returns = sample.Skip(1).Select((c, i) => first == 0 ? 0m : Math.Abs((c.Close - sample[i].Close) / sample[i].Close)).ToList();
        var volatility = returns.Count == 0 ? current * 0.001m : current * returns.Average() * (decimal)Math.Sqrt(barsAhead);
        var center = current * (1 + drift);
        var halfRange = Math.Max(current * 0.0008m, volatility * 1.96m);
        return new ForecastPoint(horizon, Math.Max(0, center - halfRange), center + halfRange, center, (center - current) / current * 100, DateTime.Now);
    }

    public Task<GoldPrice> GetPriceAsync() => Sources.Provider.Equals("twelve_data", StringComparison.OrdinalIgnoreCase)
        ? RefreshTwelveDataAsync()
        : CurrentMt5Price is not null ? Task.FromResult(CurrentMt5Price) : throw new InvalidOperationException("Няма live цена от MT5.");

    public async Task RefreshNewsAsync()
    {
        var feeds = new List<NewsFeed>();
        if (Sources.UseDefaultNews) feeds.Add(new NewsFeed { Name = "Google News", Url = "https://news.google.com/rss/search?q=gold+price+XAUUSD&hl=en-US&gl=US&ceid=US:en" });
        feeds.AddRange(Sources.Feeds);
        var found = new List<NewsItem>();
        foreach (var f in feeds)
        {
            try
            {
                var xml = await GetExternalStringAsync(f.Url);
                var doc = XDocument.Parse(xml);
                foreach (var item in doc.Descendants("item").Take(15))
                {
                    var title = item.Element("title")?.Value?.Trim();
                    var link = item.Element("link")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link) || News.Any(n => n.Url == link)) continue;
                    var text = title.ToLowerInvariant();
                    var bear = new[] { "fall", "drop", "sell", "weak", "inflation", "hawkish" }.Count(x => text.Contains(x));
                    var bull = new[] { "rise", "gain", "buy", "strong", "demand", "cut" }.Count(x => text.Contains(x));
                    found.Add(new() { Source = f.Name, Title = title, Url = link, PublishedAt = DateTime.Now, Sentiment = bull > bear ? "bullish" : bear > bull ? "bearish" : "neutral", Impact = Math.Min(5, Math.Max(1, Math.Abs(bull - bear) + (text.Contains("fed") ? 2 : 0))), Summary = "Автоматично класифицирана новина от RSS източника.", Reasoning = "Ключови думи и контекст на заглавието." });
                }
            }
            catch { }
        }
        News.InsertRange(0, found.Take(14));
        while (News.Count > 60) News.RemoveAt(News.Count - 1);
        if (found.Count == 0) Alerts.Add(new() { Title = "Няма нови новини", Body = "Не успях да заредя новини от източниците." });
        foreach (var n in found.Where(n => n.Impact >= 4)) Alerts.Insert(0, new() { Title = $"{(n.Sentiment == "bullish" ? "▲ Бичи" : n.Sentiment == "bearish" ? "▼ Мечи" : "● Неутрална")} новина ({n.Impact}/5)", Body = n.Title });
    }

    private string BuildBriefing(GoldPrice price, Signal signal, int newsCount, int bullish, int bearish) =>
        $"ПАЗАРЕН БРИФИНГ\n{price.UpdatedAt:dd.MM.yyyy HH:mm} · XAU/USD {price.Price:0.00} USD · промяна {(price.ChangePct ?? 0):0.00}%.\n\nПрогнозът е изчислен от {price.Source} чрез последните {marketHistory.Count} OHLC свещи. Локален сигнал: {ActionLabel(signal.Action)} с увереност {signal.Confidence}%.\n\nНовини: {newsCount} ({bullish} бичи / {bearish} мечи). Прогнозите са статистически оценки, не гарантирани бъдещи цени.";

    public async Task GenerateAnalysisAsync(GoldPrice price)
    {
        var recent = News.Take(40).ToList();
        var bull = recent.Count(n => n.Sentiment == "bullish");
        var bear = recent.Count(n => n.Sentiment == "bearish");
        var technicalScore = 0;
        decimal trendPct = 0;
        if (marketHistory.Count >= 2)
        {
            var window = marketHistory.TakeLast(Math.Min(20, marketHistory.Count)).ToList();
            trendPct = (window[^1].Close - window[0].Close) / window[0].Close * 100;
            if (trendPct >= 0.15m) technicalScore = 1;
            else if (trendPct <= -0.15m) technicalScore = -1;
        }
        var score = bull - bear + technicalScore;
        var action = score > 0 ? "buy" : score < 0 ? "sell" : "hold";
        var confidence = Math.Min(92, 52 + Math.Min(4, Math.Abs(score)) * 8 + (technicalScore != 0 ? 4 : 0));
        var levels = LocalSignalCalculator.Calculate(price.Price, marketHistory);
        CurrentSignal = new Signal { Levels = levels, Action = action, Confidence = confidence, Risk = confidence > 75 ? "medium" : "high", Rationale = $"Анализирани са {recent.Count} новини: {bull} бичи и {bear} мечи. Технически тренд: {trendPct:+0.00;-0.00;0.00}%.", KeyFactors = [$"Настроение: {bull} бичи / {bear} мечи", $"Технически тренд: {trendPct:+0.00;-0.00;0.00}%", $"Източник: {price.Source}", $"OHLC свещи: {marketHistory.Count}"], Invalidation = "Неочаквани макроикономически данни или рязко обръщане на тренда." };
        Briefing = BuildBriefing(price, CurrentSignal, recent.Count, bull, bear);
        await Task.CompletedTask;
    }

    public async Task AnalyzeManualAsync(string text)
    {
        if (text.Length < 8) throw new InvalidOperationException("Въведете поне осем знака за анализ.");
        var lower = text.ToLowerInvariant();
        var bull = new[] { "rise", "gain", "strong", "buy", "ръст", "куп" }.Count(x => lower.Contains(x));
        var bear = new[] { "fall", "drop", "weak", "sell", "спад", "прод" }.Count(x => lower.Contains(x));
        News.Insert(0, new NewsItem { Source = "Ръчно", Title = text.Split('\n', '.', StringSplitOptions.RemoveEmptyEntries)[0].Trim()[..Math.Min(140, text.Length)], Url = "manual://" + Guid.NewGuid(), PublishedAt = DateTime.Now, Sentiment = bull > bear ? "bullish" : bear > bull ? "bearish" : "neutral", Impact = Math.Min(5, Math.Max(1, Math.Abs(bull - bear) + 1)), Summary = "Ръчно добавена информация." });
        await Task.CompletedTask;
    }

    public static string ActionLabel(string a) => a == "buy" ? "КУПУВАЙ" : a == "sell" ? "ПРОДАВАЙ" : "ИЗЧАКАЙ";
}
