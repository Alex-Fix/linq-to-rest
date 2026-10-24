namespace LinqToRest.Server.IntegrationTests.Shop;

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
    public Contact? Contact { get; set; }
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
    public Dimensions Size { get; set; } = new() { Width = 0, Height = 0, Weight = new Weight(0, "kg") };
    public Place? Origin { get; set; }
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
    public Place? Shipping { get; set; }
}

/// <summary>A required complex type with a nested one, configured with init-only members.</summary>
public sealed record Dimensions
{
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required Weight Weight { get; init; }
}

/// <summary>A positional record nested in <see cref="Dimensions"/>, guarding its own values.</summary>
public sealed record Weight(double Amount, string Unit)
{
    public double Amount { get; init; } = Amount >= 0 ? Amount : throw new ArgumentOutOfRangeException(nameof(Amount));
}

/// <summary>An optional complex type on products, and an owned JSON type on order lines.</summary>
public sealed record Place(string Country, string? City);

/// <summary>An optional owned type, table-split into the categories table.</summary>
public sealed class Contact
{
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
}
