namespace TradingAutomationHub.MarketData;

public sealed class TwelveDataSettings
{
    public const string SectionName = "TwelveData";
    public string ApiKey { get; set; } = string.Empty;
    public int PollingIntervalSeconds { get; set; } = 60;
}
