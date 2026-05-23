using System.ComponentModel;
using System.Text.Json.Serialization;
using EShop.Data;
using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace EShop.McpServer.Tools;

[McpServerToolType]
public static class OrderTools
{
    private const int MaxPageSize = 200;

    [McpServerTool(Name = "GetOrdersByDateRange")]
    [Description("Return a paged list of orders inside a date range, optionally filtered by status. Newest first.")]
    public static async Task<OrderPage> GetOrdersByDateRange(
        EcomDbContext db,
        [Description("Start date inclusive (UTC). Defaults to 29 days before 'to'.")] DateTime? from = null,
        [Description("End date inclusive (UTC). Defaults to today.")] DateTime? to = null,
        [Description("Optional order status filter (Pending, Paid, Shipped, Delivered, Cancelled, Refunded).")] OrderStatus? status = null,
        [Description("1-based page number. Defaults to 1.")] int page = 1,
        [Description("Page size, 1-200. Defaults to 50.")] int pageSize = 50,
        CancellationToken ct = default)
    {
        var (start, endExclusive) = DateRange.Normalize(from, to);
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var query = db.Orders.AsNoTracking()
            .Where(o => o.OrderDate >= start && o.OrderDate < endExclusive);
        if (status.HasValue)
        {
            query = query.Where(o => o.Status == status.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrderRow(
                o.Id,
                o.OrderDate,
                o.Status.ToString(),
                o.TotalAmount,
                o.Customer!.Name,
                o.Zone!.Name,
                o.Zone.Region))
            .ToListAsync(ct);

        return new OrderPage(total, page, pageSize, items);
    }
}

[Description("A page of order rows, sorted newest-first.")]
public sealed record OrderPage(
    [property: Description("Total number of orders matching the filter (across all pages).")]
    [property: JsonPropertyName("totalCount")]
    int TotalCount,

    [property: Description("1-based page number actually returned.")]
    [property: JsonPropertyName("page")]
    int Page,

    [property: Description("Number of rows per page actually returned.")]
    [property: JsonPropertyName("pageSize")]
    int PageSize,

    [property: Description("Order rows on this page.")]
    [property: JsonPropertyName("items")]
    IReadOnlyList<OrderRow> Items);

[Description("One order row, flattened with customer and zone names for display.")]
public sealed record OrderRow(
    [property: Description("Order primary key.")]
    [property: JsonPropertyName("orderId")]
    int OrderId,

    [property: Description("Order timestamp (UTC).")]
    [property: JsonPropertyName("orderDate")]
    DateTime OrderDate,

    [property: Description("Status as string (Pending, Paid, Shipped, Delivered, Cancelled, Refunded).")]
    [property: JsonPropertyName("status")]
    string Status,

    [property: Description("Order total in store currency.")]
    [property: JsonPropertyName("totalAmount")]
    decimal TotalAmount,

    [property: Description("Customer display name.")]
    [property: JsonPropertyName("customerName")]
    string CustomerName,

    [property: Description("Zone name the order shipped to / belongs to.")]
    [property: JsonPropertyName("zoneName")]
    string ZoneName,

    [property: Description("Region containing the zone.")]
    [property: JsonPropertyName("region")]
    string Region);
