using System.Runtime.CompilerServices;
using TradingAutomationHub.Enums;
using TradingAutomationHub.Models;

namespace TradingAutomationHub.MarketData;

public sealed class RoutedMarketDataProvider : IMarketDataProvider
{
    private readonly BinanceMarketDataProvider _binance;
    private readonly TwelveDataMarketDataProvider _forex;

    public RoutedMarketDataProvider(BinanceMarketDataProvider binance, TwelveDataMarketDataProvider forex)
    {
        _binance = binance;
        _forex = forex;
    }

    public string Name => "Automatic market routing";
    public string GetProviderName(string symbol) => Select(symbol) == ProviderKind.Forex ? _forex.Name : _binance.Name;

    public Task<decimal?> GetCurrentPriceAsync(string symbol, CancellationToken cancellationToken = default) =>
        Select(symbol) == ProviderKind.Forex
            ? _forex.GetCurrentPriceAsync(symbol, cancellationToken)
            : _binance.GetCurrentPriceAsync(symbol, cancellationToken);

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, int limit, CancellationToken cancellationToken = default) =>
        Select(symbol) == ProviderKind.Forex
            ? _forex.GetCandlesAsync(symbol, timeframe, limit, cancellationToken)
            : _binance.GetCandlesAsync(symbol, timeframe, limit, cancellationToken);

    public async Task<bool> SymbolExistsAsync(string symbol, CancellationToken cancellationToken = default) =>
        await GetCurrentPriceAsync(symbol, cancellationToken) is not null;

    public async IAsyncEnumerable<MarketTick> GetTicksAsync(string symbol, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = Select(symbol) == ProviderKind.Forex
            ? _forex.GetTicksAsync(symbol, interval, cancellationToken)
            : _binance.GetTicksAsync(symbol, interval, cancellationToken);
        await foreach (var tick in stream.WithCancellation(cancellationToken)) yield return tick;
    }

    private static ProviderKind Select(string symbol) =>
        TwelveDataMarketDataProvider.IsForexOrMetalSymbol(symbol) ? ProviderKind.Forex : ProviderKind.Binance;

    private enum ProviderKind { Binance, Forex }
}
