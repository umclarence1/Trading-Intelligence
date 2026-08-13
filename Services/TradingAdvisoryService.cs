using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TradingAutomationHub.Indicators;
using TradingAutomationHub.Enums;
using TradingAutomationHub.MarketData;
using TradingAutomationHub.Models;
using TradingAutomationHub.Trading;

namespace TradingAutomationHub.Services;

public interface ITradingAdvisoryService
{
    IReadOnlyList<SymbolAdvisory> Advisories { get; }
    IReadOnlyList<SignalRecord> SignalHistory { get; }
    void Start(IEnumerable<string> symbols, CancellationToken applicationStopping = default);
    bool AddSymbol(string symbol);
    bool RemoveSymbol(string symbol);
    IReadOnlyList<decimal> GetPriceHistory(string symbol);
}

public sealed class TradingAdvisoryService : ITradingAdvisoryService
{
    private const int PriceHistorySize = 120;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly ILogger<TradingAdvisoryService> _logger;
    private readonly IIndicatorEngine _indicatorEngine;
    private readonly RiskManagementEngine _riskManagement;
    private readonly TradingSettings _settings;
    private readonly ConcurrentDictionary<string, SymbolAdvisory> _advisories = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _symbolTokens = new();
    private readonly ConcurrentDictionary<string, Queue<decimal>> _priceHistory = new();
        private readonly ConcurrentDictionary<string, string?> _lastErrors = new();
    private readonly List<SignalRecord> _history = new();
    private readonly object _historyLock = new();
    private CancellationToken _applicationStopping;

    public TradingAdvisoryService(
        IMarketDataProvider marketDataProvider,
        ILogger<TradingAdvisoryService> logger,
        IIndicatorEngine indicatorEngine,
        RiskManagementEngine riskManagement,
        TradingSettings settings)
    {
        _marketDataProvider = marketDataProvider;
        _logger = logger;
        _indicatorEngine = indicatorEngine;
        _riskManagement = riskManagement;
        _settings = settings;
    }

    public IReadOnlyList<SymbolAdvisory> Advisories =>
        _advisories.Values.OrderBy(a => a.Symbol).ToArray();

    public IReadOnlyList<SignalRecord> SignalHistory
    {
        get
        {
            lock (_historyLock) return _history.ToArray();
        }
    }

    // Diagnostics for external inspection
    public IReadOnlyDictionary<string, string?> LastErrors => _lastErrors;

    public void Start(IEnumerable<string> symbols, CancellationToken applicationStopping = default)
    {
        _applicationStopping = applicationStopping;
        foreach (var symbol in symbols.Select(NormalizeSymbol).Distinct())
            StartSymbol(symbol);
    }

    public bool AddSymbol(string symbol)
    {
        symbol = NormalizeSymbol(symbol);
        if (_advisories.ContainsKey(symbol)) return false;
        StartSymbol(symbol);
        return true;
    }

    public bool RemoveSymbol(string symbol)
    {
        symbol = NormalizeSymbol(symbol);
        if (!_advisories.TryRemove(symbol, out _)) return false;

        if (_symbolTokens.TryRemove(symbol, out var source))
        {
            source.Cancel();
            source.Dispose();
        }

        _priceHistory.TryRemove(symbol, out _);
        return true;
    }

    public IReadOnlyList<decimal> GetPriceHistory(string symbol)
    {
        symbol = NormalizeSymbol(symbol);
        if (!_priceHistory.TryGetValue(symbol, out var prices)) return Array.Empty<decimal>();
        lock (prices) return prices.ToArray();
    }

    private void StartSymbol(string symbol)
    {
        _advisories[symbol] = WaitingAdvisory(symbol);
        _priceHistory.TryAdd(symbol, new Queue<decimal>());
        var source = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
        _symbolTokens[symbol] = source;
        _ = RunSymbolAsync(symbol, source.Token);
    }

    private async Task RunSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        var engine = new TradingEngine(_indicatorEngine, _riskManagement, _settings);
        var retryDelay = TimeSpan.FromSeconds(3);
        TimeSpan timeframeDuration = _settings.PrimaryTimeframeEnum.Duration();
        DateTime? lastEvaluatedClose = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var tick in _marketDataProvider.GetTicksAsync(
                    symbol,
                    TimeSpan.FromSeconds(1),
                    cancellationToken))
                {
                    AddPrice(tick.Symbol, tick.Price);
                    UpdateLivePrice(tick);

                    var closedCandleClose = GetCurrentClosedCandleClose(tick.Time, timeframeDuration);
                    if (lastEvaluatedClose is null || closedCandleClose > lastEvaluatedClose)
                    {
                        lastEvaluatedClose = closedCandleClose;
                        await EvaluateAdvisoryForClosedCandle(symbol, tick.Time, cancellationToken, engine);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (MarketDataException exception)
            {
                _logger.LogWarning(exception, "Market data unavailable for {Symbol}", symbol);
                _lastErrors[symbol] = exception.Message;
                if (_advisories.TryGetValue(symbol, out var current))
                {
                    _advisories[symbol] = current with
                    {
                        Status = MarketDataStatus.ProviderUnavailable,
                        StatusMessage = exception.Message,
                        Time = DateTime.UtcNow
                    };
                }
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected polling failure for {Symbol}", symbol);
                _lastErrors[symbol] = exception.Message;
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private void AddPrice(string symbol, decimal price)
    {
        var prices = _priceHistory.GetOrAdd(symbol, _ => new Queue<decimal>());
        lock (prices)
        {
            prices.Enqueue(price);
            while (prices.Count > PriceHistorySize) prices.Dequeue();
        }
    }

    private void UpdateLivePrice(MarketTick tick)
    {
        if (!_advisories.TryGetValue(tick.Symbol, out var existing))
            existing = WaitingAdvisory(tick.Symbol);

        var updated = existing with
        {
            Price = tick.Price,
            Time = tick.Time,
            Status = MarketDataStatus.Live,
            StatusMessage = "Live market data"
        };

        _advisories[tick.Symbol] = updated;
    }

    private async Task EvaluateAdvisoryForClosedCandle(string symbol, DateTime now, CancellationToken cancellationToken, TradingEngine engine)
    {
        var timeframe = _settings.PrimaryTimeframeEnum;
        var closedCandleTime = TruncateToCandleClose(now, timeframe.Duration());
        var candleLimit = Math.Max(_settings.SlowEmaPeriod, _settings.AtrPeriod) + 50;
        var candles = await _marketDataProvider.GetCandlesAsync(symbol, timeframe, candleLimit, cancellationToken);
        var evaluation = engine.EvaluateClosedCandles(candles, timeframe);

        if (!_advisories.TryGetValue(symbol, out var previousAdvisory))
            previousAdvisory = WaitingAdvisory(symbol);

        var signal = evaluation.Direction switch
        {
            SignalDirection.StrongBuy => TradeSignal.Buy,
            SignalDirection.Buy => TradeSignal.Buy,
            SignalDirection.StrongSell => TradeSignal.Sell,
            SignalDirection.Sell => TradeSignal.Sell,
            _ => TradeSignal.Hold
        };

        var riskPlan = evaluation.Direction is SignalDirection.Buy or SignalDirection.StrongBuy or SignalDirection.Sell or SignalDirection.StrongSell
            ? _riskManagement.BuildRiskPlan(evaluation.ClosePrice, evaluation.Atr14 ?? 0m, evaluation.Direction)
            : RiskPlan.Empty;

        var advisory = previousAdvisory with
        {
            Signal = signal,
            Direction = evaluation.Direction,
            Confidence = evaluation.Confidence,
            TechnicalScore = evaluation.Score,
            TechnicalConfidence = evaluation.Confidence,
            Reason = string.Join(" ", evaluation.Reasons),
            Reasons = evaluation.Reasons,
            Ema20 = evaluation.Ema20,
            Ema50 = evaluation.Ema50,
            Rsi14 = evaluation.Rsi14,
            Atr14 = evaluation.Atr14,
            CandleCloseTime = closedCandleTime,
            EntryPrice = riskPlan.Entry,
            StopLoss = riskPlan.StopLoss,
            TakeProfit1 = riskPlan.TakeProfit1,
            TakeProfit2 = riskPlan.TakeProfit2,
            RiskRewardTP1 = riskPlan.TakeProfit1RiskReward,
            RiskRewardTP2 = riskPlan.TakeProfit2RiskReward,
            Status = evaluation.DataStatus switch
            {
                AdvisoryStatus.Ready => MarketDataStatus.Live,
                AdvisoryStatus.InsufficientHistory => MarketDataStatus.Waiting,
                AdvisoryStatus.Loading => MarketDataStatus.Waiting,
                AdvisoryStatus.ProviderUnavailable => MarketDataStatus.ProviderUnavailable,
                AdvisoryStatus.InvalidSymbol => MarketDataStatus.InvalidSymbol,
                AdvisoryStatus.Error => MarketDataStatus.ProviderUnavailable,
                _ => MarketDataStatus.Waiting
            },
            StatusMessage = evaluation.DataStatus == AdvisoryStatus.Ready
                ? "Ready"
                : evaluation.Reasons.FirstOrDefault() ?? evaluation.DataStatus.ToString(),
            Time = now
        };

        _advisories[symbol] = advisory;
        RecordSignalChange(advisory);
    }

    private static DateTime GetCurrentClosedCandleClose(DateTime time, TimeSpan timeframe)
    {
        var elapsed = time - DateTime.UnixEpoch;
        var periods = (long)(elapsed.Ticks / timeframe.Ticks);
        return DateTime.UnixEpoch.AddTicks((periods * timeframe.Ticks) + timeframe.Ticks);
    }

    private static DateTime TruncateToCandleClose(DateTime time, TimeSpan timeframe)
    {
        var elapsed = time - DateTime.UnixEpoch;
        var periods = (long)(elapsed.Ticks / timeframe.Ticks);
        return DateTime.UnixEpoch.AddTicks(periods * timeframe.Ticks + timeframe.Ticks);
    }

    private void RecordSignalChange(SymbolAdvisory advisory)
    {
        lock (_historyLock)
        {
            var previous = _history.LastOrDefault(r => r.Symbol == advisory.Symbol);
            if (previous?.Signal == advisory.Signal && previous?.Direction == advisory.Direction && previous?.Reason == advisory.Reason) return;

            _history.Add(new SignalRecord(
                advisory.Symbol,
                advisory.Price,
                advisory.Signal,
                advisory.Direction,
                advisory.Reason,
                advisory.TechnicalConfidence,
                advisory.Time));
            if (_history.Count > 200) _history.RemoveAt(0);
        }
    }

    private SymbolAdvisory WaitingAdvisory(string symbol) => new(
        symbol,
        0m,
        TradeSignal.Hold,
        PositionSide.Flat,
        "Waiting for the first market update.",
        0m,
        DateTime.UtcNow,
        _marketDataProvider.GetProviderName(symbol),
        MarketDataStatus.Waiting,
        "Connecting to market data");

    private static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();
}
