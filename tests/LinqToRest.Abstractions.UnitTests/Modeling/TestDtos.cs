using LinqToRest.Modeling;

namespace LinqToRest.Abstractions.UnitTests.Modeling;

public enum ProductStatus
{
    Draft,
    Active,
}

public sealed class ProductDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public byte[]? Version { get; set; }
    public int CategoryId { get; set; }
    public CategoryDto? Category { get; set; }
    public List<OrderLineDto> Lines { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string CategoryName { get; set; } = "";
}

public sealed class CategoryDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class OrderLineDto : IDto
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public int Quantity { get; set; }
}

public sealed class CategoryConfiguration : IDtoTypeConfiguration<CategoryDto>
{
    public void Configure(DtoTypeBuilder<CategoryDto> builder) => builder.ToSet("categories").HasKey(d => d.Id);
}
