using System.ComponentModel;
using System.Text.Json.Serialization;
using EShop.Data;
using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Tools;

[McpServerToolType]
public static class ZoneTools
{
    [McpServerTool(Name = "GetProfitByZone")]
    [Description("Revenue, cost, gross profit, and margin percent per zone inside a date range. Excludes Cancelled and Refunded orders. Sorted by gross profit descending.")]
    public static async Task<IReadOnlyList<ZoneProfit>> GetProfitByZone(
        EcomDbContext db,
        [Description("Start date inclusive (UTC). Defaults to 29 days before 'to'.")] DateTime? from = null,
        [Description("End date inclusive (UTC). Defaults to today.")] DateTime? to = null,
        CancellationToken ct = default)
    {
        var (start, endExclusive) = DateRange.Normalize(from, to);

        var rows = await db.OrderItems.AsNoTracking()
            .Where(oi => oi.Order!.OrderDate >= start && oi.Order.OrderDate < endExclusive
                      && oi.Order.Status != OrderStatus.Cancelled
                      && oi.Order.Status != OrderStatus.Refunded)
            .GroupBy(oi => new
            {
                ZoneId = oi.Order!.ZoneId,
                ZoneName = oi.Order.Zone!.Name,
                Region = oi.Order.Zone.Region,
            })
            .Select(g => new
            {
                g.Key.ZoneId,
                g.Key.ZoneName,
                g.Key.Region,
                Revenue = g.Sum(oi => oi.UnitPrice * oi.Quantity),
                Cost = g.Sum(oi => oi.UnitCost * oi.Quantity),
            })
            .ToListAsync(ct);

        return rows
            .Select(r =>
            {
                var profit = r.Revenue - r.Cost;
                var margin = r.Revenue == 0m ? 0m : Math.Round(profit / r.Revenue * 100m, 2);
                return new ZoneProfit(r.ZoneId, r.ZoneName, r.Region, r.Revenue, r.Cost, profit, margin);
            })
            .OrderByDescending(z => z.GrossProfit)
            .ToList();
    }
}

[Description("Profit roll-up for one zone over the requested window.")]
public sealed record ZoneProfit(
    [property: Description("Zone primary key.")]
    [property: JsonPropertyName("zoneId")]
    int ZoneId,

    [property: Description("Zone display name.")]
    [property: JsonPropertyName("zoneName")]
    string ZoneName,

    [property: Description("Region containing the zone.")]
    [property: JsonPropertyName("region")]
    string Region,

    [property: Description("Sum of unitPrice * quantity across all order items in this zone.")]
    [property: JsonPropertyName("revenue")]
    decimal Revenue,

    [property: Description("Sum of unitCost * quantity across all order items in this zone.")]
    [property: JsonPropertyName("cost")]
    decimal Cost,

    [property: Description("revenue - cost.")]
    [property: JsonPropertyName("grossProfit")]
    decimal GrossProfit,

    [property: Description("grossProfit / revenue * 100, rounded to 2dp. Zero when revenue is zero.")]
    [property: JsonPropertyName("marginPercent")]
    decimal MarginPercent);
