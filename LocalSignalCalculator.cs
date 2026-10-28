namespace GoldDesk.Desktop;

/// <summary>
/// Локално, детерминирано изчисление на технически нива.
/// Използва последните OHLC свещи; не зависи от backend или AI услуга.
/// </summary>
public static class LocalSignalCalculator
{
    public static SignalLevels Calculate(decimal currentPrice, IReadOnlyList<MarketCandle>? candles)
    {
        if (currentPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentPrice));

        var valid = candles?.Where(c => c.High >= c.Low && c.Low > 0 && c.Close > 0).TakeLast(120).ToArray() ?? [];
        if (valid.Length < 3)
            return Fallback(currentPrice);

        var high = valid.Max(c => c.High);
        var low = valid.Min(c => c.Low);
        var range = Math.Max(high - low, currentPrice * 0.002m);
        var atr = AverageTrueRange(valid);
        var pivot = (valid[^1].High + valid[^1].Low + valid[^1].Close) / 3m;

        // Pivot levels: R/S са локалните дневни pivot зони, а не произволни UI стойности.
        var resistance = new[]
        {
            pivot + range * 0.236m, pivot + range * 0.382m, pivot + range * 0.618m,
            pivot + range * 0.786m, pivot + range
        };
        var support = new[]
        {
            pivot - range * 0.236m, pivot - range * 0.382m, pivot - range * 0.618m,
            pivot - range * 0.786m, pivot - range
        };

        // Buy/Sell targets са Fibonacci разширения от текущия close; ATR пази нивата
        // стабилни при малък диапазон и предотвратява нива прекалено близо до цената.
        var step = Math.Max(atr, currentPrice * 0.002m);
        var buyTargets = Enumerable.Range(1, 5)
            .Select(i => currentPrice + step * (0.618m + i * 0.618m)).ToArray();
        var sellTargets = Enumerable.Range(1, 5)
            .Select(i => currentPrice - step * (0.618m + i * 0.618m)).ToArray();
        var triggerGap = Math.Max(step * 0.32m, currentPrice * 0.0008m);

        return new SignalLevels
        {
            BuyAbove = Round(currentPrice + triggerGap),
            SellBelow = Round(Math.Max(0.01m, currentPrice - triggerGap)),
            Resistance = NormalizeAbove(resistance, currentPrice),
            Support = NormalizeBelow(support, currentPrice),
            BuyTargets = buyTargets.Select(Round).ToArray(),
            SellTargets = sellTargets.Select(v => Round(Math.Max(0.01m, v))).ToArray()
        };
    }

    private static SignalLevels Fallback(decimal price)
    {
        var step = Math.Max(1.0m, Math.Round(price * 0.0055m, 2));
        var gap = Math.Max(1.0m, Math.Round(step * 0.32m, 2));
        return new SignalLevels
        {
            BuyAbove = Round(price + gap), SellBelow = Round(Math.Max(0.01m, price - gap)),
            Resistance = Enumerable.Range(1, 5).Select(i => Round(price + step * i)).ToArray(),
            Support = Enumerable.Range(1, 5).Select(i => Round(Math.Max(0.01m, price - step * i))).ToArray(),
            BuyTargets = Enumerable.Range(1, 5).Select(i => Round(price + step * (i + 0.55m))).ToArray(),
            SellTargets = Enumerable.Range(1, 5).Select(i => Round(Math.Max(0.01m, price - step * (i + 0.55m)))).ToArray()
        };
    }

    private static decimal AverageTrueRange(IReadOnlyList<MarketCandle> candles)
    {
        var values = new List<decimal>();
        for (var i = 1; i < candles.Count; i++)
        {
            var c = candles[i];
            var previousClose = candles[i - 1].Close;
            values.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - previousClose), Math.Abs(c.Low - previousClose))));
        }
        return values.Count == 0 ? 0 : values.TakeLast(14).Average();
    }

    private static decimal[] NormalizeAbove(IEnumerable<decimal> values, decimal price) =>
        values.Select(Round).Where(v => v > price).Distinct().Take(5).ToArray() is var result && result.Length == 5
            ? result : Enumerable.Range(1, 5).Select(i => Round(price + Math.Max(price * 0.002m, price * 0.0055m) * i)).ToArray();

    private static decimal[] NormalizeBelow(IEnumerable<decimal> values, decimal price) =>
        values.Select(v => Round(Math.Max(0.01m, v))).Where(v => v < price).Distinct().OrderByDescending(v => v).Take(5).ToArray() is var result && result.Length == 5
            ? result : Enumerable.Range(1, 5).Select(i => Round(Math.Max(0.01m, price - Math.Max(price * 0.002m, price * 0.0055m) * i))).ToArray();

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
