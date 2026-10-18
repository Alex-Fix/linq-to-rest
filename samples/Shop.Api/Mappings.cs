using RestToLinq.Server.Mapping;
using Shop.Contracts;

namespace Shop.Api;

public sealed class ProductMapping : IDtoMapping<ProductDto, Product>
{
    public void Configure(DtoMappingBuilder<ProductDto, Product> builder)
    {
        builder.Project(p => new ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            Price = p.Price,
            Status = p.Status,
            Version = p.Version,
            CategoryId = p.CategoryId,
            Tags = p.Tags.Select(t => t.Value).ToList(),
            CategoryName = p.Category != null ? p.Category.Name : null,
        });
        builder.HasOne(d => d.Category, p => p.Category);
    }
}

public sealed class CategoryMapping : IDtoMapping<CategoryDto, Category>
{
    public void Configure(DtoMappingBuilder<CategoryDto, Category> builder) =>
        builder.Project(c => new CategoryDto { Id = c.Id, Name = c.Name });
}
