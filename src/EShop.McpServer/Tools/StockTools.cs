using System.ComponentModel;
using System.Text.Json.Serialization;
using EShop.Data;
using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Tools;

[McpServerToolType]
public static class StockTools
{
    private const int UsageWindowDays = 30;

    [McpServerTool(Name = "GetStockLevels")]
    [Description("Return current on-hand stock, trailing 30-day average daily usage, and days-of-cover for every product. Optionally filter to items at or below a stock threshold. Sorted by days-of-cover ascending.")]
    public static async Task<IReadOnlyList<StockRow>> GetStockLevels(
        EcomDbContext db,
        [Description("If set, return only products whose on-hand quantity is at or below this threshold.")] int? belowThreshold = null,
        CancellationToken ct = default)
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
            .Select(p => new
            {
                p.Id,
                p.Name,
                CategoryName = p.Category!.Name,
                p.StockQty,
            })
            .ToListAsync(ct);

        var rows = products.Select(p =>
        {
            var qty = usage.GetValueOrDefault(p.Id, 0);
            var avgDaily = Math.Round(qty / (decimal)UsageWindowDays, 2);
            decimal? daysOfCover = avgDaily <= 0m
                ? null
                : Math.Round(p.StockQty / avgDaily, 1);
            return new StockRow(p.Id, p.Name, p.CategoryName, p.StockQty, avgDaily, daysOfCover);
        });

        if (belowThreshold.HasValue)
        {
            rows = rows.Where(r => r.OnHand <= belowThreshold.Value);
        }

        return rows
            .OrderBy(r => r.DaysOfCover ?? decimal.MaxValue)
            .ThenBy(r => r.OnHand)
            .ToList();
    }
}

[Description("Current stock level and trailing-window cover for a single product.")]
public sealed record StockRow(
    [property: Description("Product primary key.")]
    [property: JsonPropertyName("productId")]
    int ProductId,

    [property: Description("Product display name.")]
    [property: JsonPropertyName("productName")]
    string ProductName,

    [property: Description("Category the product belongs to.")]
    [property: JsonPropertyName("categoryName")]
    string CategoryName,

    [property: Description("Current on-hand quantity.")]
    [property: JsonPropertyName("onHand")]
    int OnHand,

    [property: Description("Average units shipped per day over the trailing 30 days (excludes cancelled/refunded).")]
    [property: JsonPropertyName("averageDailyUsage")]
    decimal AverageDailyUsage,

    [property: Description("Days of cover = onHand / averageDailyUsage. Null when usage is zero.")]
    [property: JsonPropertyName("daysOfCover")]
    decimal? DaysOfCover);
