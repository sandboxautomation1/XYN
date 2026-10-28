using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoldDesk.Desktop;

public sealed record Mt5PriceUpdate(string Symbol, decimal Bid, decimal Ask, decimal Last, long Volume, DateTime Timestamp);
public sealed record Mt5TradeEvent(string Symbol, ulong Ticket, string Side, string Reason, decimal Profit);

public sealed class Mt5BridgeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient http;
    private readonly TimeSpan pollingInterval = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? cancellation;
    private Task? pollingTask;
    private string symbol = "XAUUSD";
    private string timeframe = "M30";
    private int count = 100;
    private volatile bool isConnected;

    public event Action<IReadOnlyList<MarketCandle>, string>? OhlcReceived;
    public event Action<Mt5PriceUpdate>? PriceReceived;
    public event Action<Mt5TradeEvent>? TradeReceived;
    public bool IsConnected => isConnected;

    public Mt5BridgeClient()
    {
        var baseUrl = Environment.GetEnvironmentVariable("MT5_BRIDGE_BASE_URL") ?? "http://127.0.0.1:8000";
        http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task StartAsync(
        string symbol = "XAUUSD",
        string timeframe = "M30",
        int depth = 100,
        CancellationToken cancellationToken = default)
    {
        if (isConnected && pollingTask is not null) return;

        this.symbol = symbol;
        this.timeframe = timeframe;
        count = Math.Clamp(depth, 1, 1000);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await PollOnceAsync(cancellation.Token);
            isConnected = true;
            pollingTask = Task.Run(() => PollLoopAsync(cancellation.Token), cancellation.Token);
        }
        catch
        {
            isConnected = false;
            cancellation.Dispose();
            cancellation = null;
            throw;
        }
    }

    public async Task RestartAsync(
        string symbol,
        string timeframe,
        int depth = 100,
        CancellationToken cancellationToken = default)
    {
        await StopConnectionAsync();
        await StartAsync(symbol, timeframe, depth, cancellationToken);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(pollingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await PollOnceAsync(cancellationToken);
                    isConnected = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Keep the client alive during a temporary bridge/MT5 outage.
                    isConnected = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            isConnected = false;
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var ratesTask = http.GetFromJsonAsync<List<BridgeRate>>(
            $"rates/{Uri.EscapeDataString(symbol)}?timeframe={Uri.EscapeDataString(timeframe)}&count={count}",
            JsonOptions,
            cancellationToken);
        var tickTask = http.GetFromJsonAsync<BridgeTick>(
            $"tick/{Uri.EscapeDataString(symbol)}",
            JsonOptions,
            cancellationToken);

        await Task.WhenAll(ratesTask, tickTask);
        var rates = await ratesTask ?? [];
        var tick = await tickTask;

        var candles = rates
            .Select(rate => new MarketCandle(
                UnixSecondsToLocalTime(rate.Time),
                rate.Open,
                rate.High,
                rate.Low,
                rate.Close))
            .OrderBy(candle => candle.Timestamp)
            .ToList();

        if (candles.Count > 0)
            OhlcReceived?.Invoke(candles, symbol);

        if (tick is not null)
        {
            var timestamp = tick.TimeMsc > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(tick.TimeMsc).LocalDateTime
                : UnixSecondsToLocalTime(tick.Time);
            PriceReceived?.Invoke(new Mt5PriceUpdate(symbol, tick.Bid, tick.Ask, tick.Last, tick.Volume, timestamp));
        }
    }

    private static DateTime UnixSecondsToLocalTime(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;

    private async Task StopConnectionAsync()
    {
        var source = cancellation;
        cancellation = null;
        isConnected = false;
        source?.Cancel();

        if (pollingTask is not null)
        {
            try { await pollingTask; }
            catch (OperationCanceledException) { }
            pollingTask = null;
        }

        source?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopConnectionAsync();
        http.Dispose();
    }

    private sealed class BridgeRate
    {
        [JsonPropertyName("time")] public long Time { get; set; }
        [JsonPropertyName("open")] public decimal Open { get; set; }
        [JsonPropertyName("high")] public decimal High { get; set; }
        [JsonPropertyName("low")] public decimal Low { get; set; }
        [JsonPropertyName("close")] public decimal Close { get; set; }
    }

    private sealed class BridgeTick
    {
        [JsonPropertyName("time")] public long Time { get; set; }
        [JsonPropertyName("time_msc")] public long TimeMsc { get; set; }
        [JsonPropertyName("bid")] public decimal Bid { get; set; }
        [JsonPropertyName("ask")] public decimal Ask { get; set; }
        [JsonPropertyName("last")] public decimal Last { get; set; }
        [JsonPropertyName("volume")] public long Volume { get; set; }
    }
}
