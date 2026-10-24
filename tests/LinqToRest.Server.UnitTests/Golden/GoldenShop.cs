using LinqToRest.Modeling;
using LinqToRest.Server.Sets;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.UnitTests.Golden;

// The sets behind tests/golden/model.json, which the client tests, the generators and the TypeScript client use.

public enum ProductStatus
{
    Draft,
    Active,
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public Guid Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateOnly? ReleasedOn { get; set; }
    public int TenantId { get; set; }
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class Tag
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Value { get; set; } = "";
}

public sealed class GoldenContext(DbContextOptions<GoldenContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Product>().Property(p => p.Version).IsConcurrencyToken();
}

public sealed class GoldenProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").HasTypeName("ProductDto").AllowWrites(WriteOperations.All);
        builder.Property(p => p.Name).HasMaxLength(100);
        builder.Property(p => p.Description);
        builder.Property(p => p.Price).IsSortable().HasMin(0m).IsAggregatable();
        builder.Property(p => p.Status).IsGroupable();
        builder.HasConcurrencyToken(p => p.Version);
        builder.Property(p => p.CreatedAt).IsReadOnly();
        builder.Property(p => p.ReleasedOn);
        builder.Property(p => p.CategoryId).IsGroupable();
        builder.Computed("tags", p => p.Tags.OrderBy(t => t.Value).Select(t => t.Value).ToList());
        builder.Computed("categoryName", p => p.Category != null ? p.Category.Name : null);
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasMany(p => p.Lines);
    }
}

public sealed class GoldenCategorySet : IEntitySetConfiguration<Category>
{
    public void Configure(EntitySetBuilder<Category> builder)
    {
        builder.ToSet("categories").HasTypeName("CategoryDto").AllowWrites(WriteOperations.All);
        builder.Property(c => c.Name).IsGroupable();
        builder.HasPagination(p => p.Modes(PaginationModes.Cursor).DefaultPageSize(2));
    }
}

public sealed class GoldenOrderLineSet : IEntitySetConfiguration<OrderLine>
{
    public void Configure(EntitySetBuilder<OrderLine> builder)
    {
        builder.ToSet("orderLines").HasTypeName("OrderLineDto").AllowWrites(WriteOperations.All);
        builder.Property(l => l.Sku);
        builder.Property(l => l.Quantity).IsAggregatable();
        builder.Property(l => l.UnitPrice).IsAggregatable();
        builder.HasPagination(p => p.Modes(PaginationModes.Offset));
    }
}
