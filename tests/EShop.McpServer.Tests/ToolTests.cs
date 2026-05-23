using EShop.Data.Entities;
using EShop.McpServer.Tools;

namespace EShop.McpServer.Tests;

public sealed class ToolTests(EcomDatabaseFixture fixture) : IClassFixture<EcomDatabaseFixture>
{
    private readonly EcomDatabaseFixture _fx = fixture;

    [Fact]
    public async Task GetSalesSummary_returns_non_empty_buckets_with_revenue()
    {
        await using var db = _fx.CreateContext();

        var result = await SalesTools.GetSalesSummary(db, from: null, to: null, groupBy: TimeBucket.Day);

        Assert.NotEmpty(result);
        Assert.All(result, bucket =>
        {
            Assert.True(bucket.Revenue > 0m, "Revenue should be positive for seeded window.");
            Assert.True(bucket.OrderCount > 0, "OrderCount should be positive.");
            Assert.True(bucket.AverageOrderValue > 0m, "AOV should be positive.");
            Assert.Equal(bucket.BucketStart.Date, bucket.BucketStart);
        });
        Assert.True(result.Zip(result.Skip(1)).All(p => p.First.BucketStart < p.Second.BucketStart),
            "Buckets must be sorted ascending by BucketStart.");
    }

    [Fact]
    public async Task GetOrdersByDateRange_returns_paged_orders_with_lookups()
    {
        await using var db = _fx.CreateContext();

        var page = await OrderTools.GetOrdersByDateRange(db, from: null, to: null, status: null, page: 1, pageSize: 25);

        Assert.True(page.TotalCount > 0);
        Assert.Equal(1, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.NotEmpty(page.Items);
        Assert.True(page.Items.Count <= 25);
        Assert.All(page.Items, row =>
        {
            Assert.True(row.OrderId > 0);
            Assert.False(string.IsNullOrWhiteSpace(row.CustomerName));
            Assert.False(string.IsNullOrWhiteSpace(row.ZoneName));
            Assert.False(string.IsNullOrWhiteSpace(row.Region));
            Assert.True(row.TotalAmount >= 0m);
            Assert.False(string.IsNullOrWhiteSpace(row.Status));
        });
        Assert.True(page.Items.Zip(page.Items.Skip(1)).All(p => p.First.OrderDate >= p.Second.OrderDate),
            "Items must be sorted by OrderDate descending.");
    }

    [Fact]
    public async Task GetStockLevels_returns_one_row_per_product_with_consistent_cover()
    {
        await using var db = _fx.CreateContext();

        var rows = await StockTools.GetStockLevels(db, belowThreshold: null);

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.True(row.ProductId > 0);
            Assert.False(string.IsNullOrWhiteSpace(row.ProductName));
            Assert.False(string.IsNullOrWhiteSpace(row.CategoryName));
            Assert.True(row.OnHand >= 0);
            Assert.True(row.AverageDailyUsage >= 0m);
            if (row.AverageDailyUsage > 0m)
            {
                Assert.NotNull(row.DaysOfCover);
                Assert.True(row.DaysOfCover > 0m);
            }
            else
            {
                Assert.Null(row.DaysOfCover);
            }
        });
        Assert.True(rows.Count(r => r.AverageDailyUsage > 0m) > 0,
            "Seeded orders should produce usage for at least some products.");
    }

    [Fact]
    public async Task GetUserGrowthTrend_returns_buckets_with_monotonic_cumulative()
    {
        await using var db = _fx.CreateContext();
        var from = DateTime.UtcNow.Date.AddDays(-180);
        var to = DateTime.UtcNow.Date;

        var buckets = await CustomerTools.GetUserGrowthTrend(db, from: from, to: to, granularity: TimeBucket.Week);

        Assert.NotEmpty(buckets);
        Assert.All(buckets, b =>
        {
            Assert.True(b.NewCustomers > 0);
            Assert.True(b.CumulativeCustomers > 0);
            Assert.True(b.BucketStart >= from);
        });
        Assert.True(buckets.Zip(buckets.Skip(1))
            .All(p => p.Second.CumulativeCustomers >= p.First.CumulativeCustomers),
            "Cumulative customer total must never decrease.");
    }

    [Fact]
    public async Task GetProfitByZone_returns_one_row_per_zone_with_valid_margin()
    {
        await using var db = _fx.CreateContext();
        var from = DateTime.UtcNow.Date.AddDays(-60);
        var to = DateTime.UtcNow.Date;

        var rows = await ZoneTools.GetProfitByZone(db, from: from, to: to);

        Assert.NotEmpty(rows);
        Assert.True(rows.Count >= 3, "Seeded data has 8 zones across 3 regions; expect several to show activity.");
        Assert.All(rows, r =>
        {
            Assert.True(r.ZoneId > 0);
            Assert.False(string.IsNullOrWhiteSpace(r.ZoneName));
            Assert.Contains(r.Region, new[] { "North", "Central", "South" });
            Assert.True(r.Revenue > 0m);
            Assert.True(r.Cost > 0m);
            Assert.Equal(r.Revenue - r.Cost, r.GrossProfit);
            Assert.InRange(r.MarginPercent, -100m, 100m);
        });
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.GrossProfit >= p.Second.GrossProfit),
            "Rows must be sorted by GrossProfit descending.");
    }
}
