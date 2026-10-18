using Microsoft.EntityFrameworkCore;
using Shop.Contracts;

namespace Shop.Api;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Version).IsConcurrencyToken();

            // SQLite stores decimal as text, which it can't compare or sort; real databases don't need this.
            product.Property(p => p.Price).HasConversion<double>();
            product.HasIndex(p => p.Price);
            product.HasIndex(p => p.Name);
        });
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
            new Product { Name = "Chess", Price = 20m, Status = ProductStatus.Active, TenantId = "acme", Category = games, Tags = [new() { Value = "classic" }] },
            new Product { Name = "Go", Price = 30m, Status = ProductStatus.Active, TenantId = "acme", Category = games },
            new Product { Name = "Dune", Price = 12.5m, Status = ProductStatus.Active, TenantId = "acme", Category = books, Tags = [new() { Value = "scifi" }] },
            new Product { Name = "Hidden", Price = 1m, Status = ProductStatus.Draft, TenantId = "other" });
        await SaveChangesAsync();
    }
}
