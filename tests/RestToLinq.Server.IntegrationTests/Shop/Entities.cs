namespace RestToLinq.Server.IntegrationTests.Shop;

public enum ProductStatus
{
    Draft,
    Active,
    Retired,
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int TenantId { get; set; }
    public List<Product> Products { get; set; } = [];
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public Guid Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateOnly? ReleasedOn { get; set; }
    public double Rating { get; set; }
    public bool IsFeatured { get; set; }
    public int TenantId { get; set; }
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public List<ProductTag> Tags { get; set; } = [];
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class ProductTag
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Value { get; set; } = "";
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
