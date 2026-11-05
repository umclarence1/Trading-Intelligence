using System.Collections.Generic;
using TradingAutomationHub.Enums;

namespace TradingAutomationHub;

public sealed class TradingSettings
{
    public const string SectionName = "TradingSettings";

    public string PrimaryTimeframe { get; set; } = "15m";

    public int FastEmaPeriod { get; set; } = 20;
    public int SlowEmaPeriod { get; set; } = 50;

    public int RsiPeriod { get; set; } = 14;
    public int AtrPeriod { get; set; } = 14;

    public decimal BullishRsiMinimum { get; set; } = 52m;
    public decimal BullishRsiMaximum { get; set; } = 68m;
    public decimal BearishRsiMinimum { get; set; } = 32m;
    public decimal BearishRsiMaximum { get; set; } = 48m;

    public int StrongBuyThreshold { get; set; } = 4;
    public int BuyThreshold { get; set; } = 2;
    public int SellThreshold { get; set; } = -2;
    public int StrongSellThreshold { get; set; } = -4;

    public decimal AtrStopMultiplier { get; set; } = 1.5m;
    public decimal TakeProfit1RiskMultiple { get; set; } = 1.0m;
    public decimal TakeProfit2RiskMultiple { get; set; } = 2.0m;

    // Safety-first default: this strategy has not been validated against an
    // out-of-sample backtest, so directional Buy/Sell advisories are suppressed
    // to Hold until it is. Set true only once a strategy has proven an edge.
    public bool PublishUnvalidatedSignals { get; set; } = false;

    // How many closed candles a tracked signal is allowed to run before it's
    // marked Expired (and marked to market) if neither the stop nor the target
    // was hit. 24 bars on the default 15m timeframe is 6 hours.
    public int SignalTrackingHoldingBars { get; set; } = 24;

    // A signal direction is only promoted out of "UNVALIDATED" automatically once
    // its OWN live, resolved trades (not the seed backtest) clear every one of
    // these bars at once. Deliberately matches the same thresholds used elsewhere
    // for strategy validation, so promotion requires real, out-of-sample proof,
    // not a lucky streak. It is re-checked after every resolution and can demote
    // again if live performance later falls back below the bar.
    public int MinimumValidationTrades { get; set; } = 100;
    public decimal MinimumValidationProfitFactor { get; set; } = 1.25m;
    public decimal MaximumValidationDrawdownR { get; set; } = 10m;

    public Dictionary<string, int> TimeframeWeights { get; set; } = new()
    {
        ["5m"] = 1,
        ["15m"] = 2,
        ["1h"] = 3,
        ["4h"] = 4
    };

    public Timeframe PrimaryTimeframeEnum =>
        TimeframeExtensions.TryParseConfigValue(PrimaryTimeframe, out var timeframe)
            ? timeframe
            : Timeframe.FifteenMinutes;
}
