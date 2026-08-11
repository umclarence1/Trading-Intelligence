using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, SymbolAdvisory> _advisories = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _symbolTokens = new();
    private readonly ConcurrentDictionary<string, Queue<decimal>> _priceHistory = new();
    private readonly List<SignalRecord> _history = new();
    private readonly object _historyLock = new();
    private CancellationToken _applicationStopping;

    public TradingAdvisoryService(
        IMarketDataProvider marketDataProvider,
        ILogger<TradingAdvisoryService> logger)
    {
        _marketDataProvider = marketDataProvider;
        _logger = logger;
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
        var engine = new TradingEngine();
        var retryDelay = TimeSpan.FromSeconds(3);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var tick in _marketDataProvider.GetTicksAsync(
                    symbol,
                    TimeSpan.FromSeconds(1),
                    cancellationToken))
                {
                    engine.OnTick(tick);
                    var advisory = new SymbolAdvisory(
                        tick.Symbol,
                        tick.Price,
                        engine.CurrentSignal,
                        engine.Position,
                        engine.Reason,
                        engine.Confidence,
                        tick.Time,
                        _marketDataProvider.GetProviderName(tick.Symbol),
                        MarketDataStatus.Live,
                        "Live market data");
                    _advisories[tick.Symbol] = advisory;
                    AddPrice(tick.Symbol, tick.Price);
                    RecordSignalChange(advisory);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (MarketDataException exception)
            {
                _logger.LogWarning(exception, "Market data unavailable for {Symbol}", symbol);
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

    private void RecordSignalChange(SymbolAdvisory advisory)
    {
        lock (_historyLock)
        {
            var previous = _history.LastOrDefault(r => r.Symbol == advisory.Symbol);
            if (previous?.Signal == advisory.Signal && previous.Reason == advisory.Reason) return;

            _history.Add(new SignalRecord(
                advisory.Symbol,
                advisory.Price,
                advisory.Signal,
                advisory.Reason,
                advisory.Confidence,
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
