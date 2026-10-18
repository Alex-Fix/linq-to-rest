using Microsoft.EntityFrameworkCore;

namespace RestToLinq.Server.IntegrationTests.Shop;

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // SQLite has no rowversion, so the app rotates the concurrency token itself.
        foreach (var entry in ChangeTracker.Entries<Product>().Where(entry => entry.State == EntityState.Modified))
        {
            entry.Entity.Version = Guid.NewGuid();
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Version).IsConcurrencyToken();

            // SQLite stores decimal as text, which it can't compare, sort or sum; the double conversion keeps those
            // operations in SQL for the tests.
            product.Property(p => p.Price).HasConversion<double>();
            product.HasMany(p => p.Tags).WithOne().HasForeignKey(t => t.ProductId);
            product.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.ProductId);
        });
        modelBuilder.Entity<OrderLine>().Property(l => l.UnitPrice).HasConversion<double>();
    }
}
