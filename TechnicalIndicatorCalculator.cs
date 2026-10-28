namespace GoldDesk.Desktop;

public sealed record TechnicalIndicators(
    decimal? Sma20,
    decimal? Rsi14,
    decimal? Macd,
    decimal? MacdSignal,
    decimal? BollingerMiddle,
    decimal? BollingerUpper,
    decimal? BollingerLower);

public static class TechnicalIndicatorCalculator
{
    public static TechnicalIndicators Calculate(IReadOnlyList<MarketCandle>? candles)
    {
        var closes = candles?.Where(c => c.Close > 0).Select(c => c.Close).ToArray() ?? [];
        if (closes.Length == 0)
            return new(null, null, null, null, null, null, null);

        decimal? sma20 = closes.Length >= 20 ? Average(closes.TakeLast(20)) : null;
        decimal? rsi14 = closes.Length >= 15 ? CalculateRsi(closes, 14) : null;
        var macdSeries = CalculateMacdSeries(closes, 12, 26, 9);
        decimal? macd = macdSeries.Count > 0 ? macdSeries[^1].Macd : null;
        decimal? macdSignal = macdSeries.Count >= 9 ? macdSeries[^1].Signal : null;

        decimal? bollingerMiddle = sma20;
        decimal? bollingerUpper = null;
        decimal? bollingerLower = null;
        if (closes.Length >= 20)
        {
            var window = closes.TakeLast(20).ToArray();
            var mean = Average(window);
            var variance = window.Select(value => (value - mean) * (value - mean)).Average();
            var deviation = (decimal)Math.Sqrt((double)variance);
            bollingerUpper = mean + 2m * deviation;
            bollingerLower = mean - 2m * deviation;
        }

        return new(sma20, rsi14, macd, macdSignal, bollingerMiddle, bollingerUpper, bollingerLower);
    }

    private static decimal CalculateRsi(IReadOnlyList<decimal> closes, int period)
    {
        var changes = closes.Zip(closes.Skip(1), (previous, current) => current - previous).TakeLast(period);
        var gains = changes.Where(change => change > 0).DefaultIfEmpty().Average();
        var losses = changes.Where(change => change < 0).Select(Math.Abs).DefaultIfEmpty().Average();
        if (losses == 0) return 100m;
        return 100m - 100m / (1m + gains / losses);
    }

    private static List<(decimal Macd, decimal Signal)> CalculateMacdSeries(IReadOnlyList<decimal> closes, int fast, int slow, int signal)
    {
        if (closes.Count < slow) return [];
        var fastEma = closes[0];
        var slowEma = closes[0];
        var signalEma = 0m;
        var fastMultiplier = 2m / (fast + 1);
        var slowMultiplier = 2m / (slow + 1);
        var signalMultiplier = 2m / (signal + 1);
        var values = new List<(decimal Macd, decimal Signal)>();

        for (var i = 0; i < closes.Count; i++)
        {
            fastEma = i == 0 ? closes[i] : (closes[i] - fastEma) * fastMultiplier + fastEma;
            slowEma = i == 0 ? closes[i] : (closes[i] - slowEma) * slowMultiplier + slowEma;
            var macd = fastEma - slowEma;
            signalEma = values.Count == 0 ? macd : (macd - signalEma) * signalMultiplier + signalEma;
            if (i >= slow - 1) values.Add((macd, signalEma));
        }

        return values;
    }

    private static decimal Average(IEnumerable<decimal> values) => values.DefaultIfEmpty().Average();
}
