using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace GoldDesk.Desktop;

public sealed class GoldDeskService
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
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
    private void LoadSources() { try { if (File.Exists(sourcesFile)) Sources = JsonSerializer.Deserialize<SourceSettings>(File.ReadAllText(sourcesFile)) ?? new SourceSettings(); } catch { Sources = new SourceSettings(); } }
    public void SaveSources() { Directory.CreateDirectory(Path.GetDirectoryName(sourcesFile)!); File.WriteAllText(sourcesFile, JsonSerializer.Serialize(Sources, new JsonSerializerOptions { WriteIndented = true })); }
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

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    { var r = new HttpRequestMessage(method, url + path); r.Headers.Add("apikey", key); if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); if (body != null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"); return r; }
    public async Task<(bool Ok, string Message)> LoginAsync(string email, string password, bool signup)
    { using var req = signup ? Request(HttpMethod.Post, "/auth/v1/signup", new { email, password }) : Request(HttpMethod.Post, "/auth/v1/token?grant_type=password", new { email, password }); using var res = await http.SendAsync(req); var text = await res.Content.ReadAsStringAsync(); if (!res.IsSuccessStatusCode) return (false, signup ? "Регистрацията не успя. Проверете данните." : "Грешен имейл или парола."); using var doc = JsonDocument.Parse(text); if (doc.RootElement.TryGetProperty("access_token", out var at)) token = at.GetString(); return (true, signup && token == null ? "Проверете пощата си за потвърждение." : ""); }
    public void Logout() => token = null;

    private async Task<string> GetExternalStringAsync(string requestUrl)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl); request.Headers.UserAgent.ParseAdd("GoldDesk/1.0"); using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();
            if ((int)response.StatusCode != 429 || attempt == 2) throw new HttpRequestException($"Доставчикът ограничи заявките (HTTP {(int)response.StatusCode}). Опитайте отново след малко.");
            await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1)));
        }
        throw new HttpRequestException("Доставчикът временно не отговаря.");
    }

    public Task<GoldPrice> GetPriceAsync()
    {
        if (CurrentMt5Price is not null)
            return Task.FromResult(CurrentMt5Price);

        throw new InvalidOperationException("Няма live цена от MT5. Проверете дали MT5 terminal и bridge-ът работят.");
    }

    public async Task RefreshNewsAsync()
    { var feeds = new List<NewsFeed>(); if (Sources.UseDefaultNews) feeds.Add(new NewsFeed { Name = "Google News", Url = "https://news.google.com/rss/search?q=gold+price+XAUUSD&hl=en-US&gl=US&ceid=US:en" }); feeds.AddRange(Sources.Feeds); var found = new List<NewsItem>(); foreach (var f in feeds) { try { var xml = await http.GetStringAsync(f.Url); var doc = XDocument.Parse(xml); foreach (var item in doc.Descendants("item").Take(15)) { var title = item.Element("title")?.Value?.Trim(); var link = item.Element("link")?.Value?.Trim(); if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link) || News.Any(n => n.Url == link)) continue; var text = title.ToLowerInvariant(); var bear = new[] { "fall", "drop", "sell", "weak", "inflation", "hawkish" }.Count(x => text.Contains(x)); var bull = new[] { "rise", "gain", "buy", "strong", "demand", "cut" }.Count(x => text.Contains(x)); found.Add(new() { Source = f.Name, Title = title, Url = link, PublishedAt = DateTime.Now, Sentiment = bull > bear ? "bullish" : bear > bull ? "bearish" : "neutral", Impact = Math.Min(5, Math.Max(1, Math.Abs(bull - bear) + (text.Contains("fed") ? 2 : 0))), Summary = "Автоматично класифицирана новина от RSS източника.", Reasoning = "Ключови думи и контекст на заглавието." }); } } catch { } } News.InsertRange(0, found.Take(14)); while (News.Count > 60) News.RemoveAt(News.Count - 1); if (found.Count == 0) Alerts.Add(new() { Title = "Няма нови новини", Body = "Не успях да заредя новини от източниците." }); foreach (var n in found.Where(n => n.Impact >= 4)) Alerts.Insert(0, new() { Title = $"{(n.Sentiment == "bullish" ? "▲ Бичи" : n.Sentiment == "bearish" ? "▼ Мечи" : "● Неутрална")} новина ({n.Impact}/5)", Body = n.Title }); }

    private static List<MarketCandle> ReadTwelveDataHistory(JsonElement root) { if (!root.TryGetProperty("values", out var values)) return []; var output = new List<MarketCandle>(); foreach (var item in values.EnumerateArray()) { if (!DateTime.TryParse(item.GetProperty("datetime").GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp)) continue; if (!decimal.TryParse(item.GetProperty("open").GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var open) || !decimal.TryParse(item.GetProperty("high").GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var high) || !decimal.TryParse(item.GetProperty("low").GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var low) || !decimal.TryParse(item.GetProperty("close").GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var close)) continue; output.Add(new MarketCandle(timestamp.ToLocalTime(), open, high, low, close)); } return output.OrderBy(x => x.Timestamp).ToList(); }
    private static decimal ReadDecimal(JsonElement value, decimal fallback) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private IReadOnlyList<ForecastPoint> BuildForecasts(GoldPrice price) { if (marketHistory.Count == 0) return []; var daily = marketHistory.TakeLast(Math.Min(48, marketHistory.Count)).ToList(); var weekly = marketHistory.TakeLast(Math.Min(192, marketHistory.Count)).ToList(); return [BuildForecast("Следващите 24 часа", price.Price, daily), BuildForecast("Следващата седмица", price.Price, weekly)]; }
    private static ForecastPoint BuildForecast(string horizon, decimal current, List<MarketCandle> candles) { var range = candles.Count == 0 ? current * .01m : candles.Max(x => x.High) - candles.Min(x => x.Low); var drift = candles.Count < 2 ? 0 : (candles[^1].Close - candles[0].Close) / candles[0].Close; var scale = horizon.Contains("седмица", StringComparison.OrdinalIgnoreCase) ? .55m : .22m; var center = current * (1 + Math.Clamp(drift, -.04m, .04m) * scale); var halfRange = Math.Max(current * (horizon.Contains("седмица", StringComparison.OrdinalIgnoreCase) ? .006m : .0025m), range * scale); return new ForecastPoint(horizon, center - halfRange, center + halfRange, center, (center - current) / current * 100, DateTime.Now); }
    private static List<MarketCandle> ReadHistory(JsonElement result) { var timestamps = result.GetProperty("timestamp").EnumerateArray().ToArray(); var quote = result.GetProperty("indicators").GetProperty("quote")[0]; var opens = quote.GetProperty("open").EnumerateArray().ToArray(); var highs = quote.GetProperty("high").EnumerateArray().ToArray(); var lows = quote.GetProperty("low").EnumerateArray().ToArray(); var closes = quote.GetProperty("close").EnumerateArray().ToArray(); var count = new[] { timestamps.Length, opens.Length, highs.Length, lows.Length, closes.Length }.Min(); var history = new List<MarketCandle>(count); for (var i = 0; i < count; i++) { if (opens[i].ValueKind == JsonValueKind.Null || highs[i].ValueKind == JsonValueKind.Null || lows[i].ValueKind == JsonValueKind.Null || closes[i].ValueKind == JsonValueKind.Null) continue; var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).LocalDateTime; history.Add(new MarketCandle(timestamp, opens[i].GetDecimal(), highs[i].GetDecimal(), lows[i].GetDecimal(), closes[i].GetDecimal())); } return history; }
    private string BuildBriefing(GoldPrice price, Signal signal, int newsCount, int bullish, int bearish) => $"НАКРАТКО\nКъм {price.UpdatedAt:dd MMMM yyyy г.} златото е на {price.Price:0.00} USD ({price.Symbol}), с {(price.ChangePct ?? 0):0.00}% промяна. Текущият локален сигнал е „{ActionLabel(signal.Action)}“ при увереност {signal.Confidence}%.\n\nИзточникът е {price.Source}; анализирани са {newsCount} новини: {bullish} бичи и {bearish} мечи.\n\nСигналът е локален детерминиран анализ на цената и RSS заглавията, а не инвестиционен съвет.";
    public async Task GenerateAnalysisAsync(GoldPrice price) { var recent = News.Take(40).ToList(); var bull = recent.Count(n => n.Sentiment == "bullish"); var bear = recent.Count(n => n.Sentiment == "bearish"); var action = bull > bear + 1 ? "buy" : bear > bull + 1 ? "sell" : "hold"; var confidence = Math.Min(92, 52 + Math.Abs(bull - bear) * 8); var levels = LocalSignalCalculator.Calculate(price.Price, marketHistory); CurrentSignal = new Signal { Levels = levels, Action = action, Confidence = confidence, Risk = confidence > 75 ? "medium" : "high", Rationale = $"Анализирани са {recent.Count} новини: {bull} бичи и {bear} мечи. Текущата цена е {price.Price:0.00}.", KeyFactors = [$"Настроение: {bull} бичи / {bear} мечи", $"Източник на цената: {price.Source}", $"Локални нива: {marketHistory.Count} OHLC свещи", "Локален детерминиран анализ"], Invalidation = "Неочаквани макроикономически данни или обръщане на индекса на щатския долар." }; Briefing = BuildBriefing(price, CurrentSignal, recent.Count, bull, bear); await Task.CompletedTask; }
    public async Task AnalyzeManualAsync(string text) { if (text.Length < 8) throw new InvalidOperationException("Поставете линк или текст на новина."); var lower = text.ToLowerInvariant(); var bull = new[] { "rise", "gain", "strong", "buy", "ръст", "силен" }.Count(x => lower.Contains(x)); var bear = new[] { "fall", "drop", "weak", "sell", "спад", "слаб" }.Count(x => lower.Contains(x)); News.Insert(0, new NewsItem { Source = "Ръчно", Title = text.Split('\n', '.', StringSplitOptions.RemoveEmptyEntries)[0].Trim()[..Math.Min(140, text.Length)], Url = "manual://" + Guid.NewGuid(), PublishedAt = DateTime.Now, Sentiment = bull > bear ? "bullish" : bear > bull ? "bearish" : "neutral", Impact = Math.Min(5, Math.Max(1, Math.Abs(bull - bear) + 1)), Summary = "Локално анализиран ръчен текст." }); await Task.CompletedTask; }
    public static string ActionLabel(string a) => a == "buy" ? "КУПУВАЙ" : a == "sell" ? "ПРОДАВАЙ" : "ИЗЧАКАЙ";
}
