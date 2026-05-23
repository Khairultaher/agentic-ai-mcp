using System.ComponentModel;

namespace EShop.McpServer.Tools;

[Description("Time bucket granularity for grouping a date range.")]
public enum TimeBucket
{
    Day = 0,
    Week = 1,
    Month = 2,
}
