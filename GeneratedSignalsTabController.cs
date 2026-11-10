namespace GoldDesk.Desktop;

public sealed record GeneratedSignalResult(
    GoldPrice Price,
    MarketSignalPlan Plan);

/// <summary>
/// Application logic for the "Сигнали" tab. The window only binds the result to controls.
/// </summary>
public sealed class GeneratedSignalsTabController
{
    private readonly GoldDeskService service;

    public GeneratedSignalsTabController(GoldDeskService service)
    {
        this.service = service;
    }

    public async Task<GeneratedSignalResult> GenerateAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var requestedSymbol = SymbolUtility.RequireSymbol(symbol);
        service.Sources.Symbol = requestedSymbol;
        service.SaveSources();

        cancellationToken.ThrowIfCancellationRequested();
        var price = await service.RefreshTwelveDataAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = MarketSignalGenerator.Generate(price.Price, service.MarketHistory);
        return new GeneratedSignalResult(price, plan);
    }
}
