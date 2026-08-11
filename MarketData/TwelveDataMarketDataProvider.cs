using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TradingAutomationHub.Enums;
using TradingAutomationHub.Models;

namespace TradingAutomationHub.MarketData;

public sealed class TwelveDataMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly TwelveDataSettings _settings;

    public TwelveDataMarketDataProvider(HttpClient httpClient, IOptions<TwelveDataSettings> settings)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
    }

    public string Name => "Twelve Data Forex";

    public async Task<decimal?> GetCurrentPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var providerSymbol = FormatSymbol(symbol);
        try
        {
            using var request = CreateRequest($"/price?symbol={Uri.EscapeDataString(providerSymbol)}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadFromJsonAsync<TwelveDataPriceResponse>(cancellationToken);

            if (response.StatusCode == HttpStatusCode.BadRequest || payload?.Status == "error") return null;
            response.EnsureSuccessStatusCode();
            if (payload is null || !decimal.TryParse(payload.Price, NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
                throw new MarketDataException($"{Name} returned malformed price data for {symbol}.");
            return price;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new MarketDataException($"{Name} is currently unavailable. Please try again.", exception);
        }
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        var interval = timeframe switch
        {
            Timeframe.OneMinute => "1min",
            Timeframe.FiveMinutes => "5min",
            Timeframe.FifteenMinutes => "15min",
            Timeframe.OneHour => "1h",
            Timeframe.FourHours => "4h",
            Timeframe.OneDay => "1day",
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };
        var providerSymbol = FormatSymbol(symbol);
        try
        {
            using var request = CreateRequest($"/time_series?symbol={Uri.EscapeDataString(providerSymbol)}&interval={interval}&outputsize={limit}&timezone=UTC");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadFromJsonAsync<TwelveDataSeriesResponse>(cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest || payload?.Status == "error") return Array.Empty<Candle>();
            response.EnsureSuccessStatusCode();
            return (payload?.Values ?? [])
                .Select(value => new Candle(
                    DateTime.SpecifyKind(DateTime.Parse(value.DateTime, CultureInfo.InvariantCulture), DateTimeKind.Utc),
                    ParseDecimal(value.Open), ParseDecimal(value.High), ParseDecimal(value.Low),
                    ParseDecimal(value.Close), ParseOptionalDecimal(value.Volume)))
                .OrderBy(candle => candle.OpenTime)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or FormatException)
        {
            throw new MarketDataException($"Unable to load candle data for {symbol} from {Name}.", exception);
        }
    }

    public async IAsyncEnumerable<MarketTick> GetTicksAsync(
        string symbol,
        TimeSpan requestedInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var configuredInterval = TimeSpan.FromSeconds(Math.Max(15, _settings.PollingIntervalSeconds));
        var interval = requestedInterval > configuredInterval ? requestedInterval : configuredInterval;
        while (!cancellationToken.IsCancellationRequested)
        {
            var price = await GetCurrentPriceAsync(symbol, cancellationToken);
            if (price.HasValue) yield return new MarketTick(NormalizeSymbol(symbol), DateTime.UtcNow, price.Value);
            await Task.Delay(interval, cancellationToken);
        }
    }

    public static bool IsForexOrMetalSymbol(string symbol) =>
        NormalizeSymbol(symbol) is { Length: 6 } normalized && normalized.All(char.IsLetter);

    private HttpRequestMessage CreateRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"apikey {_settings.ApiKey}");
        return request;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new MarketDataException("Twelve Data is not configured. Set the TwelveData:ApiKey secret.");
    }

    private static string FormatSymbol(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);
        if (!IsForexOrMetalSymbol(normalized)) throw new ArgumentException("Forex symbols must use six letters, for example EURUSD or XAUUSD.", nameof(symbol));
        return $"{normalized[..3]}/{normalized[3..]}";
    }

    private static string NormalizeSymbol(string symbol) => symbol.Trim().Replace("/", string.Empty).ToUpperInvariant();
    private static decimal ParseDecimal(string value) => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static decimal ParseOptionalDecimal(string? value) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private sealed class TwelveDataPriceResponse
    {
        public string? Price { get; set; }
        public string? Status { get; set; }
    }

    private sealed class TwelveDataSeriesResponse
    {
        public string? Status { get; set; }
        public List<TwelveDataValue> Values { get; set; } = [];
    }

    private sealed class TwelveDataValue
    {
        [JsonPropertyName("datetime")] public string DateTime { get; set; } = string.Empty;
        public string Open { get; set; } = string.Empty;
        public string High { get; set; } = string.Empty;
        public string Low { get; set; } = string.Empty;
        public string Close { get; set; } = string.Empty;
        public string? Volume { get; set; }
    }
}
