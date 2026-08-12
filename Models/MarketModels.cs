namespace TradingAutomationHub.Models;

public sealed record Candle(
    DateTime OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record MarketTick(string Symbol, DateTime Time, decimal Price);

public sealed class SymbolRequest
{
    public string Symbol { get; set; } = string.Empty;
}

public enum TradeSignal { Hold, Buy, Sell }
public enum PositionSide { Flat, Long, Short }
public enum MarketDataStatus { Waiting, Live, InvalidSymbol, ProviderUnavailable }

public sealed record SymbolAdvisory(
    string Symbol,
    decimal Price,
    TradeSignal Signal,
    PositionSide Position,
    string Reason,
    decimal Confidence,
    DateTime Time,
    string Provider,
    MarketDataStatus Status,
    string StatusMessage,
    SignalDirection Direction = SignalDirection.Hold,
    int TechnicalScore = 0,
    decimal TechnicalConfidence = 0m,
    decimal? EntryPrice = null,
    decimal? StopLoss = null,
    decimal? TakeProfit1 = null,
    decimal? TakeProfit2 = null,
    string? RiskRewardTP1 = null,
    string? RiskRewardTP2 = null,
    decimal? Ema20 = null,
    decimal? Ema50 = null,
    decimal? Rsi14 = null,
    decimal? Atr14 = null,
    DateTime? CandleCloseTime = null,
    IReadOnlyList<string>? Reasons = null);

public sealed record SignalRecord(
    string Symbol,
    decimal Price,
    TradeSignal Signal,
    SignalDirection Direction,
    string Reason,
    decimal Confidence,
    DateTime Time);

public sealed record TradeAction(TradeSignal Signal, decimal Price, DateTime Time, string Description);
