using System.ComponentModel;
using System.Text.Json.Serialization;
using EShop.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Tools;

[McpServerToolType]
public static class CustomerTools
{
    [McpServerTool(Name = "GetUserGrowthTrend")]
    [Description("Count new customer registrations per bucket across a date range, plus a running cumulative total (including customers registered before the range).")]
    public static async Task<IReadOnlyList<GrowthBucket>> GetUserGrowthTrend(
        EcomDbContext db,
        [Description("Start date inclusive (UTC). Defaults to 29 days before 'to'.")] DateTime? from = null,
        [Description("End date inclusive (UTC). Defaults to today.")] DateTime? to = null,
        [Description("Bucket granularity: Day, Week (ISO Mon-Sun), or Month.")] TimeBucket granularity = TimeBucket.Day,
        CancellationToken ct = default)
    {
        var (start, endExclusive) = DateRange.Normalize(from, to);

        var priorCount = await db.Customers.AsNoTracking()
            .CountAsync(c => c.RegisteredOn < start, ct);

        var daily = await db.Customers.AsNoTracking()
            .Where(c => c.RegisteredOn >= start && c.RegisteredOn < endExclusive)
            .GroupBy(c => c.RegisteredOn.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var buckets = daily
            .GroupBy(r => DateRange.BucketStart(r.Date, granularity))
            .OrderBy(g => g.Key)
            .Select(g => new { Bucket = g.Key, NewCustomers = g.Sum(x => x.Count) })
            .ToList();

        var cumulative = priorCount;
        var result = new List<GrowthBucket>(buckets.Count);
        foreach (var b in buckets)
        {
            cumulative += b.NewCustomers;
            result.Add(new GrowthBucket(b.Bucket, b.NewCustomers, cumulative));
        }
        return result;
    }
}

[Description("New customer count and running total for one bucket.")]
public sealed record GrowthBucket(
    [property: Description("Inclusive start of the bucket (UTC midnight).")]
    [property: JsonPropertyName("bucketStart")]
    DateTime BucketStart,

    [property: Description("Customers whose RegisteredOn falls inside this bucket.")]
    [property: JsonPropertyName("newCustomers")]
    int NewCustomers,

    [property: Description("Running total customers up to and including this bucket.")]
    [property: JsonPropertyName("cumulativeCustomers")]
    int CumulativeCustomers);
