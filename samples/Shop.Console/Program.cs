using LinqToRest.Client;
using Microsoft.Extensions.DependencyInjection;
using Shop.Client;

// Start samples/Shop.Api first (dotnet run --project samples/Shop.Api --urls http://localhost:5080).
var api = new Uri(args.Length > 0 ? args[0] : "http://localhost:5080/api/");
var services = new ServiceCollection();
// ShopContext and its classes are generated from Shop.Api's model.json (see Shop.Client).
services.AddShopContext(http =>
{
    http.BaseAddress = api;
    http.DefaultRequestHeaders.Add("X-Api-Key", "dev-acme-key");
});
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var shop = scope.ServiceProvider.GetRequiredService<ShopContext>();

// LINQ runs on the server: filter, sort, project.
var cheapGames = await shop.Products
    .Where(p => p.Category!.Name == "Games" && p.Price < 25)
    .OrderBy(p => p.Price)
    .Select(p => new { p.Name, p.Price, Label = p.Name + " (" + p.CategoryName + ")" })
    .Take(10)
    .ToListAsync();
foreach (var game in cheapGames)
{
    Console.WriteLine($"{game.Label}: {game.Price}");
}

// Keyset pagination.
var page = await shop.Products.OrderBy(p => p.Name).Take(2).ToCursorPageAsync(includeTotalCount: true);
Console.WriteLine($"Page 1 of {page.TotalCount} products: {string.Join(", ", page.Items.Select(p => p.Name))}");
if (page.NextCursor is { } cursor)
{
    var next = await shop.Products.OrderBy(p => p.Name).After(cursor).Take(2).ToCursorPageAsync();
    Console.WriteLine($"Page 2: {string.Join(", ", next.Items.Select(p => p.Name))}");
}

// Unit of work: change, add, save atomically.
var chess = await shop.Products.FirstAsync(p => p.Name == "Chess");
chess.Price += 1;
var puzzles = new CategoryDto { Name = "Puzzles" };
shop.Categories.Add(puzzles);
shop.Products.Add(new ProductDto { Name = $"Cube {DateTime.UtcNow:HHmmss}", Price = 9.5m, Status = ProductStatus.Active, Category = puzzles });
var sent = await shop.SaveChangesAsync();
Console.WriteLine($"Saved {sent} changes; Chess now costs {chess.Price}; Puzzles got id {puzzles.Id}.");
Console.WriteLine($"Products visible to this tenant: {await shop.Products.CountAsync()}");
