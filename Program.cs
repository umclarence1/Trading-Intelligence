using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TradingAutomationHub;
using TradingAutomationHub.MarketData;
using TradingAutomationHub.Models;
using TradingAutomationHub.Services;
using TradingAutomationHub.Enums;
using TradingAutomationHub.Indicators;
using TradingAutomationHub.Trading;

var initialSymbols = new[] { "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "ADAUSDT" };

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<TwelveDataSettings>(builder.Configuration.GetSection(TwelveDataSettings.SectionName));
builder.Services.Configure<TradingSettings>(builder.Configuration.GetSection(TradingSettings.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<TradingSettings>>().Value);
builder.Services.AddSingleton<RiskManagementEngine>();
builder.Services.AddHttpClient<BinanceMarketDataProvider>(client =>
{
    client.BaseAddress = new Uri("https://api.binance.com");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<TwelveDataMarketDataProvider>(client =>
{
    client.BaseAddress = new Uri("https://api.twelvedata.com");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<CoinGeckoMarketDataProvider>(client =>
{
    client.BaseAddress = new Uri("https://api.coingecko.com/api/v3");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IMarketDataProvider, RoutedMarketDataProvider>();
builder.Services.AddSingleton<ITradingAdvisoryService, TradingAdvisoryService>();
builder.Services.AddSingleton<IIndicatorEngine, IndicatorEngine>();
// CORS: allow requests from Vercel frontend and localhost during development.
// Use permissive policy for now to avoid 3rd-party Vercel host variants blocking requests.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVercelAndLocal", policy =>
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("AllowVercelAndLocal");
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/api/advisories", (ITradingAdvisoryService service) =>
    Results.Ok(service.Advisories.Select(a => a.ToResponse(service.GetPriceHistory(a.Symbol)))));

app.MapGet("/api/history", (ITradingAdvisoryService service) =>
    Results.Ok(service.SignalHistory.Select(r => new
    {
        symbol = r.Symbol,
        price = r.Price,
        signal = r.Signal.ToString(),
        confidence = r.Confidence,
        reason = r.Reason,
        time = r.Time
    })));

app.MapGet("/api/symbols", (ITradingAdvisoryService service) =>
    Results.Ok(service.Advisories.Select(a => a.Symbol)));

app.MapGet("/api/prices", (ITradingAdvisoryService service, string symbol) =>
    string.IsNullOrWhiteSpace(symbol)
        ? Results.BadRequest(new { error = "A symbol is required." })
        : Results.Ok(service.GetPriceHistory(symbol)));

app.MapGet("/api/indicators", async (
    string symbol,
    string timeframe,
    IMarketDataProvider provider,
    IIndicatorEngine indicators,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(symbol))
        return Results.BadRequest(new { error = "A symbol is required." });
    if (!TimeframeExtensions.TryParseApiValue(timeframe ?? string.Empty, out var parsedTimeframe))
        return Results.BadRequest(new { error = "Timeframe must be one of: 5m, 15m, 1h, 4h." });

    var normalizedSymbol = symbol.Trim().ToUpperInvariant();
    try
    {
        var candles = await provider.GetCandlesAsync(normalizedSymbol, parsedTimeframe, 100, cancellationToken);
        var now = DateTime.UtcNow;
        var closedCandles = candles
            .Where(candle => candle.OpenTime + parsedTimeframe.Duration() <= now)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();

        if (closedCandles.Length == 0)
            return Results.NotFound(new { error = $"No closed {timeframe} candles are available for {normalizedSymbol}." });

        var closes = closedCandles.Select(candle => candle.Close).ToArray();
        return Results.Ok(new IndicatorSnapshot(
            normalizedSymbol,
            provider.GetProviderName(normalizedSymbol),
            parsedTimeframe,
            closedCandles[^1].OpenTime,
            closedCandles[^1].Close,
            indicators.CalculateEma(closes, 20),
            indicators.CalculateEma(closes, 50),
            indicators.CalculateRsi(closes, 14),
            indicators.CalculateAtr(closedCandles, 14),
            closedCandles.Length,
            closedCandles.Length >= 50 ? "Ready" : "Insufficient candle history"));
    }
    catch (MarketDataException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/symbols/add", async (
    ITradingAdvisoryService service,
    IMarketDataProvider provider,
    SymbolRequest request,
    CancellationToken cancellationToken) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Symbol))
        return Results.BadRequest(new { error = "A symbol is required." });

    var symbol = request.Symbol.Trim().ToUpperInvariant();

    try
    {
        if (!await provider.SymbolExistsAsync(symbol, cancellationToken))
            return Results.NotFound(new { error = $"{symbol} is not available from {provider.Name}." });
    }
    catch (MarketDataException exception)
    {
        return Results.Json(
            new { error = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return service.AddSymbol(symbol)
        ? Results.Ok(new { message = $"{symbol} is now being tracked." })
        : Results.Conflict(new { error = $"{symbol} is already being tracked." });
});

app.MapPost("/api/symbols/remove", (ITradingAdvisoryService service, SymbolRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Symbol))
        return Results.BadRequest(new { error = "A symbol is required." });

    return service.RemoveSymbol(request.Symbol)
        ? Results.Ok(new { message = "Symbol removed." })
        : Results.NotFound(new { error = "That symbol is not currently tracked." });
});

app.Map("/ws/advisories", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var service = context.RequestServices.GetRequiredService<ITradingAdvisoryService>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AdvisoryWebSocket");
    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    try
    {
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var payload = JsonSerializer.Serialize(
                service.Advisories.Select(a => a.ToResponse(service.GetPriceHistory(a.Symbol))));
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(payload),
                WebSocketMessageType.Text,
                true,
                context.RequestAborted);
            await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The browser disconnected or the application is stopping.
    }
    catch (WebSocketException exception)
    {
        logger.LogDebug(exception, "Advisory WebSocket disconnected unexpectedly.");
    }
});

var advisoryService = app.Services.GetRequiredService<ITradingAdvisoryService>();
advisoryService.Start(initialSymbols, app.Lifetime.ApplicationStopping);

app.Run();

static class AdvisoryResponseExtensions
{
    public static object ToResponse(this SymbolAdvisory advisory, IReadOnlyList<decimal> prices) => new
    {
        symbol = advisory.Symbol,
        price = advisory.Price,
        signal = advisory.Signal.ToString(),
        position = advisory.Position.ToString(),
        confidence = advisory.Confidence,
        reason = advisory.Reason,
        time = advisory.Time,
        provider = advisory.Provider,
        status = advisory.Status.ToString(),
        statusMessage = advisory.StatusMessage,
        entryPrice = advisory.EntryPrice,
        stopLoss = advisory.StopLoss,
        takeProfit1 = advisory.TakeProfit1,
        takeProfit2 = advisory.TakeProfit2,
        riskRewardTP1 = advisory.RiskRewardTP1,
        riskRewardTP2 = advisory.RiskRewardTP2,
        prices
    };
}

public partial class Program;
