using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using TradingAutomationHub.Enums;
using TradingAutomationHub.Models;

namespace TradingAutomationHub.MarketData;

public sealed class BinanceMarketDataProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BinanceMarketDataProvider> _logger;

    public BinanceMarketDataProvider(HttpClient httpClient, ILogger<BinanceMarketDataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string Name => "Binance Spot";
    public string GetProviderName(string symbol) => Name;

    public async Task<decimal?> GetCurrentPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        symbol = NormalizeSymbol(symbol);
        try
        {
            using var response = await _httpClient.GetAsync(
                $"/api/v3/ticker/price?symbol={Uri.EscapeDataString(symbol)}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Read response body for diagnostics (best-effort)
                string body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                _logger.LogWarning("Binance returned non-success {Status} for {Symbol}: {Body}", (int)response.StatusCode, symbol, body);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                    return null;

                response.EnsureSuccessStatusCode();
            }
            var ticker = await response.Content.ReadFromJsonAsync<BinanceTickerResponse>(cancellationToken);

            if (ticker is null || !decimal.TryParse(
                    ticker.Price,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var price))
            {
                throw new MarketDataException($"{Name} returned malformed price data for {symbol}.");
            }

            return price;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MarketDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new MarketDataException($"{Name} is currently unavailable. Please try again.", exception);
        }
    }

    public async Task<bool> SymbolExistsAsync(string symbol, CancellationToken cancellationToken = default) =>
        await GetCurrentPriceAsync(symbol, cancellationToken) is not null;

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Binance candle limits must be between 1 and 1000.");

        symbol = NormalizeSymbol(symbol);
        var interval = timeframe switch
        {
            Timeframe.OneMinute => "1m",
            Timeframe.FiveMinutes => "5m",
            Timeframe.FifteenMinutes => "15m",
            Timeframe.OneHour => "1h",
            Timeframe.FourHours => "4h",
            Timeframe.OneDay => "1d",
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };

        try
        {
            using var response = await _httpClient.GetAsync(
                $"/api/v3/klines?symbol={Uri.EscapeDataString(symbol)}&interval={interval}&limit={limit}",
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.BadRequest)
                return Array.Empty<Candle>();

            response.EnsureSuccessStatusCode();
            var rows = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement[]>>(cancellationToken)
                ?? new List<System.Text.Json.JsonElement[]>();

            return rows.Select(ParseCandle).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or FormatException)
        {
            throw new MarketDataException($"Unable to load candle data for {symbol} from {Name}.", exception);
        }
    }

    public async IAsyncEnumerable<MarketTick> GetTicksAsync(
        string symbol,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        symbol = NormalizeSymbol(symbol);
        while (!cancellationToken.IsCancellationRequested)
        {
            var price = await GetCurrentPriceAsync(symbol, cancellationToken);
            if (price.HasValue)
                yield return new MarketTick(symbol, DateTime.UtcNow, price.Value);

            await Task.Delay(interval, cancellationToken);
        }
    }

    private static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("A symbol is required.", nameof(symbol));
        return symbol.Trim().ToUpperInvariant();
    }

    private static Candle ParseCandle(System.Text.Json.JsonElement[] row)
    {
        if (row.Length < 6)
            throw new FormatException("The candle response did not contain all OHLCV fields.");

        return new Candle(
            DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime,
            ParseDecimal(row[1]),
            ParseDecimal(row[2]),
            ParseDecimal(row[3]),
            ParseDecimal(row[4]),
            ParseDecimal(row[5]));
    }

    private static decimal ParseDecimal(System.Text.Json.JsonElement value) =>
        decimal.Parse(value.GetString() ?? string.Empty, NumberStyles.Float, CultureInfo.InvariantCulture);

    private sealed class BinanceTickerResponse
    {
        public string Symbol { get; set; } = string.Empty;
        public string Price { get; set; } = string.Empty;
    }
}
