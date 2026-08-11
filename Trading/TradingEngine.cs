using TradingAutomationHub.Models;

namespace TradingAutomationHub.Trading;

public sealed class TradingEngine
{
    private readonly Queue<decimal> _shortPrices = new();
    private readonly Queue<decimal> _longPrices = new();
    private const int ShortWindow = 5;
    private const int LongWindow = 20;

    public TradeSignal CurrentSignal { get; private set; } = TradeSignal.Hold;
    public PositionSide Position { get; private set; } = PositionSide.Flat;
    public TradeAction? LastAction { get; private set; }
    public decimal Confidence { get; private set; }
    public string Reason { get; private set; } = "Waiting for enough market history.";

    public void OnTick(MarketTick tick)
    {
        AddToWindow(_shortPrices, tick.Price, ShortWindow);
        AddToWindow(_longPrices, tick.Price, LongWindow);

        if (_longPrices.Count < LongWindow)
        {
            CurrentSignal = TradeSignal.Hold;
            LastAction = null;
            Confidence = 0m;
            Reason = $"Collecting market history ({_longPrices.Count}/{LongWindow} updates).";
            return;
        }

        var shortSma = _shortPrices.Average();
        var longSma = _longPrices.Average();
        var momentum = (shortSma - longSma) / longSma;
        Confidence = Math.Clamp(Math.Abs(momentum) * 25m, 0m, 1m);

        if (momentum > 0.0015m)
        {
            CurrentSignal = TradeSignal.Buy;
            Reason = "Short-term trend is above the long-term trend.";
            if (Position != PositionSide.Long)
            {
                Position = PositionSide.Long;
                LastAction = new TradeAction(TradeSignal.Buy, tick.Price, tick.Time, "Advisory: momentum is bullish.");
            }
        }
        else if (momentum < -0.0015m)
        {
            CurrentSignal = TradeSignal.Sell;
            Reason = "Short-term trend is below the long-term trend.";
            if (Position != PositionSide.Short)
            {
                Position = PositionSide.Short;
                LastAction = new TradeAction(TradeSignal.Sell, tick.Price, tick.Time, "Advisory: momentum is bearish.");
            }
        }
        else
        {
            CurrentSignal = TradeSignal.Hold;
            Reason = "Market is range-bound; no strong signal.";
            LastAction = null;
        }
    }

    private static void AddToWindow(Queue<decimal> prices, decimal price, int size)
    {
        prices.Enqueue(price);
        while (prices.Count > size) prices.Dequeue();
    }
}
