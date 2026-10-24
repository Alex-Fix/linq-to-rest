using LinqToRest.Modeling;
using LinqToRest.Server.Sets;

namespace Catalog.Api;

// What the API exposes. Writes are further limited to editors by the policies.

public sealed class CategorySet : IEntitySetConfiguration<Category>
{
    public void Configure(EntitySetBuilder<Category> builder)
    {
        builder.ToSet("categories").AllowWrites(WriteOperations.All);
        builder.Property(c => c.Name).IsSortable().IsRequired().HasMaxLength(50);
        builder.Property(c => c.ParentId);

        // Computed values read the entity directly, past the product policy, so this counts active products only.
        builder.Computed("productCount", c => c.Products.Count(p => p.Status == ProductStatus.Active));
        builder.HasOne(c => c.Parent).WithForeignKey(c => c.ParentId);
        builder.HasMany(c => c.Children);
        builder.HasMany(c => c.Products);
    }
}

public sealed class BrandSet : IEntitySetConfiguration<Brand>
{
    public void Configure(EntitySetBuilder<Brand> builder)
    {
        // Brands can be added and renamed, never deleted.
        builder.ToSet("brands").AllowWrites(WriteOperations.Insert | WriteOperations.Update);
        builder.Property(b => b.Name).IsSortable().IsRequired().HasMaxLength(50).IsGroupable();
        builder.Computed("productCount", b => b.Products.Count(p => p.Status == ProductStatus.Active));
        builder.HasMany(b => b.Products);
    }
}

public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").AllowWrites(WriteOperations.All);
        // Built-in rules: exported in model.json, so both front ends check them before saving.
        builder.Property(p => p.Sku).IsRequired().HasPattern("^[A-Z0-9-]{3,20}$");
        builder.Property(p => p.Name).IsSortable().IsRequired().HasMaxLength(100);
        builder.Property(p => p.Description).IsNotFilterable().HasMaxLength(2000);
        builder.Property(p => p.Price).IsSortable().HasRange(0.01m, 100_000m).IsAggregatable();
        builder.Property(p => p.Status).IsGroupable();
        builder.Property(p => p.Stock).IsSortable().HasMin(0).IsAggregatable();
        // Facets group by these; the summary aggregates price and stock.
        builder.Property(p => p.CategoryId).IsGroupable();
        builder.Property(p => p.BrandId).IsGroupable();
        builder.Property(p => p.CreatedAt).IsReadOnly().IsSortable();
        builder.HasConcurrencyToken(p => p.Version);
        builder.Computed("inStock", p => p.Stock > 0);
        builder.Computed("tags", p => p.Tags.OrderBy(t => t.Value).Select(t => t.Value).ToList());
        builder.Computed("imageCount", p => p.Images.Count);
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasOne(p => p.Brand).WithForeignKey(p => p.BrandId);
        builder.HasMany(p => p.Images);
        builder.HasPagination(p => p.DefaultPageSize(24).MaxPageSize(96).MaxSkip(1000));

        // Server-only rules: a price in whole cents, and a SKU no other product has (instead of a 409 from the index).
        builder.Must(p => p.Price, price => decimal.Round(price, 2) == price, "too_precise");
        builder.ValidateWith<UniqueSkuValidator>();
    }
}

public sealed class ProductImageSet : IEntitySetConfiguration<ProductImage>
{
    public void Configure(EntitySetBuilder<ProductImage> builder)
    {
        builder.ToSet("productImages").AllowWrites(WriteOperations.All);
        builder.Property(i => i.ProductId);
        builder.Property(i => i.Url).IsRequired().HasMaxLength(500).HasPattern("^https://");
        builder.Property(i => i.SortOrder).IsSortable();

        // The navigation is what lets a batch point a new image at a product inserted in the same batch (refs).
        builder.HasOne(i => i.Product).WithForeignKey(i => i.ProductId);
    }
}
