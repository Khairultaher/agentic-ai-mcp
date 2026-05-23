using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EShop.Data;

internal sealed class EcomDbContextFactory : IDesignTimeDbContextFactory<EcomDbContext>
{
    public EcomDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EcomDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ecom_design;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new EcomDbContext(options);
    }
}
