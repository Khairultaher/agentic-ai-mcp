using EShop.Data;
using EShop.Data.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EShop.DbInitializer;

internal sealed class DbInitializerHostedService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DbInitializerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EcomDbContext>();

            logger.LogInformation("Applying EF Core migrations to ecom database...");
            await db.Database.MigrateAsync(stoppingToken);

            logger.LogInformation("Running EcomSeeder (idempotent)...");
            var result = await EcomSeeder.SeedAsync(db, stoppingToken);

            if (result.WasSeeded)
            {
                logger.LogInformation(
                    "Seed complete: {Zones} zones, {Categories} categories, {Products} products, {Customers} customers, {Orders} orders, {Items} order items, {Movements} stock movements.",
                    result.ZonesAdded, result.CategoriesAdded, result.ProductsAdded,
                    result.CustomersAdded, result.OrdersAdded, result.OrderItemsAdded,
                    result.StockMovementsAdded);
            }
            else
            {
                logger.LogInformation("Database already seeded; no rows inserted.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
