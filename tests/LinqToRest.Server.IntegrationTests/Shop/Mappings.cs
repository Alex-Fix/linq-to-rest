using LinqToRest.Server.Mapping;

namespace LinqToRest.Server.IntegrationTests.Shop;

public sealed class ProductMapping : IDtoMapping<ProductDto, Product>
{
    public void Configure(DtoMappingBuilder<ProductDto, Product> builder)
    {
        builder.Project(p => new ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Price = p.Price,
            Status = p.Status,
            Version = p.Version,
            CreatedAt = p.CreatedAt,
            ReleasedOn = p.ReleasedOn,
            Rating = p.Rating,
            IsFeatured = p.IsFeatured,
            CategoryId = p.CategoryId,
            Tags = p.Tags.OrderBy(t => t.Value).Select(t => t.Value).ToList(),
            CategoryName = p.Category != null ? p.Category.Name : null,
        });
        builder.HasOne(d => d.Category, p => p.Category);
        builder.HasMany(d => d.Lines, p => p.Lines);
    }
}

public sealed class CategoryMapping : IDtoMapping<CategoryDto, Category>
{
    public void Configure(DtoMappingBuilder<CategoryDto, Category> builder)
    {
        builder.Project(c => new CategoryDto { Id = c.Id, Name = c.Name });
        builder.HasMany(d => d.Products, c => c.Products);
    }
}

public sealed class OrderLineMapping : IDtoMapping<OrderLineDto, OrderLine>
{
    public void Configure(DtoMappingBuilder<OrderLineDto, OrderLine> builder) =>
        builder.Project(l => new OrderLineDto { Id = l.Id, ProductId = l.ProductId, Sku = l.Sku, Quantity = l.Quantity, UnitPrice = l.UnitPrice });
}
