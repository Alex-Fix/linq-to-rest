using LinqToRest.Modeling;
using LinqToRest.Server.Mapping;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.UnitTests.Mapping;

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long Stock { get; set; }
    public decimal Price { get; private init; }
    public Guid Version { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public List<Line> Lines { get; set; } = [];
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Line
{
    public int Id { get; set; }
    public int Quantity { get; set; }
}

public sealed class ProductDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public Guid Version { get; set; }
    public int CategoryId { get; set; }
    public CategoryDto? Category { get; set; }
    public List<LineDto> Lines { get; set; } = [];
}

public sealed class CategoryDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class LineDto : IDto
{
    public int Id { get; set; }
    public int Quantity { get; set; }
}

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options);

public sealed class OtherContext(DbContextOptions<OtherContext> options) : DbContext(options);

public sealed class ProductMapping : IDtoMapping<ProductDto, Product>
{
    public void Configure(DtoMappingBuilder<ProductDto, Product> builder)
    {
        builder.FromSource<ShopContext>(db => db.Set<Product>().Where(p => p.Id > 0));
        builder.Project(p => new ProductDto { Id = p.Id, Name = p.Name, Label = p.Name + "!", Version = p.Version, CategoryId = p.CategoryId });
        builder.HasOne(d => d.Category, p => p.Category);
        builder.HasMany(d => d.Lines, p => p.Lines);
    }
}

public sealed class CategoryMapping : IDtoMapping<CategoryDto, Category>
{
    public void Configure(DtoMappingBuilder<CategoryDto, Category> builder) =>
        builder.Project(c => new CategoryDto { Id = c.Id, Name = c.Name });
}

public sealed class LineMapping : IDtoMapping<LineDto, Line>
{
    public void Configure(DtoMappingBuilder<LineDto, Line> builder) =>
        builder.Project(l => new LineDto { Id = l.Id, Quantity = l.Quantity });
}

public static class ShopModel
{
    public static DtoModel Create() => new DtoModelBuilder()
        .Dto<ProductDto>(b =>
        {
            b.ToSet("products").HasKey(d => d.Id).HasConcurrencyToken(d => d.Version);
            b.Property(d => d.Label).IsReadOnly();
            b.HasOne(d => d.Category).WithForeignKey(d => d.CategoryId);
        })
        .Dto<CategoryDto>(b => b.ToSet("categories").HasKey(d => d.Id))
        .Dto<LineDto>(b => b.ToSet("lines").HasKey(d => d.Id))
        .Build();
}
