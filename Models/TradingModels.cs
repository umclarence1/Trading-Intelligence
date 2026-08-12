using TradingAutomationHub.Enums;

namespace TradingAutomationHub.Models;

public enum SignalDirection
{
    StrongBuy,
    Buy,
    Hold,
    Sell,
    StrongSell
}

public enum AdvisoryStatus
{
    Ready,
    Loading,
    InsufficientHistory,
    InvalidSymbol,
    ProviderUnavailable,
    Error
}

public sealed record RiskPlan(
    decimal? Entry,
    decimal? StopLoss,
    decimal? TakeProfit1,
    decimal? TakeProfit2,
    decimal? RiskDistance,
    string TakeProfit1RiskReward,
    string TakeProfit2RiskReward)
{
    public static RiskPlan Empty { get; } = new(null, null, null, null, null, string.Empty, string.Empty);
}

public sealed record TradingAdvisory(
    string Symbol,
    string Provider,
    decimal CurrentPrice,
    SignalDirection Direction,
    Timeframe PrimaryTimeframe,
    int TechnicalScore,
    decimal TechnicalConfidence,
    decimal? EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit1,
    decimal? TakeProfit2,
    string? RiskRewardTP1,
    string? RiskRewardTP2,
    decimal? Ema20,
    decimal? Ema50,
    decimal? Rsi14,
    decimal? Atr14,
    DateTime CandleCloseTime,
    AdvisoryStatus Status,
    string Reason,
    IReadOnlyList<string> Reasons,
    DateTime UpdatedAt);
