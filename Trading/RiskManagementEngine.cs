using TradingAutomationHub.Models;

namespace TradingAutomationHub.Trading;

public sealed class RiskManagementEngine
{
    private readonly TradingSettings _settings;

    public RiskManagementEngine(TradingSettings settings)
    {
        _settings = settings;
    }

    public RiskPlan BuildRiskPlan(decimal entryPrice, decimal atr, SignalDirection direction)
    {
        var riskDistance = atr * _settings.AtrStopMultiplier;
        if (riskDistance <= 0m)
            throw new ArgumentOutOfRangeException(nameof(atr), "ATR must be positive for risk calculations.");

        if (direction is SignalDirection.Buy or SignalDirection.StrongBuy)
        {
            var stopLoss = entryPrice - riskDistance;
            var tp1 = entryPrice + (riskDistance * _settings.TakeProfit1RiskMultiple);
            var tp2 = entryPrice + (riskDistance * _settings.TakeProfit2RiskMultiple);
            return new RiskPlan(entryPrice, stopLoss, tp1, tp2, riskDistance, "1 : 1", "1 : 2");
        }

        if (direction is SignalDirection.Sell or SignalDirection.StrongSell)
        {
            var stopLoss = entryPrice + riskDistance;
            var tp1 = entryPrice - (riskDistance * _settings.TakeProfit1RiskMultiple);
            var tp2 = entryPrice - (riskDistance * _settings.TakeProfit2RiskMultiple);
            return new RiskPlan(entryPrice, stopLoss, tp1, tp2, riskDistance, "1 : 1", "1 : 2");
        }

        return RiskPlan.Empty;
    }
}
