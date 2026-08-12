using TradingAutomationHub.Indicators;
using TradingAutomationHub.Models;
using TradingAutomationHub.Enums;

namespace TradingAutomationHub.Trading;

public sealed class TradingEngine
{
    private readonly IIndicatorEngine _indicators;
    private readonly RiskManagementEngine _riskManagement;
    private readonly TradingSettings _settings;

    private readonly Queue<decimal> _shortPrices = new();
    private readonly Queue<decimal> _longPrices = new();
    private const int ShortWindow = 5;
    private const int LongWindow = 20;

    public TradeSignal CurrentSignal { get; private set; } = TradeSignal.Hold;
    public PositionSide Position { get; private set; } = PositionSide.Flat;
    public TradeAction? LastAction { get; private set; }
    public decimal Confidence { get; private set; }
    public string Reason { get; private set; } = "Waiting for enough market history.";

    public TradingEngine(IIndicatorEngine indicators, RiskManagementEngine riskManagement, TradingSettings settings)
    {
        _indicators = indicators;
        _riskManagement = riskManagement;
        _settings = settings;
    }

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

    public SignalEvaluationResult EvaluateClosedCandles(IReadOnlyList<Candle> candles, Timeframe timeframe)
    {
        if (candles is null) throw new ArgumentNullException(nameof(candles));

        var closedCandles = candles
            .Where(candle => candle.OpenTime + timeframe.Duration() <= DateTime.UtcNow)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();

        if (closedCandles.Length == 0)
        {
            return new SignalEvaluationResult(
                SignalDirection.Hold,
                0,
                0.25m,
                AdvisoryStatus.Loading,
                new[] { "Waiting for the first closed candle." },
                null,
                null,
                null,
                null,
                0m,
                closedCandles.Length > 0 ? closedCandles[^1].OpenTime : DateTime.MinValue,
                false);
        }

        var requiredHistory = Math.Max(_settings.SlowEmaPeriod, _settings.AtrPeriod);
        var candleCloseTime = closedCandles[^1].OpenTime;
        if (closedCandles.Length < requiredHistory)
        {
            return new SignalEvaluationResult(
                SignalDirection.Hold,
                0,
                0.25m,
                AdvisoryStatus.InsufficientHistory,
                new[] { $"Insufficient candle history: {closedCandles.Length}/{requiredHistory} closed candles." },
                null,
                null,
                null,
                null,
                0m,
                candleCloseTime,
                false);
        }

        var closes = closedCandles.Select(candle => candle.Close).ToArray();
        var ema20 = _indicators.CalculateEma(closes, _settings.FastEmaPeriod);
        var ema50 = _indicators.CalculateEma(closes, _settings.SlowEmaPeriod);
        var rsi14 = _indicators.CalculateRsi(closes, _settings.RsiPeriod);
        var atr14 = _indicators.CalculateAtr(closedCandles, _settings.AtrPeriod);
        var lastClose = closes[^1];

        if (ema20 is null || ema50 is null || rsi14 is null || atr14 is null)
        {
            return new SignalEvaluationResult(
                SignalDirection.Hold,
                0,
                0.25m,
                AdvisoryStatus.InsufficientHistory,
                new[] { "Not enough closed candle history to compute all required indicators." },
                ema20,
                ema50,
                rsi14,
                atr14,
                lastClose,
                candleCloseTime,
                false);
        }

        var score = 0;
        var reasons = new List<string>();

        if (ema20 > ema50)
        {
            score += 2;
            reasons.Add("EMA20 is above EMA50, indicating a bullish short-term trend.");
        }
        else if (ema20 < ema50)
        {
            score -= 2;
            reasons.Add("EMA20 is below EMA50, indicating a bearish short-term trend.");
        }
        else
        {
            reasons.Add("EMA20 and EMA50 are aligned.");
        }

        if (lastClose > ema20)
        {
            score += 1;
            reasons.Add("Price closed above EMA20.");
        }
        else if (lastClose < ema20)
        {
            score -= 1;
            reasons.Add("Price closed below EMA20.");
        }
        else
        {
            reasons.Add("Price closed at EMA20.");
        }

        if (rsi14 >= _settings.BullishRsiMinimum && rsi14 <= _settings.BullishRsiMaximum)
        {
            score += 1;
            reasons.Add("RSI is in a bullish range.");
        }
        else if (rsi14 >= _settings.BearishRsiMinimum && rsi14 <= _settings.BearishRsiMaximum)
        {
            score -= 1;
            reasons.Add("RSI is in a bearish range.");
        }
        else if (rsi14 > _settings.BullishRsiMaximum)
        {
            reasons.Add("RSI is above the bullish range and may be overextended.");
        }
        else if (rsi14 < _settings.BearishRsiMinimum)
        {
            reasons.Add("RSI is below the bearish range and may be oversold.");
        }
        else
        {
            reasons.Add("RSI is neutral.");
        }

        var direction = DetermineDirection(score);
        var confidence = DetermineConfidence(score);

        return new SignalEvaluationResult(
            direction,
            score,
            confidence,
            AdvisoryStatus.Ready,
            reasons,
            ema20,
            ema50,
            rsi14,
            atr14,
            lastClose,
            closedCandles[^1].OpenTime,
            true);
    }

    private static SignalDirection DetermineDirection(int score)
    {
        return score switch
        {
            >= 4 => SignalDirection.StrongBuy,
            >= 2 => SignalDirection.Buy,
            <= -4 => SignalDirection.StrongSell,
            <= -2 => SignalDirection.Sell,
            _ => SignalDirection.Hold
        };
    }

    private static decimal DetermineConfidence(int score)
    {
        var absoluteScore = Math.Abs(score);
        return absoluteScore switch
        {
            0 => 0.25m,
            1 => 0.40m,
            2 => 0.55m,
            3 => 0.75m,
            _ => 0.90m
        };
    }

    private static void AddToWindow(Queue<decimal> prices, decimal price, int size)
    {
        prices.Enqueue(price);
        while (prices.Count > size) prices.Dequeue();
    }
}
