namespace EShop.McpServer.Tools;

internal static class DateRange
{
    private const int DefaultWindowDays = 30;

    public static (DateTime From, DateTime ToExclusive) Normalize(DateTime? from, DateTime? to)
    {
        var endInclusive = (to ?? DateTime.UtcNow).Date;
        var start = (from ?? endInclusive.AddDays(-(DefaultWindowDays - 1))).Date;
        if (start > endInclusive)
        {
            throw new ArgumentException($"'from' ({start:yyyy-MM-dd}) must be on or before 'to' ({endInclusive:yyyy-MM-dd}).");
        }
        return (start, endInclusive.AddDays(1));
    }

    public static DateTime BucketStart(DateTime date, TimeBucket bucket) => bucket switch
    {
        TimeBucket.Day   => date.Date,
        TimeBucket.Week  => StartOfIsoWeek(date),
        TimeBucket.Month => new DateTime(date.Year, date.Month, 1),
        _ => date.Date,
    };

    private static DateTime StartOfIsoWeek(DateTime date)
    {
        var diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.Date.AddDays(-diff);
    }
}
