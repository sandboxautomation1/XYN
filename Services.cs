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
    private GoldPrice? lastPrice;
    private readonly string url = "https://c--79d25a68-5661-49cf-95ac-988291b20873-prod.lovable.cloud";
    private readonly string key = "sb_publishable_gLhzmPmIa9vh8OFrNL63RQ_vAWsXY2I";
    private string? token;
    private List<MarketCandle> marketHistory = [];
    public SourceSettings Sources { get; private set; } = new();
    public IReadOnlyList<MarketCandle> MarketHistory => marketHistory;
    public MarketSnapshot? MarketSnapshot { get; private set; }
    public Mt5Quote? CurrentMt5Quote { get; private set; }
    public GoldPrice? CurrentMt5Price { get; private set; }
    public IReadOnlyList<ForecastPoint> Forecasts { get; private set; } = [];
    public List<NewsItem> News { get; } = [];
    public List<Alert> Alerts { get; } = [];
    public Signal? CurrentSignal { get; private set; }
    public string Briefing { get; private set; } = "Брифингът се генерира при обновяване на анализа.";

    public GoldDeskService() => LoadSources();
    private void LoadSources() { try { if (File.Exists(sourcesFile)) Sources = JsonConvert.DeserializeObject<SourceSettings>(File.ReadAllText(sourcesFile)) ?? new SourceSettings(); } catch { Sources = new SourceSettings(); } }
    public void SaveSources() { Directory.CreateDirectory(Path.GetDirectoryName(sourcesFile)!); File.WriteAllText(sourcesFile, JsonConvert.SerializeObject(Sources, Formatting.Indented)); }
    public void ClearPriceCache() { lastPrice = null; marketHistory = []; MarketSnapshot = null; Forecasts = []; CurrentMt5Quote = null; CurrentMt5Price = null; }
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
        lastPrice = CurrentMt5Price;
        Forecasts = BuildForecasts(CurrentMt5Price);
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
            if ((int?)response.StatusCode != 429 || attempt == 2) throw new InvalidOperationException($"Доставчикът ограничи заявките (HTTP {(int?)response.StatusCode ?? 0}). Опитайте отново след малко.");
            await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
        }
        throw new InvalidOperationException("Доставчикът временно не отговаря.");
    }

    public Task<GoldPrice> GetPriceAsync() => CurrentMt5Price is not null ? Task.FromResult(CurrentMt5Price) : throw new InvalidOperationException("Няма live цена от MT5. Проверете дали MT5 terminal и bridge-ът работят.");

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

    private static List<MarketCandle> ReadTwelveDataHistory(JObject root)
    {
        var values = root["values"] as JArray;
        if (values is null) return [];
        var output = new List<MarketCandle>();
        foreach (var item in values.OfType<JObject>())
        {
            if (!DateTime.TryParse(item.Value<string>("datetime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)) continue;
            if (!decimal.TryParse(item.Value<string>("open"), NumberStyles.Any, CultureInfo.InvariantCulture, out var open) || !decimal.TryParse(item.Value<string>("high"), NumberStyles.Any, CultureInfo.InvariantCulture, out var high) || !decimal.TryParse(item.Value<string>("low"), NumberStyles.Any, CultureInfo.InvariantCulture, out var low) || !decimal.TryParse(item.Value<string>("close"), NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
            output.Add(new MarketCandle(timestamp.ToLocalTime(), open, high, low, close));
        }
        return output.OrderBy(x => x.Timestamp).ToList();
    }
    private static decimal ReadDecimal(JToken value, decimal fallback) => value.Type == JTokenType.Float || value.Type == JTokenType.Integer ? value.Value<decimal>() : decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private IReadOnlyList<ForecastPoint> BuildForecasts(GoldPrice price) { if (marketHistory.Count == 0) return []; var daily = marketHistory.TakeLast(Math.Min(48, marketHistory.Count)).ToList(); var weekly = marketHistory.TakeLast(Math.Min(192, marketHistory.Count)).ToList(); return [BuildForecast("пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ 24 пїЅпїЅпїЅпїЅ", price.Price, daily), BuildForecast("пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅпїЅ", price.Price, weekly)]; }
    private static ForecastPoint BuildForecast(string horizon, decimal current, List<MarketCandle> candles) { var range = candles.Count == 0 ? current * .01m : candles.Max(x => x.High) - candles.Min(x => x.Low); var drift = candles.Count < 2 ? 0 : (candles[^1].Close - candles[0].Close) / candles[0].Close; var scale = horizon.Contains("седмица", StringComparison.OrdinalIgnoreCase) ? .55m : .22m; var center = current * (1 + Math.Clamp(drift, -.04m, .04m) * scale); var halfRange = Math.Max(current * (horizon.Contains("седмица", StringComparison.OrdinalIgnoreCase) ? .006m : .0025m), range * scale); return new ForecastPoint(horizon, center - halfRange, center + halfRange, center, (center - current) / current * 100, DateTime.Now); }
    private static List<MarketCandle> ReadHistory(JObject result) { var timestamps = result["timestamp"]?.Values<long>().ToArray() ?? []; var quote = result["indicators"]?["quote"]?[0]; if (quote is null) return []; var opens = quote["open"]?.ToArray() ?? []; var highs = quote["high"]?.ToArray() ?? []; var lows = quote["low"]?.ToArray() ?? []; var closes = quote["close"]?.ToArray() ?? []; var count = new[] { timestamps.Length, opens.Length, highs.Length, lows.Length, closes.Length }.Min(); var history = new List<MarketCandle>(count); for (var i = 0; i < count; i++) { if (opens[i].Type == JTokenType.Null || highs[i].Type == JTokenType.Null || lows[i].Type == JTokenType.Null || closes[i].Type == JTokenType.Null) continue; var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).LocalDateTime; history.Add(new MarketCandle(timestamp, opens[i].Value<decimal>(), highs[i].Value<decimal>(), lows[i].Value<decimal>(), closes[i].Value<decimal>())); } return history; }
    private string BuildBriefing(GoldPrice price, Signal signal, int newsCount, int bullish, int bearish) => $"пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ\nпїЅпїЅпїЅ {price.UpdatedAt:dd MMMM yyyy пїЅ.} пїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅ пїЅпїЅ {price.Price:0.00} USD ({price.Symbol}), пїЅ {(price.ChangePct ?? 0):0.00}% пїЅпїЅпїЅпїЅпїЅпїЅпїЅ. пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅ пїЅ пїЅ{ActionLabel(signal.Action)}пїЅ пїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ {signal.Confidence}%.\n\nпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅ {price.Source}; пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅ {newsCount} пїЅпїЅпїЅпїЅпїЅпїЅ: {bullish} пїЅпїЅпїЅпїЅ пїЅ {bearish} пїЅпїЅпїЅпїЅ.\n\nпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅ пїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅ пїЅ RSS пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ, пїЅ пїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅ.";
    public async Task GenerateAnalysisAsync(GoldPrice price) { var recent = News.Take(40).ToList(); var bull = recent.Count(n => n.Sentiment == "bullish"); var bear = recent.Count(n => n.Sentiment == "bearish"); var action = bull > bear + 1 ? "buy" : bear > bull + 1 ? "sell" : "hold"; var confidence = Math.Min(92, 52 + Math.Abs(bull - bear) * 8); var levels = LocalSignalCalculator.Calculate(price.Price, marketHistory); CurrentSignal = new Signal { Levels = levels, Action = action, Confidence = confidence, Risk = confidence > 75 ? "medium" : "high", Rationale = $"Анализирани са {recent.Count} новини: {bull} бичи и {bear} мечи. Текущата цена е {price.Price:0.00}.", KeyFactors = [$"Настроение: {bull} бичи / {bear} мечи", $"Източник на цената: {price.Source}", $"Локални нива: {marketHistory.Count} OHLC свещи", "Локален детерминиран анализ"], Invalidation = "Неочаквани макроикономически данни или обръщане на индекса на щатския долар." }; Briefing = BuildBriefing(price, CurrentSignal, recent.Count, bull, bear); await Task.CompletedTask; }
    public async Task AnalyzeManualAsync(string text) { if (text.Length < 8) throw new InvalidOperationException("пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅ пїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅ пїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅ."); var lower = text.ToLowerInvariant(); var bull = new[] { "rise", "gain", "strong", "buy", "пїЅпїЅпїЅпїЅ", "пїЅпїЅпїЅпїЅпїЅ" }.Count(x => lower.Contains(x)); var bear = new[] { "fall", "drop", "weak", "sell", "пїЅпїЅпїЅпїЅ", "пїЅпїЅпїЅпїЅ" }.Count(x => lower.Contains(x)); News.Insert(0, new NewsItem { Source = "пїЅпїЅпїЅпїЅпїЅ", Title = text.Split('\n', '.', StringSplitOptions.RemoveEmptyEntries)[0].Trim()[..Math.Min(140, text.Length)], Url = "manual://" + Guid.NewGuid(), PublishedAt = DateTime.Now, Sentiment = bull > bear ? "bullish" : bear > bull ? "bearish" : "neutral", Impact = Math.Min(5, Math.Max(1, Math.Abs(bull - bear) + 1)), Summary = "пїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅ пїЅпїЅпїЅпїЅпїЅ." }); await Task.CompletedTask; }
    public static string ActionLabel(string a) => a == "buy" ? "КУПУВАЙ" : a == "sell" ? "ПРОДАВАЙ" : "ИЗЧАКАЙ";
}

