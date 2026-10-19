namespace LinqToRest.Server.IntegrationTests.Shop;

public static class ShopData
{
    public static void Seed(ShopDbContext db)
    {
        var books = new Category { Id = 1, Name = "Books", TenantId = 1 };
        var games = new Category { Id = 2, Name = "Games", TenantId = 1 };
        var tools = new Category { Id = 3, Name = "Tools", TenantId = 2 };
        db.Categories.AddRange(books, games, tools);
        db.Products.AddRange(
            Product(1, "Chess", 20m, ProductStatus.Active, games, new DateTime(2026, 1, 10, 9, 30, 0, DateTimeKind.Utc), new DateOnly(2025, 12, 1), 4.5, featured: true, ["sale", "classic"],
                [Line(11, "C1", 2, 20m), Line(12, "C2", 1, 18m)], description: "Wooden board"),
            Product(2, "Dune", 12.5m, ProductStatus.Active, books, new DateTime(2026, 2, 1, 14, 0, 0, DateTimeKind.Utc), null, 4.8, featured: false, ["scifi"], [Line(21, "D1", 3, 12.5m)]),
            Product(3, "Go", 30m, ProductStatus.Draft, games, new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 3, 1), 4.0, featured: false, ["classic"], []),
            Product(4, "Hammer", 15m, ProductStatus.Active, tools, new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc), null, 3.2, featured: false, [], [Line(41, "H1", 0, 15m)], tenant: 2),
            Product(5, "Notebook", 3m, ProductStatus.Retired, null, new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc), null, 3.9, featured: false, ["sale"], [], description: "  A5 dotted "),
            Product(6, "Puzzle", 20m, ProductStatus.Active, games, new DateTime(2026, 5, 1, 16, 45, 0, DateTimeKind.Utc), new DateOnly(2026, 4, 15), 4.1, featured: true, [], []));
        db.SaveChanges();
        db.ResetSequences();
    }

    private static Product Product(
        int id, string name, decimal price, ProductStatus status, Category? category, DateTime createdAt, DateOnly? releasedOn, double rating,
        bool featured, string[] tags, List<OrderLine> lines, string? description = null, int tenant = 1) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            Price = price,
            Status = status,
            Version = new Guid(id, 0, 0, new byte[8]),
            CreatedAt = createdAt,
            ReleasedOn = releasedOn,
            Rating = rating,
            IsFeatured = featured,
            TenantId = tenant,
            Category = category,
            Tags = [.. tags.Select((tag, index) => new ProductTag { Id = (id * 10) + index, Value = tag })],
            Lines = lines,
        };

    private static OrderLine Line(int id, string sku, int quantity, decimal unitPrice) =>
        new() { Id = id, Sku = sku, Quantity = quantity, UnitPrice = unitPrice };
}
