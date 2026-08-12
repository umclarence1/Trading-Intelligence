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
