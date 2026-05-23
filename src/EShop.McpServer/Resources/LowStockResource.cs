using System.ComponentModel;
using System.Text.Json;
using EShop.Data;
using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Resources;

[McpServerResourceType]
public static class LowStockResource
{
    private const int Threshold = 300;
    private const int UsageWindowDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [McpServerResource(UriTemplate = "reports://low-stock", Name = "LowStockReport", MimeType = "application/json")]
    [Description("Snapshot of products at or below the default safety-stock threshold (300 units), with trailing-30-day average daily usage and days-of-cover. Sorted by days-of-cover ascending.")]
    public static async Task<string> GetLowStock(EcomDbContext db, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var since = today.AddDays(-UsageWindowDays);

        var usage = await db.OrderItems.AsNoTracking()
            .Where(oi => oi.Order!.OrderDate >= since
                      && oi.Order.Status != OrderStatus.Cancelled
                      && oi.Order.Status != OrderStatus.Refunded)
            .GroupBy(oi => oi.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(oi => oi.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Qty, ct);

        var products = await db.Products.AsNoTracking()
            .Where(p => p.StockQty <= Threshold)
            .Select(p => new
            {
                p.Id,
                p.Name,
                CategoryName = p.Category!.Name,
                p.StockQty,
            })
            .ToListAsync(ct);

        var items = products
            .Select(p =>
            {
                var qty = usage.GetValueOrDefault(p.Id, 0);
                var avgDaily = Math.Round(qty / (decimal)UsageWindowDays, 2);
                decimal? cover = avgDaily <= 0m ? null : Math.Round(p.StockQty / avgDaily, 1);
                return new
                {
                    ProductId = p.Id,
                    ProductName = p.Name,
                    CategoryName = p.CategoryName,
                    OnHand = p.StockQty,
                    AverageDailyUsage = avgDaily,
                    DaysOfCover = cover,
                };
            })
            .OrderBy(r => r.DaysOfCover ?? decimal.MaxValue)
            .ThenBy(r => r.OnHand)
            .ToList();

        var payload = new
        {
            Threshold,
            GeneratedAt = today,
            Items = items,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
