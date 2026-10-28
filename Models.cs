namespace GoldDesk.Desktop;

public record GoldPrice(string Symbol, decimal Price, decimal? ChangePct, string Source, DateTime UpdatedAt);
public record MarketCandle(DateTime Timestamp, decimal Open, decimal High, decimal Low, decimal Close);
public record MarketSnapshot(decimal Open, decimal High, decimal Low, decimal Close, decimal? ChangePct, DateTime UpdatedAt);
public record Mt5Quote(string Symbol, decimal Bid, decimal Ask, decimal Last, long Volume, DateTime UpdatedAt);
public record ForecastPoint(string Horizon, decimal Low, decimal High, decimal Average, decimal ChangePct, DateTime AsOf);

public class SignalLevels
{
    public decimal BuyAbove { get; init; }
    public decimal SellBelow { get; init; }
    public decimal[] Resistance { get; init; } = [];
    public decimal[] Support { get; init; } = [];
    public decimal[] BuyTargets { get; init; } = [];
    public decimal[] SellTargets { get; init; } = [];
}

public class NewsItem { public string Source { get; set; } = ""; public string Title { get; set; } = ""; public string Url { get; set; } = ""; public DateTime PublishedAt { get; set; } public string Sentiment { get; set; } = "neutral"; public int Impact { get; set; } public string Summary { get; set; } = ""; public string Reasoning { get; set; } = ""; }
public class Signal { public string Action { get; set; } = "hold"; public int Confidence { get; set; } public string Risk { get; set; } = "medium"; public string Horizon { get; set; } = "1–3 дни"; public string Rationale { get; set; } = ""; public List<string> KeyFactors { get; set; } = []; public string Invalidation { get; set; } = ""; public SignalLevels Levels { get; set; } = new(); public DateTime CreatedAt { get; set; } = DateTime.Now; }
public class Alert { public string Title { get; set; } = ""; public string Body { get; set; } = ""; public bool Read { get; set; } }
public class SourceSettings { public string Provider { get; set; } = "mt5"; public string Symbol { get; set; } = "XAUUSD"; public string PriceUrl { get; set; } = ""; public string PricePath { get; set; } = "price"; public string TwelveDataApiKey { get; set; } = ""; public string TwelveDataInterval { get; set; } = "30min"; public bool UseDefaultNews { get; set; } = true; public List<NewsFeed> Feeds { get; set; } = []; }
public class NewsFeed { public string Name { get; set; } = ""; public string Url { get; set; } = ""; }
