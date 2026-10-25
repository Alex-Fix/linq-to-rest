using Microsoft.EntityFrameworkCore;

namespace Catalog.Api;

/// <summary>A deterministic catalog: about 200 products over a three-level category tree and 8 brands.</summary>
public static class CatalogSeed
{
    private static readonly string[] TagValues = ["bestseller", "classic", "eco", "family", "gift", "new", "sale", "travel"];
    private static readonly string[] Countries = ["DE", "US", "JP", "UA"];
    private static readonly string[] BrandNames = ["Acme", "Northwind", "Contoso", "Fabrikam", "Tailspin", "Litware", "Adatum", "Proseware"];

    public static async Task SeedAsync(CatalogDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Categories.AnyAsync(cancellationToken))
        {
            return;
        }

        var leaves = new List<Category>();
        Category Node(string name, Category? parent = null, bool leaf = false)
        {
            var category = new Category { Name = name, Parent = parent };
            db.Categories.Add(category);
            if (leaf)
            {
                leaves.Add(category);
            }

            return category;
        }

        var games = Node("Games");
        var board = Node("Board games", games);
        Node("Strategy", board, leaf: true);
        Node("Party", board, leaf: true);
        Node("Video games", games, leaf: true);
        Node("Puzzles", games, leaf: true);
        var books = Node("Books");
        var fiction = Node("Fiction", books);
        Node("Fantasy", fiction, leaf: true);
        Node("Crime", fiction, leaf: true);
        Node("Science", books, leaf: true);
        Node("Comics", books, leaf: true);
        var electronics = Node("Electronics");
        Node("Audio", electronics, leaf: true);
        var computers = Node("Computers", electronics);
        Node("Laptops", computers, leaf: true);
        Node("Accessories", computers, leaf: true);

        // Every other brand publishes a contact; most products have package dimensions (derived from the index, so the
        // random sequence of the other values stays the same).
        Brand[] brands =
        [
            .. BrandNames.Select((name, index) => new Brand
            {
                Name = name,
                Contact = index % 2 == 0 ? new BrandContact { Website = $"https://{name.ToLowerInvariant()}.example", Country = Countries[index % Countries.Length] } : null,
            }),
        ];
        db.Brands.AddRange(brands);

        var random = new Random(20261020);
        var created = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        foreach (var index in Enumerable.Range(1, 200))
        {
            var category = leaves[index % leaves.Count];
            var price = Math.Round((decimal)(random.NextDouble() * 190) + 5, 2);
            var sku = $"SKU-{index:D4}";
            db.Products.Add(new Product
            {
                Sku = sku,
                Name = $"{category.Name} {Adjective(random)} {index}",
                Description = $"A {Adjective(random).ToLowerInvariant()} pick from {category.Name.ToLowerInvariant()}.",
                Price = price,
                CostPrice = Math.Round(price * 0.6m, 2),
                Status = (index % 20) switch { 0 => ProductStatus.Discontinued, 1 or 2 => ProductStatus.Draft, _ => ProductStatus.Active },
                Stock = index % 7 == 0 ? 0 : random.Next(1, 60),
                Dimensions = index % 5 == 0 ? null : new Dimensions(10 + (index % 30), 5 + (index % 20), 2 + (index % 10), Math.Round(0.1 + (index % 25 * 0.2), 1)),
                CreatedAt = created.AddHours(index * 9),
                Version = Guid.NewGuid(),
                Category = category,
                Brand = brands[index % brands.Length],
                InternalNotes = index % 10 == 0 ? "Supplier contract under review." : null,
                Tags = [.. TagValues.Where((_, tag) => (index + tag) % 4 == 0).Select(value => new ProductTag { Value = value })],
                Images = [.. Enumerable.Range(0, index % 4).Select(order => new ProductImage { Url = $"https://picsum.photos/seed/{sku}-{order}/400/300", SortOrder = order })],
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Adjective(Random random) => random.Next(6) switch
    {
        0 => "Classic",
        1 => "Deluxe",
        2 => "Compact",
        3 => "Pocket",
        4 => "Premium",
        _ => "Essential",
    };
}
