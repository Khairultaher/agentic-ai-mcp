using EShop.Data;
using EShop.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace EShop.McpServer.Tests;

public sealed class EcomDatabaseFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await EcomSeeder.SeedAsync(db);
    }

    public EcomDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<EcomDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        return new EcomDbContext(options);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
