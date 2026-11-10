namespace GoldDesk.Desktop;

public sealed record MarketSignalPlan(
    string Direction,
    decimal? Entry,
    decimal? StopLoss,
    decimal? TakeProfit,
    string Rationale,
    DateTime GeneratedAt);

/// <summary>Generates a deterministic technical setup from Twelve Data OHLC history.</summary>
public static class MarketSignalGenerator
{
    public static MarketSignalPlan Generate(decimal price, IReadOnlyList<MarketCandle> candles)
    {
        var valid = candles.Where(c => c.High >= c.Low && c.Low > 0 && c.Close > 0).TakeLast(120).ToArray();
        if (valid.Length < 26)
            return new("ИЗЧАКВАНЕ", null, null, null, $"Нужни са поне 26 валидни OHLC свещи; налични: {valid.Length}.", DateTime.Now);

        var indicators = TechnicalIndicatorCalculator.Calculate(valid);
        var atrValues = new List<decimal>();
        for (var i = 1; i < valid.Length; i++)
        {
            var c = valid[i];
            var prev = valid[i - 1].Close;
            atrValues.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prev), Math.Abs(c.Low - prev))));
        }
        var atr = atrValues.TakeLast(14).DefaultIfEmpty().Average();
        if (atr <= 0 || indicators.Sma20 is null || indicators.Macd is null || indicators.MacdSignal is null || indicators.Rsi14 is null)
            return new("ИЗЧАКВАНЕ", null, null, null, "Индикаторите още нямат достатъчно валидни данни.", DateTime.Now);

        var bullish = new[] { price > indicators.Sma20, indicators.Macd > indicators.MacdSignal, indicators.Rsi14 >= 50m && indicators.Rsi14 <= 70m }.Count(x => x);
        var bearish = new[] { price < indicators.Sma20, indicators.Macd < indicators.MacdSignal, indicators.Rsi14 < 50m && indicators.Rsi14 >= 30m }.Count(x => x);
        var direction = bullish >= 2 && bullish > bearish ? "BUY" : bearish >= 2 && bearish > bullish ? "SELL" : "ИЗЧАКВАНЕ";
        if (direction == "ИЗЧАКВАНЕ")
            return new(direction, null, null, null,
                $"Няма достатъчно съгласуване на индикаторите (SMA20 {indicators.Sma20:0.00}, RSI14 {indicators.Rsi14:0.0}, MACD {indicators.Macd:0.000} / {indicators.MacdSignal:0.000}).", DateTime.Now);

        var risk = Math.Max(atr * 1.5m, price * 0.001m);
        var entry = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        var stop = direction == "BUY" ? entry - risk : entry + risk;
        var target = direction == "BUY" ? entry + risk * 2m : entry - risk * 2m;
        return new(direction, entry,
            Math.Round(stop, 2, MidpointRounding.AwayFromZero),
            Math.Round(target, 2, MidpointRounding.AwayFromZero),
            $"Съгласуване: {Math.Max(bullish, bearish)}/3 условия. SMA20 {indicators.Sma20:0.00}; RSI14 {indicators.Rsi14:0.0}; MACD {indicators.Macd:0.000} / {indicators.MacdSignal:0.000}; ATR14 {atr:0.00}. Stop-loss = 1.5 ATR, take-profit = 2R.", DateTime.Now);
    }
}
