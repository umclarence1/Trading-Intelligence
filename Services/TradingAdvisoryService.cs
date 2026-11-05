using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    IReadOnlyList<TrackedSignal> TrackedSignals { get; }
    void Start(IEnumerable<string> symbols, CancellationToken applicationStopping = default);
    bool AddSymbol(string symbol);
    bool RemoveSymbol(string symbol);
    IReadOnlyList<decimal> GetPriceHistory(string symbol);
}

public sealed class TradingAdvisoryService : ITradingAdvisoryService
{
    // Starting point measured by replaying this exact strategy (EMA20/EMA50/RSI score,
    // 15m XAUUSD) against real Twelve Data candles from 2026-05-13 to 2026-10-16
    // (~10,000 resolved signals), using the same risk model as RiskManagementEngine:
    // entry at next-bar open, stop at 1.5x ATR, target at 1x ATR, 3bps round-trip cost,
    // 24-bar max hold. Every bucket started net negative. From here, every real signal
    // this service tracks live gets resolved (win/loss/expired) and blended into these
    // counts, so the win rate shown keeps updating from real forward results instead of
    // staying frozen at this one-time backtest forever.
    private static readonly IReadOnlyDictionary<SignalDirection, (int Wins, int Losses)> SeedWinRate = new Dictionary<SignalDirection, (int, int)>
    {
        [SignalDirection.Buy] = (380, 813),
        [SignalDirection.StrongBuy] = (1067, 2003),
        [SignalDirection.Sell] = (502, 681),
        [SignalDirection.StrongSell] = (1333, 2108),
    };

    private sealed class DirectionStats
    {
        public int Wins;
        public int Losses;
        public decimal WinRate => (Wins + Losses) == 0 ? 0m : (decimal)Wins / (Wins + Losses);
    }

    private readonly ConcurrentDictionary<SignalDirection, DirectionStats> _winRateStats = new(
        SeedWinRate.ToDictionary(kv => kv.Key, kv => new DirectionStats { Wins = kv.Value.Wins, Losses = kv.Value.Losses }));

    private const int PriceHistorySize = 120;
    private const int TrackedSignalHistorySize = 300;
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

    private readonly TelegramNotifier _telegramNotifier;
    private readonly List<TrackedSignal> _trackedSignals = new();
    private readonly object _trackingLock = new();
    private readonly string? _trackingStoragePath;

    public TradingAdvisoryService(
        IMarketDataProvider marketDataProvider,
        ILogger<TradingAdvisoryService> logger,
        IIndicatorEngine indicatorEngine,
        RiskManagementEngine riskManagement,
        TradingSettings settings,
        TelegramNotifier telegramNotifier,
        IWebHostEnvironment environment)
    {
        _marketDataProvider = marketDataProvider;
        _logger = logger;
        _indicatorEngine = indicatorEngine;
        _riskManagement = riskManagement;
        _settings = settings;
        _telegramNotifier = telegramNotifier;
        _trackingStoragePath = Path.Combine(environment.ContentRootPath, "App_Data", "signal-outcomes.json");
        LoadTrackedSignals();
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

    public IReadOnlyList<TrackedSignal> TrackedSignals
    {
        get
        {
            lock (_trackingLock) return _trackedSignals.ToArray();
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
        var baseRetryDelay = TimeSpan.FromSeconds(3);
        var maxRetryDelay = TimeSpan.FromMinutes(5);
        var consecutiveFailures = 0;
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
                    consecutiveFailures = 0;
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
                // Exponential backoff, capped at 5 minutes: a sustained failure (e.g. a
                // provider's daily quota being exhausted) must not retry every few
                // seconds, since each retry still consumes quota and only deepens and
                // prolongs the outage. A transient blip still recovers quickly.
                consecutiveFailures++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(
                    baseRetryDelay.TotalMilliseconds * Math.Pow(2, consecutiveFailures - 1),
                    maxRetryDelay.TotalMilliseconds));
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected polling failure for {Symbol}", symbol);
                _lastErrors[symbol] = exception.Message;
                consecutiveFailures++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(
                    baseRetryDelay.TotalMilliseconds * Math.Pow(2, consecutiveFailures - 1),
                    maxRetryDelay.TotalMilliseconds));
                await Task.Delay(delay, cancellationToken);
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
        ResolveTrackedSignals(tick);
    }

    private void ResolveTrackedSignals(MarketTick tick)
    {
        List<TrackedSignal>? resolved = null;
        lock (_trackingLock)
        {
            for (var i = 0; i < _trackedSignals.Count; i++)
            {
                var tracked = _trackedSignals[i];
                if (tracked.Symbol != tick.Symbol || tracked.Outcome != SignalOutcome.Open) continue;

                var isBuy = tracked.Direction is SignalDirection.Buy or SignalDirection.StrongBuy;
                var stopHit = isBuy ? tick.Price <= tracked.StopLoss : tick.Price >= tracked.StopLoss;
                var targetHit = isBuy ? tick.Price >= tracked.Target : tick.Price <= tracked.Target;
                var expired = !stopHit && !targetHit && tick.Time >= tracked.ExpiresAt;
                if (!stopHit && !targetHit && !expired) continue;

                var outcome = stopHit ? SignalOutcome.Loss : targetHit ? SignalOutcome.Win : SignalOutcome.Expired;
                var exitPrice = stopHit ? tracked.StopLoss : targetHit ? tracked.Target : tick.Price;
                var risk = Math.Abs(tracked.Entry - tracked.StopLoss);
                var resultR = risk <= 0m ? 0m : (isBuy ? exitPrice - tracked.Entry : tracked.Entry - exitPrice) / risk;

                var closed = tracked with { Outcome = outcome, ResultR = decimal.Round(resultR, 4), ClosedAt = tick.Time };
                _trackedSignals[i] = closed;
                (resolved ??= new List<TrackedSignal>()).Add(closed);

                if (outcome is SignalOutcome.Win or SignalOutcome.Loss)
                {
                    var stats = _winRateStats.GetOrAdd(tracked.Direction, _ => new DirectionStats());
                    if (outcome == SignalOutcome.Win) Interlocked.Increment(ref stats.Wins);
                    else Interlocked.Increment(ref stats.Losses);
                }
            }
        }

        if (resolved is not null) PersistTrackedSignals();
    }

    private void RegisterTrackedSignal(string symbol, SignalDirection direction, RiskPlan riskPlan, DateTime openedAt, TimeSpan timeframeDuration)
    {
        if (riskPlan.Entry is null || riskPlan.StopLoss is null || riskPlan.TakeProfit1 is null) return;

        lock (_trackingLock)
        {
            var alreadyTracked = _trackedSignals.Any(t => t.Symbol == symbol && t.Direction == direction && t.OpenedAt == openedAt);
            if (alreadyTracked) return;

            _trackedSignals.Add(new TrackedSignal(
                Guid.NewGuid(), symbol, direction, riskPlan.Entry.Value, riskPlan.StopLoss.Value, riskPlan.TakeProfit1.Value,
                openedAt, openedAt + TimeSpan.FromTicks(timeframeDuration.Ticks * Math.Max(1, _settings.SignalTrackingHoldingBars)),
                SignalOutcome.Open, null, null));
            while (_trackedSignals.Count > TrackedSignalHistorySize) _trackedSignals.RemoveAt(0);
        }
        PersistTrackedSignals();
    }

    private void PersistTrackedSignals()
    {
        if (_trackingStoragePath is null) return;
        try
        {
            List<TrackedSignal> snapshot;
            Dictionary<SignalDirection, DirectionStats> statsSnapshot;
            lock (_trackingLock) snapshot = _trackedSignals.ToList();
            statsSnapshot = _winRateStats.ToDictionary(kv => kv.Key, kv => kv.Value);

            Directory.CreateDirectory(Path.GetDirectoryName(_trackingStoragePath)!);
            var payload = new PersistedTracking(snapshot, statsSnapshot.ToDictionary(kv => kv.Key, kv => new DirectionStatSnapshot(kv.Value.Wins, kv.Value.Losses)));
            var temporaryPath = _trackingStoragePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload));
            File.Move(temporaryPath, _trackingStoragePath, true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to persist tracked signal outcomes.");
        }
    }

    private void LoadTrackedSignals()
    {
        if (_trackingStoragePath is null || !File.Exists(_trackingStoragePath)) return;
        try
        {
            var payload = JsonSerializer.Deserialize<PersistedTracking>(File.ReadAllText(_trackingStoragePath));
            if (payload is null) return;

            lock (_trackingLock)
            {
                _trackedSignals.Clear();
                _trackedSignals.AddRange(payload.Signals.TakeLast(TrackedSignalHistorySize));
            }
            foreach (var (direction, counts) in payload.Stats)
                _winRateStats[direction] = new DirectionStats { Wins = counts.Wins, Losses = counts.Losses };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to load persisted signal outcomes.");
        }
    }

    private sealed record DirectionStatSnapshot(int Wins, int Losses);
    private sealed record PersistedTracking(List<TrackedSignal> Signals, Dictionary<SignalDirection, DirectionStatSnapshot> Stats);

    private async Task EvaluateAdvisoryForClosedCandle(string symbol, DateTime now, CancellationToken cancellationToken, TradingEngine engine)
    {
        var timeframe = _settings.PrimaryTimeframeEnum;
        var closedCandleTime = TruncateToCandleClose(now, timeframe.Duration());
        var candleLimit = Math.Max(_settings.SlowEmaPeriod, _settings.AtrPeriod) + 50;
        var candles = await _marketDataProvider.GetCandlesAsync(symbol, timeframe, candleLimit, cancellationToken);
        var evaluation = engine.EvaluateClosedCandles(candles, timeframe);

        if (!_advisories.TryGetValue(symbol, out var previousAdvisory))
            previousAdvisory = WaitingAdvisory(symbol);

        var isDirectional = evaluation.Direction is SignalDirection.Buy or SignalDirection.StrongBuy or SignalDirection.Sell or SignalDirection.StrongSell;
        var direction = evaluation.Direction;
        var reasons = evaluation.Reasons;
        var confidence = evaluation.Confidence;

        // This strategy has not been validated against an out-of-sample backtest, so
        // rather than hiding its calls, show the real win rate measured for this exact
        // signal type — seeded from a one-time backtest, then continuously updated from
        // every live signal this service has tracked to a resolved outcome since, so
        // the number keeps reflecting real, ongoing performance instead of staying
        // frozen. All buckets started net negative.
        if (isDirectional && !_settings.PublishUnvalidatedSignals && _winRateStats.TryGetValue(direction, out var stats))
        {
            var realWinRate = stats.WinRate;
            reasons = reasons.Append(
                $"UNVALIDATED: {direction} calls like this have won {realWinRate:P0} of the time across {stats.Wins + stats.Losses:N0} resolved signals. Do not trade this signal.")
                .ToArray();
            confidence = realWinRate;
        }

        var signal = direction switch
        {
            SignalDirection.StrongBuy => TradeSignal.Buy,
            SignalDirection.Buy => TradeSignal.Buy,
            SignalDirection.StrongSell => TradeSignal.Sell,
            SignalDirection.Sell => TradeSignal.Sell,
            _ => TradeSignal.Hold
        };

        var riskPlan = isDirectional
            ? _riskManagement.BuildRiskPlan(evaluation.ClosePrice, evaluation.Atr14 ?? 0m, direction)
            : RiskPlan.Empty;

        if (isDirectional)
            RegisterTrackedSignal(symbol, direction, riskPlan, closedCandleTime, timeframe.Duration());

        var advisory = previousAdvisory with
        {
            Signal = signal,
            Direction = direction,
            Confidence = confidence,
            TechnicalScore = evaluation.Score,
            TechnicalConfidence = evaluation.Confidence,
            Reason = string.Join(" ", reasons),
            Reasons = reasons,
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
                advisory.Confidence,
                advisory.Time));
            if (_history.Count > 200) _history.RemoveAt(0);
        }

        if (advisory.Signal != TradeSignal.Hold)
            _ = _telegramNotifier.SendAsync(FormatTelegramMessage(advisory));
    }

    private static string FormatTelegramMessage(SymbolAdvisory advisory)
    {
        var emoji = advisory.Signal == TradeSignal.Buy ? "\U0001F7E2" : "\U0001F534"; // green/red circle
        var lines = new List<string>
        {
            $"{emoji} <b>{advisory.Signal.ToString().ToUpperInvariant()} {advisory.Symbol}</b> at {advisory.Price:F2}",
            $"Historical win rate: {advisory.Confidence:P0}",
        };
        if (advisory.EntryPrice is not null)
            lines.Add($"Entry {advisory.EntryPrice:F2} | SL {advisory.StopLoss:F2} | TP1 {advisory.TakeProfit1:F2} | TP2 {advisory.TakeProfit2:F2}");
        lines.Add(advisory.Reason);
        return string.Join("\n", lines);
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
