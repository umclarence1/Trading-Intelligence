using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using TradingAutomationHub.Enums;
using TradingAutomationHub.Models;

namespace TradingAutomationHub.MarketData;

public sealed class CoinGeckoMarketDataProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CoinGeckoMarketDataProvider> _logger;

    private static readonly IReadOnlyDictionary<string, string> SymbolToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = "bitcoin",
        ["ETH"] = "ethereum",
        ["BNB"] = "binancecoin",
        ["SOL"] = "solana",
        ["ADA"] = "cardano",
    };

    public CoinGeckoMarketDataProvider(HttpClient httpClient, ILogger<CoinGeckoMarketDataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string Name => "CoinGecko";
    public string GetProviderName(string symbol) => Name;

    public async Task<decimal?> GetCurrentPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("A symbol is required.", nameof(symbol));

        // normalize base asset from symbols like BTCUSDT
        var baseAsset = symbol.Trim().ToUpperInvariant();
        if (baseAsset.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) || baseAsset.EndsWith("USD", StringComparison.OrdinalIgnoreCase))
            baseAsset = baseAsset.Substring(0, baseAsset.Length - 4);

        var id = SymbolToId.TryGetValue(baseAsset, out var mapped) ? mapped : baseAsset.ToLowerInvariant();

        try
        {
            using var res = await _httpClient.GetAsync($"/coins/{Uri.EscapeDataString(id)}/tickers", cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                res.EnsureSuccessStatusCode();
            }

            var doc = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>(cancellationToken);
            if (doc is null)
                throw new MarketDataException($"{Name} returned malformed data for {symbol}.");

            var root = doc.RootElement;
            if (root.TryGetProperty("tickers", out var tickers) && tickers.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var tk in tickers.EnumerateArray())
                {
                    if (tk.TryGetProperty("converted_last", out var conv) && conv.TryGetProperty("usd", out var usd))
                    {
                        if (usd.TryGetDecimal(out var price))
                            return price;
                    }
                    if (tk.TryGetProperty("last", out var last) && last.TryGetDecimal(out var lastPrice))
                    {
                        return lastPrice;
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new MarketDataException($"{Name} is currently unavailable. Please try again.", ex);
        }
    }

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, int limit, CancellationToken cancellationToken = default)
    {
        throw new MarketDataException("CoinGecko candle data is not supported by this provider.");
    }

    public async Task<bool> SymbolExistsAsync(string symbol, CancellationToken cancellationToken = default) =>
        await GetCurrentPriceAsync(symbol, cancellationToken) is not null;

    public async IAsyncEnumerable<MarketTick> GetTicksAsync(string symbol, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            decimal? price = null;
            try
            {
                price = await GetCurrentPriceAsync(symbol, cancellationToken);
            }
            catch
            {
                // swallow and continue
            }

            if (price.HasValue)
                yield return new MarketTick(symbol, DateTime.UtcNow, price.Value);

            try { await Task.Delay(interval, cancellationToken); } catch (OperationCanceledException) { break; }
        }
    }
}
