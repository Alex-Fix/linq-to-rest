namespace Catalog.Api;

public enum ProductStatus
{
    Draft,
    Active,
    Discontinued,
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = [];
    public List<Product> Products { get; set; } = [];
}

public sealed class Brand
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Product> Products { get; set; } = [];
}

public sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public int Stock { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid Version { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public int BrandId { get; set; }
    public Brand Brand { get; set; } = null!;
    public List<ProductTag> Tags { get; set; } = [];
    public List<ProductImage> Images { get; set; } = [];

    // Internal columns: never configured in a set, so no client can read, filter, sort or write them.
    public decimal CostPrice { get; set; }
    public string? InternalNotes { get; set; }
}

public sealed class ProductTag
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Value { get; set; } = "";
}

public sealed class ProductImage
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string Url { get; set; } = "";
    public int SortOrder { get; set; }
}
