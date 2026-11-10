using Newtonsoft.Json;
using RestSharp;
using System.Net.Http;

namespace GoldDesk.Desktop;

public sealed record Mt5PriceUpdate(string Symbol, decimal Bid, decimal Ask, decimal Last, long Volume, DateTime Timestamp);
public sealed record Mt5TradeEvent(string Symbol, ulong Ticket, string Side, string Reason, decimal Profit);

public sealed class Mt5BridgeClient : IAsyncDisposable
{
    private readonly RestClient client;
    private readonly TimeSpan pollingInterval = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? cancellation;
    private Task? pollingTask;
    private string symbol = "XAUUSD.p";
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
        client = new RestClient(new RestClientOptions(baseUrl.TrimEnd('/'))
        {
            Timeout = TimeSpan.FromSeconds(10)
        });
    }

    public async Task StartAsync(string symbol = "XAUUSD.p", string timeframe = "M30", int depth = 100, CancellationToken cancellationToken = default)
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

    public async Task RestartAsync(string symbol, string timeframe, int depth = 100, CancellationToken cancellationToken = default)
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch { isConnected = false; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { isConnected = false; }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var ratesRequest = new RestRequest("/rates/{symbol}", Method.Get)
            .AddUrlSegment("symbol", symbol)
            .AddQueryParameter("timeframe", timeframe)
            .AddQueryParameter("count", count);
        var tickRequest = new RestRequest("/tick/{symbol}", Method.Get)
            .AddUrlSegment("symbol", symbol);

        var ratesTask = client.ExecuteAsync(ratesRequest, cancellationToken);
        var tickTask = client.ExecuteAsync(tickRequest, cancellationToken);
        await Task.WhenAll(ratesTask, tickTask);

        var ratesResponse = await ratesTask;
        var tickResponse = await tickTask;
        EnsureSuccess(ratesResponse);
        EnsureSuccess(tickResponse);

        var rates = JsonConvert.DeserializeObject<List<BridgeRate>>(ratesResponse.Content ?? "[]") ?? [];
        var tick = string.IsNullOrWhiteSpace(tickResponse.Content)
            ? null
            : JsonConvert.DeserializeObject<BridgeTick>(tickResponse.Content);

        var candles = rates.Select(rate => new MarketCandle(UnixSecondsToLocalTime(rate.Time), rate.Open, rate.High, rate.Low, rate.Close))
            .OrderBy(candle => candle.Timestamp).ToList();
        if (candles.Count > 0) OhlcReceived?.Invoke(candles, symbol);
        if (tick is not null)
        {
            var timestamp = tick.TimeMsc > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(tick.TimeMsc).LocalDateTime : UnixSecondsToLocalTime(tick.Time);
            PriceReceived?.Invoke(new Mt5PriceUpdate(symbol, tick.Bid, tick.Ask, tick.Last, tick.Volume, timestamp));
        }
    }

    private static void EnsureSuccess(RestResponse response)
    {
        if (!response.IsSuccessful)
            throw new HttpRequestException($"MT5 bridge returned HTTP {(int?)response.StatusCode ?? 0} for {response.ResponseUri}: {response.ErrorMessage ?? response.Content}");
    }

    private static DateTime UnixSecondsToLocalTime(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;

    private async Task StopConnectionAsync()
    {
        var source = cancellation;
        cancellation = null;
        isConnected = false;
        source?.Cancel();
        if (pollingTask is not null)
        {
            try { await pollingTask; } catch (OperationCanceledException) { }
            pollingTask = null;
        }
        source?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopConnectionAsync();
        client.Dispose();
    }

    private sealed class BridgeRate
    {
        [JsonProperty("time")] public long Time { get; set; }
        [JsonProperty("open")] public decimal Open { get; set; }
        [JsonProperty("high")] public decimal High { get; set; }
        [JsonProperty("low")] public decimal Low { get; set; }
        [JsonProperty("close")] public decimal Close { get; set; }
    }

    private sealed class BridgeTick
    {
        [JsonProperty("time")] public long Time { get; set; }
        [JsonProperty("time_msc")] public long TimeMsc { get; set; }
        [JsonProperty("bid")] public decimal Bid { get; set; }
        [JsonProperty("ask")] public decimal Ask { get; set; }
        [JsonProperty("last")] public decimal Last { get; set; }
        [JsonProperty("volume")] public long Volume { get; set; }
    }
}
