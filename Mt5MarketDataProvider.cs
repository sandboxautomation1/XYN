namespace GoldDesk.Desktop;

public sealed record Mt5MarketData(
    GoldPrice Price,
    MarketSnapshot Snapshot,
    IReadOnlyList<MarketCandle> History);

public sealed class Mt5MarketDataProvider : IAsyncDisposable
{
    private readonly Mt5BridgeClient bridge = new();
    private readonly TaskCompletionSource<Mt5MarketData> firstData = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<MarketCandle> history = [];
    private Mt5MarketData? current;

    public event Action<Mt5MarketData>? DataUpdated;
    public event Action<Mt5TradeEvent>? TradeReceived;
    public Mt5MarketData? Current => current;
    public bool IsConnected => bridge.IsConnected;

    public Mt5MarketDataProvider()
    {
        bridge.OhlcReceived += OnOhlcReceived;
        bridge.PriceReceived += OnPriceReceived;
        bridge.TradeReceived += update => TradeReceived?.Invoke(update);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        bridge.StartAsync("XAUUSD.p", "M30", 100, cancellationToken);

    public async Task<Mt5MarketData> WaitForInitialDataAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await firstData.Task.WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("MT5 bridge не изпрати начални пазарни данни.");
        }
    }

    private void OnOhlcReceived(IReadOnlyList<MarketCandle> candles, string symbol)
    {
        history = candles.OrderBy(x => x.Timestamp).ToList();
        var latest = history.LastOrDefault();
        if (latest is null) return;

        var price = current?.Price.Price ?? latest.Close;
        var previous = history.Count > 1 ? history[^2].Close : (decimal?)null;
        var change = previous is null or 0 ? null : (price - previous) / previous * 100;
        Publish(symbol, price, change, latest);
    }

    private void OnPriceReceived(Mt5PriceUpdate update)
    {
        var mid = update.Last > 0 ? update.Last : (update.Bid + update.Ask) / 2;
        var latest = history.LastOrDefault();
        var previous = history.Count > 1 ? history[^2].Close : latest?.Open;
        var change = previous is null or 0 ? null : (mid - previous) / previous * 100;
        var snapshot = latest is null
            ? new MarketSnapshot(mid, mid, mid, mid, change, update.Timestamp)
            : new MarketSnapshot(latest.Open, Math.Max(latest.High, mid), Math.Min(latest.Low, mid), mid, change, update.Timestamp);
        Publish(update.Symbol, mid, change, snapshot);
    }

    private void Publish(string symbol, decimal price, decimal? change, MarketCandle latest)
    {
        var snapshot = new MarketSnapshot(latest.Open, Math.Max(latest.High, price), Math.Min(latest.Low, price), price, change, latest.Timestamp);
        Publish(symbol, price, change, snapshot);
    }

    private void Publish(string symbol, decimal price, decimal? change, MarketSnapshot snapshot)
    {
        var data = new Mt5MarketData(
            new GoldPrice(symbol, price, change, "MT5", snapshot.UpdatedAt),
            snapshot,
            history.ToList());
        current = data;
        firstData.TrySetResult(data);
        DataUpdated?.Invoke(data);
    }

    public async ValueTask DisposeAsync()
    {
        bridge.OhlcReceived -= OnOhlcReceived;
        bridge.PriceReceived -= OnPriceReceived;
        await bridge.DisposeAsync();
    }
}
