using TradingAutomationHub.Enums;
using TradingAutomationHub.Models;

namespace TradingAutomationHub.MarketData;

public interface IMarketDataProvider
{
    string Name { get; }
    string GetProviderName(string symbol);

    Task<decimal?> GetCurrentPriceAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> SymbolExistsAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<MarketTick> GetTicksAsync(
        string symbol,
        TimeSpan interval,
        CancellationToken cancellationToken = default);
}

public sealed class MarketDataException : Exception
{
    public MarketDataException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
