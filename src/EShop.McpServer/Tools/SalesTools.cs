using System.ComponentModel;
using System.Text.Json.Serialization;
using EShop.Data;
using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Tools;

[McpServerToolType]
public static class SalesTools
{
    [McpServerTool(Name = "GetSalesSummary")]
    [Description("Aggregate gross revenue, order count, and average order value across a date range, bucketed by day, week, or month. Excludes Cancelled and Refunded orders.")]
    public static async Task<IReadOnlyList<SalesBucket>> GetSalesSummary(
        EcomDbContext db,
        [Description("Start date inclusive (UTC). Defaults to 29 days before 'to'.")] DateTime? from = null,
        [Description("End date inclusive (UTC). Defaults to today.")] DateTime? to = null,
        [Description("Bucket granularity: Day, Week (ISO Mon-Sun), or Month.")] TimeBucket groupBy = TimeBucket.Day,
        CancellationToken ct = default)
    {
        var (start, endExclusive) = DateRange.Normalize(from, to);

        var daily = await db.Orders.AsNoTracking()
            .Where(o => o.OrderDate >= start && o.OrderDate < endExclusive
                     && o.Status != OrderStatus.Cancelled
                     && o.Status != OrderStatus.Refunded)
            .GroupBy(o => o.OrderDate.Date)
            .Select(g => new
            {
                Date = g.Key,
                Revenue = g.Sum(o => o.TotalAmount),
                Orders = g.Count(),
            })
            .ToListAsync(ct);

        return daily
            .GroupBy(r => DateRange.BucketStart(r.Date, groupBy))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var revenue = g.Sum(x => x.Revenue);
                var orders = g.Sum(x => x.Orders);
                var aov = orders == 0 ? 0m : Math.Round(revenue / orders, 2);
                return new SalesBucket(g.Key, revenue, orders, aov);
            })
            .ToList();
    }
}

[Description("One bucket of aggregated sales activity.")]
public sealed record SalesBucket(
    [property: Description("Inclusive start of the bucket (UTC midnight).")]
    [property: JsonPropertyName("bucketStart")]
    DateTime BucketStart,

    [property: Description("Sum of TotalAmount for orders in this bucket.")]
    [property: JsonPropertyName("revenue")]
    decimal Revenue,

    [property: Description("Number of orders in this bucket.")]
    [property: JsonPropertyName("orderCount")]
    int OrderCount,

    [property: Description("Average order value = revenue / orderCount.")]
    [property: JsonPropertyName("averageOrderValue")]
    decimal AverageOrderValue);
