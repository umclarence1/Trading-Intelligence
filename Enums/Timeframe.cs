namespace TradingAutomationHub.Enums;

public enum Timeframe
{
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    OneHour,
    FourHours,
    OneDay
}

public static class TimeframeExtensions
{
    public static TimeSpan Duration(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.OneMinute => TimeSpan.FromMinutes(1),
        Timeframe.FiveMinutes => TimeSpan.FromMinutes(5),
        Timeframe.FifteenMinutes => TimeSpan.FromMinutes(15),
        Timeframe.OneHour => TimeSpan.FromHours(1),
        Timeframe.FourHours => TimeSpan.FromHours(4),
        Timeframe.OneDay => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
    };

    public static bool TryParseApiValue(string value, out Timeframe timeframe)
    {
        timeframe = value.Trim().ToLowerInvariant() switch
        {
            "5m" => Timeframe.FiveMinutes,
            "15m" => Timeframe.FifteenMinutes,
            "1h" => Timeframe.OneHour,
            "4h" => Timeframe.FourHours,
            _ => (Timeframe)(-1)
        };
        return Enum.IsDefined(timeframe);
    }

    public static bool TryParseConfigValue(string value, out Timeframe timeframe)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            timeframe = (Timeframe)(-1);
            return false;
        }

        timeframe = value.Trim().ToLowerInvariant() switch
        {
            "5m" => Timeframe.FiveMinutes,
            "15m" => Timeframe.FifteenMinutes,
            "1h" => Timeframe.OneHour,
            "4h" => Timeframe.FourHours,
            "1d" => Timeframe.OneDay,
            _ => (Timeframe)(-1)
        };
        return Enum.IsDefined(timeframe);
    }
}
