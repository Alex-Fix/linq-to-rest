using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Catalog.Api;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Product> Products => Set<Product>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // SQLite has no rowversion: rotate the concurrency token on every change.
        foreach (var entry in ChangeTracker.Entries<Product>().Where(e => e.State == EntityState.Modified))
        {
            entry.Entity.Version = Guid.NewGuid();
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // timestamptz takes UTC instants: keep values UTC and treat offset-less ones (Kind Unspecified) as UTC.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(category =>
        {
            category.HasOne(c => c.Parent).WithMany(c => c.Children).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
            category.HasMany(c => c.Products).WithOne(p => p.Category).OnDelete(DeleteBehavior.Restrict);
            category.HasIndex(c => c.Name);
        });
        modelBuilder.Entity<Brand>(brand =>
        {
            brand.HasIndex(b => b.Name);
            brand.OwnsOne(b => b.Contact);
        });
        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Version).IsConcurrencyToken();
            product.HasIndex(p => p.Sku).IsUnique();
            product.HasIndex(p => p.Name);
            product.HasIndex(p => p.Price);
            product.HasIndex(p => p.Stock);
            product.HasIndex(p => p.CreatedAt);
            product.ComplexProperty(p => p.Dimensions);
            product.HasMany(p => p.Tags).WithOne().HasForeignKey(t => t.ProductId);
            product.HasMany(p => p.Images).WithOne(i => i.Product).HasForeignKey(i => i.ProductId);
        });

        if (Database.IsSqlite())
        {
            // SQLite stores decimal as text, which it can't compare or sort; PostgreSQL keeps native numeric.
            modelBuilder.Entity<Product>().Property(p => p.Price).HasConversion<double>();
            modelBuilder.Entity<Product>().Property(p => p.CostPrice).HasConversion<double>();
        }
    }
}

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime(),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
