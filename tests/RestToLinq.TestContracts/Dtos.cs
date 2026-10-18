using RestToLinq.Modeling;

namespace RestToLinq.TestContracts;

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
    public Guid Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateOnly? ReleasedOn { get; set; }
    public int? CategoryId { get; set; }
    public CategoryDto? Category { get; set; }
    public List<OrderLineDto> Lines { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string? CategoryName { get; set; }
}

public sealed class CategoryDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class OrderLineDto : IDto
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class ProductConfiguration : IDtoTypeConfiguration<ProductDto>
{
    public void Configure(DtoTypeBuilder<ProductDto> builder)
    {
        builder.ToSet("products").HasKey(d => d.Id).HasConcurrencyToken(d => d.Version);
        builder.HasOne(d => d.Category).WithForeignKey(d => d.CategoryId);
        builder.Property(d => d.Price).IsSortable();
        builder.Property(d => d.CreatedAt).IsReadOnly();
        builder.Property(d => d.Tags).IsReadOnly();
        builder.Property(d => d.CategoryName).IsReadOnly();
    }
}

public sealed class CategoryConfiguration : IDtoTypeConfiguration<CategoryDto>
{
    public void Configure(DtoTypeBuilder<CategoryDto> builder) =>
        builder.ToSet("categories").HasKey(d => d.Id).HasPagination(p => p.Modes(PaginationModes.Cursor).DefaultPageSize(2));
}

public sealed class OrderLineConfiguration : IDtoTypeConfiguration<OrderLineDto>
{
    public void Configure(DtoTypeBuilder<OrderLineDto> builder) =>
        builder.ToSet("orderLines").HasKey(d => d.Id).HasPagination(p => p.Modes(PaginationModes.Offset));
}

public static class TestModel
{
    public static DtoModel Create() => new DtoModelBuilder().ApplyConfigurationsFromAssembly(typeof(TestModel).Assembly).Build();
}
