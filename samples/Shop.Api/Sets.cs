using LinqToRest.Modeling;
using LinqToRest.Server.Sets;

namespace Shop.Api;

// What the API exposes. Nothing else on the entities (such as TenantId) is reachable by clients.
public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").HasTypeName("ProductDto").AllowWrites(WriteOperations.All);

        // Sorting is opt-in: only indexed columns.
        builder.Property(p => p.Name).IsSortable();
        builder.Property(p => p.Price).IsSortable();
        builder.Property(p => p.Status);
        builder.HasConcurrencyToken(p => p.Version);

        // Server-owned: set when the product is created.
        builder.Property(p => p.CreatedAt).IsReadOnly().IsSortable();
        builder.Property(p => p.CategoryId);

        // Computed on the server.
        builder.Computed("tags", p => p.Tags.Select(t => t.Value).ToList());
        builder.Computed("categoryName", p => p.Category != null ? p.Category.Name : null);

        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasPagination(p => p.DefaultPageSize(20).MaxPageSize(100));
    }
}

public sealed class CategorySet : IEntitySetConfiguration<Category>
{
    public void Configure(EntitySetBuilder<Category> builder)
    {
        // Categories can be added but not changed or removed.
        builder.ToSet("categories").HasTypeName("CategoryDto").AllowWrites(WriteOperations.Insert);
        builder.Property(c => c.Name);
    }
}
