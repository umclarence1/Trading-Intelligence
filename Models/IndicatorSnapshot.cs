using TradingAutomationHub.Enums;

namespace TradingAutomationHub.Models;

public sealed record IndicatorSnapshot(
    string Symbol,
    string Provider,
    Timeframe Timeframe,
    DateTime LastClosedCandleTime,
    decimal LastClose,
    decimal? Ema20,
    decimal? Ema50,
    decimal? Rsi14,
    decimal? Atr14,
    int ClosedCandleCount,
    string Status);
