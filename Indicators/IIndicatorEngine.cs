using TradingAutomationHub.Models;

namespace TradingAutomationHub.Indicators;

public interface IIndicatorEngine
{
    decimal? CalculateEma(IReadOnlyList<decimal> values, int period);
    decimal? CalculateRsi(IReadOnlyList<decimal> values, int period);
    decimal? CalculateAtr(IReadOnlyList<Candle> candles, int period);
}
