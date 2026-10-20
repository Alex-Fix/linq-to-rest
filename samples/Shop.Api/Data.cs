using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Shop.Api;

public enum ProductStatus
{
    Draft,
    Active,
    Retired,
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string TenantId { get; set; } = "";
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public Guid Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public string TenantId { get; set; } = "";
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public List<ProductTag> Tags { get; set; } = [];
}

public sealed class ProductTag
{
    public int Id { get; set; }
    public string Value { get; set; } = "";
}

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // SQLite has no rowversion: rotate the concurrency token on every change.
        foreach (var entry in ChangeTracker.Entries<Product>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Version = Guid.NewGuid();
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // PostgreSQL stores DateTime as timestamptz, which takes UTC instants: keep every value UTC and treat offset-less
    // values (Kind Unspecified) as UTC. EF Core applies this to query parameters compared with the column too.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Version).IsConcurrencyToken();
            product.HasIndex(p => p.Price);
            product.HasIndex(p => p.Name);
            product.HasIndex(p => p.CreatedAt);
        });

        if (Database.IsSqlite())
        {
            // SQLite stores decimal as text, which it can't compare or sort; PostgreSQL keeps native numeric.
            modelBuilder.Entity<Product>().Property(p => p.Price).HasConversion<double>();
        }
    }

    public async Task SeedAsync()
    {
        if (await Categories.AnyAsync())
        {
            return;
        }

        var games = new Category { Name = "Games", TenantId = "acme" };
        var books = new Category { Name = "Books", TenantId = "acme" };
        Products.AddRange(
            new Product { Name = "Chess", CreatedAt = new DateTime(2026, 1, 10, 9, 30, 0, DateTimeKind.Utc), Price = 20m, Status = ProductStatus.Active, TenantId = "acme", Category = games, Tags = [new() { Value = "classic" }] },
            new Product { Name = "Go", CreatedAt = new DateTime(2026, 2, 1, 14, 0, 0, DateTimeKind.Utc), Price = 30m, Status = ProductStatus.Active, TenantId = "acme", Category = games },
            new Product { Name = "Dune", CreatedAt = new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc), Price = 12.5m, Status = ProductStatus.Active, TenantId = "acme", Category = books, Tags = [new() { Value = "scifi" }] },
            new Product { Name = "Hidden", CreatedAt = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc), Price = 1m, Status = ProductStatus.Draft, TenantId = "other" });
        await SaveChangesAsync();
    }
}

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime(),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
