using System.Runtime.CompilerServices;
using TradingAutomationHub.Enums;
using TradingAutomationHub.Models;

namespace TradingAutomationHub.MarketData;

public sealed class RoutedMarketDataProvider : IMarketDataProvider
{
    private readonly BinanceMarketDataProvider _binance;
    private readonly TwelveDataMarketDataProvider _forex;
    private readonly CoinGeckoMarketDataProvider _coinGecko;
    private readonly string _preferredCryptoProvider;

    public RoutedMarketDataProvider(BinanceMarketDataProvider binance, TwelveDataMarketDataProvider forex, CoinGeckoMarketDataProvider coinGecko)
    {
        _binance = binance;
        _forex = forex;
        _coinGecko = coinGecko;
        // Allow an environment override to prefer CoinGecko when deployed to locations
        // where Binance may be restricted (e.g., Render). Set PREFERRED_CRYPTO_PROVIDER=CoinGecko
        // to force CoinGecko as the primary crypto provider.
        _preferredCryptoProvider = Environment.GetEnvironmentVariable("PREFERRED_CRYPTO_PROVIDER") ?? string.Empty;
    }

    public string Name => "Automatic market routing";
    public string GetProviderName(string symbol)
    {
        return Select(symbol) == ProviderKind.Forex ? _forex.Name : _binance.Name;
    }

    public async Task<decimal?> GetCurrentPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (Select(symbol) == ProviderKind.Forex)
            return await _forex.GetCurrentPriceAsync(symbol, cancellationToken);

        // Crypto: optionally prefer CoinGecko when environment requests it.
        if (string.Equals(_preferredCryptoProvider, "CoinGecko", StringComparison.OrdinalIgnoreCase))
        {
            return await _coinGecko.GetCurrentPriceAsync(symbol, cancellationToken);
        }

        // Default: try Binance first, then CoinGecko fallback
        try
        {
            var val = await _binance.GetCurrentPriceAsync(symbol, cancellationToken);
            if (val is not null) return val;
        }
        catch
        {
            // swallow and try fallback
        }

        return await _coinGecko.GetCurrentPriceAsync(symbol, cancellationToken);
    }

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
            : GetCryptoTicks(symbol, interval, cancellationToken);

        var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Provider error: stop iterating and return to caller without throwing
                    break;
                }

                if (!hasNext) break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private static ProviderKind Select(string symbol) =>
        TwelveDataMarketDataProvider.IsForexOrMetalSymbol(symbol) ? ProviderKind.Forex : ProviderKind.Binance;

    private IAsyncEnumerable<MarketTick> GetCryptoTicks(string symbol, TimeSpan interval, CancellationToken cancellationToken)
    {
        // Allow preferring CoinGecko via env when Binance is restricted in the deployment region
        if (string.Equals(_preferredCryptoProvider, "CoinGecko", StringComparison.OrdinalIgnoreCase))
            return _coinGecko.GetTicksAsync(symbol, interval, cancellationToken);

        // Prefer Binance stream; if it fails, use CoinGecko stream
        var binanceStream = _binance.GetTicksAsync(symbol, interval, cancellationToken);
        return WrapFallbackStreams(binanceStream, _coinGecko.GetTicksAsync(symbol, interval, cancellationToken));
    }

    private static async IAsyncEnumerable<MarketTick> WrapFallbackStreams(IAsyncEnumerable<MarketTick> primary, IAsyncEnumerable<MarketTick> fallback, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = primary.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch
                {
                    break; // primary failed — switch to fallback
                }

                if (!hasNext) break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // primary ended or failed — enumerate fallback
        var ft = fallback.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await ft.MoveNextAsync();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (!hasNext) break;
                yield return ft.Current;
            }
        }
        finally
        {
            await ft.DisposeAsync();
        }
    }

    private enum ProviderKind { Binance, Forex }
}
