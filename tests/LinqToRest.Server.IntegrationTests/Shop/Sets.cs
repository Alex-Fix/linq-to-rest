using LinqToRest.Modeling;
using LinqToRest.Server.Sets;

namespace LinqToRest.Server.IntegrationTests.Shop;

public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").AllowWrites(WriteOperations.All);
        builder.Property(p => p.Name).HasMaxLength(100);
        builder.Property(p => p.Description);
        builder.Property(p => p.Price).IsSortable().HasMin(0m).IsAggregatable();
        builder.Property(p => p.Status).IsSortable().IsGroupable();
        builder.HasConcurrencyToken(p => p.Version);
        builder.Property(p => p.CreatedAt).IsSortable();
        builder.Property(p => p.ReleasedOn).IsSortable();
        builder.Property(p => p.Rating).IsNotFilterable().IsAggregatable();
        builder.Property(p => p.IsFeatured);
        builder.Property(p => p.CategoryId).IsGroupable();
        builder.Computed("tags", p => p.Tags.OrderBy(t => t.Value).Select(t => t.Value).ToList());
        builder.Computed("categoryName", p => p.Category != null ? p.Category.Name : null);
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasMany(p => p.Lines);
        builder.HasPagination(p => p.DefaultPageSize(10).MaxPageSize(20).MaxSkip(100));
    }
}

public sealed class CategorySet : IEntitySetConfiguration<Category>
{
    public void Configure(EntitySetBuilder<Category> builder)
    {
        builder.ToSet("categories").AllowWrites(WriteOperations.Insert | WriteOperations.Update);
        builder.Property(c => c.Name).IsSortable().IsGroupable();
        builder.HasMany(c => c.Products);
        builder.HasPagination(p => p.Modes(PaginationModes.Cursor));
    }
}

public sealed class OrderLineSet : IEntitySetConfiguration<OrderLine>
{
    public void Configure(EntitySetBuilder<OrderLine> builder)
    {
        builder.ToSet("orderLines");
        builder.Property(l => l.ProductId);
        builder.Property(l => l.Sku);
        builder.Property(l => l.Quantity);
        builder.Property(l => l.UnitPrice);
    }
}
