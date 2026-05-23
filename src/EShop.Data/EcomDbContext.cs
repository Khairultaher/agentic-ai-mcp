using EShop.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EShop.Data;

public class EcomDbContext(DbContextOptions<EcomDbContext> options) : DbContext(options)
{
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Zone>(b =>
        {
            b.Property(z => z.Name).HasMaxLength(100).IsRequired();
            b.Property(z => z.Region).HasMaxLength(100).IsRequired();
            b.HasIndex(z => z.Name).IsUnique();
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(200).IsRequired();
            b.Property(c => c.Email).HasMaxLength(256).IsRequired();
            b.HasIndex(c => c.Email).IsUnique();
            b.HasOne(c => c.Zone)
                .WithMany(z => z.Customers)
                .HasForeignKey(c => c.ZoneId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.Property(p => p.Price).HasPrecision(12, 2);
            b.Property(p => p.Cost).HasPrecision(12, 2);
            b.HasOne(p => p.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.Property(o => o.TotalAmount).HasPrecision(18, 2);
            b.Property(o => o.Status).HasConversion<int>();
            b.HasIndex(o => o.OrderDate);
            b.HasOne(o => o.Customer)
                .WithMany(c => c.Orders)
                .HasForeignKey(o => o.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne(o => o.Zone)
                .WithMany(z => z.Orders)
                .HasForeignKey(o => o.ZoneId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.Property(i => i.UnitPrice).HasPrecision(12, 2);
            b.Property(i => i.UnitCost).HasPrecision(12, 2);
            b.HasOne(i => i.Order)
                .WithMany(o => o.Items)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Product)
                .WithMany(p => p.OrderItems)
                .HasForeignKey(i => i.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockMovement>(b =>
        {
            b.Property(m => m.MovementType).HasConversion<int>();
            b.HasIndex(m => m.MovedOn);
            b.HasOne(m => m.Product)
                .WithMany(p => p.StockMovements)
                .HasForeignKey(m => m.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
