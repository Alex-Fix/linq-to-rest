using RestToLinq;

namespace Shop.Contracts;

public enum ProductStatus
{
    Draft,
    Active,
    Retired,
}

public sealed class ProductDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public Guid Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CategoryId { get; set; }
    public CategoryDto? Category { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? CategoryName { get; set; }
}

public sealed class CategoryDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
