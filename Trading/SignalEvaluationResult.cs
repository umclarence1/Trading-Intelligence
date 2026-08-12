using TradingAutomationHub.Enums;
using TradingAutomationHub.Models;

namespace TradingAutomationHub.Trading;

public sealed record SignalEvaluationResult(
    SignalDirection Direction,
    int Score,
    decimal Confidence,
    AdvisoryStatus DataStatus,
    IReadOnlyList<string> Reasons,
    decimal? Ema20,
    decimal? Ema50,
    decimal? Rsi14,
    decimal? Atr14,
    decimal ClosePrice,
    DateTime CandleCloseTime,
    bool HasEnoughData);
