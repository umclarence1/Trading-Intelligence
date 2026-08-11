using TradingAutomationHub.Models;

namespace TradingAutomationHub.Indicators;

public sealed class IndicatorEngine : IIndicatorEngine
{
    public decimal? CalculateEma(IReadOnlyList<decimal> values, int period)
    {
        ValidatePeriod(period);
        if (values.Count < period) return null;

        var ema = values.Take(period).Average();
        var multiplier = 2m / (period + 1m);
        for (var index = period; index < values.Count; index++)
            ema = ((values[index] - ema) * multiplier) + ema;

        return ema;
    }

    public decimal? CalculateRsi(IReadOnlyList<decimal> values, int period)
    {
        ValidatePeriod(period);
        if (values.Count < period + 1) return null;

        decimal gains = 0m;
        decimal losses = 0m;
        for (var index = 1; index <= period; index++)
        {
            var change = values[index] - values[index - 1];
            if (change > 0) gains += change;
            else losses -= change;
        }

        var averageGain = gains / period;
        var averageLoss = losses / period;
        for (var index = period + 1; index < values.Count; index++)
        {
            var change = values[index] - values[index - 1];
            var gain = Math.Max(change, 0m);
            var loss = Math.Max(-change, 0m);
            averageGain = ((averageGain * (period - 1)) + gain) / period;
            averageLoss = ((averageLoss * (period - 1)) + loss) / period;
        }

        if (averageLoss == 0m) return averageGain == 0m ? 50m : 100m;
        var relativeStrength = averageGain / averageLoss;
        return 100m - (100m / (1m + relativeStrength));
    }

    public decimal? CalculateAtr(IReadOnlyList<Candle> candles, int period)
    {
        ValidatePeriod(period);
        if (candles.Count < period + 1) return null;

        var trueRanges = new List<decimal>(candles.Count - 1);
        for (var index = 1; index < candles.Count; index++)
        {
            var candle = candles[index];
            var previousClose = candles[index - 1].Close;
            trueRanges.Add(Math.Max(
                candle.High - candle.Low,
                Math.Max(Math.Abs(candle.High - previousClose), Math.Abs(candle.Low - previousClose))));
        }

        var atr = trueRanges.Take(period).Average();
        for (var index = period; index < trueRanges.Count; index++)
            atr = ((atr * (period - 1)) + trueRanges[index]) / period;

        return atr;
    }

    private static void ValidatePeriod(int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period), "Indicator period must be positive.");
    }
}
