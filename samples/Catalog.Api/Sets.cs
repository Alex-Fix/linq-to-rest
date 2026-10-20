using LinqToRest.Modeling;
using LinqToRest.Server.Sets;

namespace Catalog.Api;

// What the API exposes. Writes are further limited to editors by the policies.

public sealed class CategorySet : IEntitySetConfiguration<Category>
{
    public void Configure(EntitySetBuilder<Category> builder)
    {
        builder.ToSet("categories").AllowWrites(WriteOperations.All);
        builder.Property(c => c.Name).IsSortable();
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
        builder.Property(b => b.Name).IsSortable();
        builder.Computed("productCount", b => b.Products.Count(p => p.Status == ProductStatus.Active));
        builder.HasMany(b => b.Products);
    }
}

public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").AllowWrites(WriteOperations.All);
        builder.Property(p => p.Sku);
        builder.Property(p => p.Name).IsSortable();
        builder.Property(p => p.Description).IsNotFilterable();
        builder.Property(p => p.Price).IsSortable();
        builder.Property(p => p.Status);
        builder.Property(p => p.Stock).IsSortable();
        builder.Property(p => p.CategoryId);
        builder.Property(p => p.BrandId);
        builder.Property(p => p.CreatedAt).IsReadOnly().IsSortable();
        builder.HasConcurrencyToken(p => p.Version);
        builder.Computed("inStock", p => p.Stock > 0);
        builder.Computed("tags", p => p.Tags.OrderBy(t => t.Value).Select(t => t.Value).ToList());
        builder.Computed("imageCount", p => p.Images.Count);
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasOne(p => p.Brand).WithForeignKey(p => p.BrandId);
        builder.HasMany(p => p.Images);
        builder.HasPagination(p => p.DefaultPageSize(24).MaxPageSize(96).MaxSkip(1000));
    }
}

public sealed class ProductImageSet : IEntitySetConfiguration<ProductImage>
{
    public void Configure(EntitySetBuilder<ProductImage> builder)
    {
        builder.ToSet("productImages").AllowWrites(WriteOperations.All);
        builder.Property(i => i.ProductId);
        builder.Property(i => i.Url);
        builder.Property(i => i.SortOrder).IsSortable();

        // The navigation is what lets a batch point a new image at a product inserted in the same batch (refs).
        builder.HasOne(i => i.Product).WithForeignKey(i => i.ProductId);
    }
}
