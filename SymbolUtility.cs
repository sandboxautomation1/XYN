namespace GoldDesk.Desktop;

/// <summary>
/// Shared symbol rules used by the dashboard tabs and market-data providers.
/// </summary>
public static class SymbolUtility
{
    public static string RequireSymbol(string? value, string example = "XAUUSD")
    {
        var symbol = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
            throw new InvalidOperationException($"Въведете символ, например {example}.");

        return symbol;
    }

    /// <summary>
    /// Twelve Data accepts forex/metal pairs in the slash form (XAU/USD),
    /// while the UI may use the compact MT5-like form (XAUUSD).
    /// </summary>
    public static string NormalizeTwelveDataSymbol(string value)
    {
        var symbol = RequireSymbol(value);
        if (symbol.Contains('/')) return symbol.ToUpperInvariant();

        var compact = symbol.Replace("-", string.Empty).Replace("_", string.Empty);
        if (compact.Length == 6)
            return $"{compact[..3]}/{compact[3..]}".ToUpperInvariant();

        if (compact.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase))
            return "XAU/USD";

        return symbol.ToUpperInvariant();
    }
}
