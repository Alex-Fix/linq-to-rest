using Catalog.Client;

namespace Catalog.Blazor;

// Projections: each query asks the server for exactly these members.

public sealed record CategoryNode(int Id, string Name, int? ParentId, int ProductCount)
{
    /// <summary>The ids of a category and every category below it.</summary>
    public static List<int> Descendants(IReadOnlyList<CategoryNode> categories, int id) =>
        [id, .. categories.Where(category => category.ParentId == id).SelectMany(child => Descendants(categories, child.Id))];
}

public sealed record BrandRow(int Id, string Name, int ProductCount);

public sealed record ProductCard(int Id, string Name, decimal Price, bool InStock, ProductStatus Status, List<string> Images);
