using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using EShop.Data;
using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Resources;

[McpServerResourceType]
public static class DailySalesResource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [McpServerResource(UriTemplate = "reports://daily-sales/{date}", Name = "DailySalesReport", MimeType = "application/json")]
    [Description("Pre-shaped daily sales snapshot for the given UTC date: total revenue, order count, average order value, and the top 5 zones by revenue. Excludes Cancelled and Refunded orders. {date} must be yyyy-MM-dd.")]
    public static async Task<string> GetDailySales(
        EcomDbContext db,
        [Description("UTC date in yyyy-MM-dd format, e.g. 2026-05-23.")] string date,
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new ArgumentException($"Invalid date '{date}'; expected yyyy-MM-dd.", nameof(date));
        }

        var start = parsed.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endExclusive = start.AddDays(1);

        var totals = await db.Orders.AsNoTracking()
            .Where(o => o.OrderDate >= start && o.OrderDate < endExclusive
                     && o.Status != OrderStatus.Cancelled
                     && o.Status != OrderStatus.Refunded)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Revenue = g.Sum(o => o.TotalAmount),
                Orders = g.Count(),
            })
            .FirstOrDefaultAsync(ct);

        var topZones = await db.OrderItems.AsNoTracking()
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
                ZoneId = g.Key.ZoneId,
                ZoneName = g.Key.ZoneName,
                Region = g.Key.Region,
                Revenue = g.Sum(oi => oi.UnitPrice * oi.Quantity),
            })
            .OrderByDescending(z => z.Revenue)
            .Take(5)
            .ToListAsync(ct);

        var revenue = totals?.Revenue ?? 0m;
        var orders = totals?.Orders ?? 0;
        var aov = orders == 0 ? 0m : Math.Round(revenue / orders, 2);

        var payload = new
        {
            Date = date,
            Revenue = revenue,
            Orders = orders,
            AverageOrderValue = aov,
            TopZones = topZones,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
