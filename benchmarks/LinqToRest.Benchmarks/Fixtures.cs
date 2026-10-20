using System.Text;
using LinqToRest.Protocol;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.IntegrationTests.Shop;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Benchmarks;

/// <summary>The three query shapes every suite measures.</summary>
public static class Queries
{
    private const string Small = """{"v":1,"filter":{"gt":[{"member":"price"},10]},"page":{"mode":"offset","take":20}}""";

    private const string Medium = """
        {"v":1,
         "filter":{"and":[
           {"any":{"source":{"member":"tags"},"var":"t","body":{"eq":[{"var":"t"},"sale"]}}},
           {"gt":[{"sum":{"source":{"member":"lines"},"var":"l","body":{"mul":[{"member":"l/quantity"},{"member":"l/unitPrice"}]}}},10]},
           {"ne":[{"member":"category/name"},"Tools"]}]},
         "orderBy":[{"member":"price","desc":true}],
         "select":{"new":{
           "id":{"member":"id"},
           "label":{"concat":[{"member":"name"}," (",{"member":"categoryName"},")"]},
           "total":{"sum":{"source":{"member":"lines"},"var":"l","body":{"mul":[{"member":"l/quantity"},{"member":"l/unitPrice"}]}}},
           "skus":{"map":{"source":{"member":"lines"},"var":"l","where":{"gt":[{"member":"l/quantity"},1]},"body":{"member":"l/sku"}}}}},
         "page":{"mode":"offset","take":20},
         "count":true}
        """;

    private static readonly string Large = BuildLarge();

    public static string Get(string shape) => shape switch
    {
        "small" => Small,
        "medium" => Medium,
        _ => Large,
    };

    private static string BuildLarge()
    {
        var ids = string.Join(",", Enumerable.Range(1, 300));
        return """
            {"v":1,
             "filter":{"and":[
               {"in":[{"member":"id"},[IDS]]},
               {"or":[{"contains":[{"member":"name"},"o"]},{"startsWith":[{"toLower":[{"member":"name"}]},"p"]},{"eq":[{"year":[{"member":"createdAt"}]},2026]}]},
               {"any":{"source":{"member":"category/products"},"var":"p","body":{"any":{"source":{"member":"p/lines"},"var":"l","body":{"gt":[{"member":"l/quantity"},0]}}}}},
               {"not":[{"eq":[{"coalesce":[{"member":"description"},""]},"x"]}]}]},
             "orderBy":[{"member":"createdAt"},{"member":"price","desc":true}],
             "include":["category","lines"],
             "page":{"mode":"cursor","take":20},
             "count":true}
            """.Replace("IDS", ids, StringComparison.Ordinal);
    }
}

/// <summary>A LinqToRest server model over a seeded in-memory SQLite shop, without HTTP.</summary>
public sealed class ServerFixture : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;

    public ServerFixture(int products = 500)
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<ShopDbContext>(options => options.UseSqlite(_connection), ServiceLifetime.Transient);
        services.AddLinqToRest(ShopModel.Create(), options => options.UseDbContext<ShopDbContext>().AddMappingsFromAssembly(typeof(ServerFixture).Assembly));
        _services = services.BuildServiceProvider();
        Server = _services.GetRequiredService<ServerModel>();
        using var db = NewDbContext();
        db.Database.EnsureCreated();
        ShopData.Seed(db);
        Seed(db, products);
    }

    internal ServerModel Server { get; }

    public ShopDbContext NewDbContext() => _services.GetRequiredService<ShopDbContext>();

    internal RequestPolicies Policies() => new(new PolicyContext(new DefaultHttpContext { RequestServices = _services }));

    internal SetMapping Products => Server.FindSet("products")!;

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    internal static QueryRequest Parse(string json) => QueryJson.Read(Encoding.UTF8.GetBytes(json), new QueryLimits());

    private static void Seed(ShopDbContext db, int count)
    {
        var categories = db.Categories.ToList();
        for (var index = 0; index < count; index++)
        {
            db.Products.Add(new Product
            {
                Name = $"Product {index:D4}",
                Description = index % 3 == 0 ? null : $"Description {index}",
                Price = 1 + (index % 97),
                Status = (ProductStatus)(index % 3),
                Version = Guid.NewGuid(),
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(index),
                Rating = index % 5,
                TenantId = 1,
                Category = categories[index % categories.Count],
                Tags = [new ProductTag { Value = index % 2 == 0 ? "sale" : "new" }],
                Lines = [new OrderLine { Sku = $"S{index}", Quantity = index % 4, UnitPrice = 2 + (index % 7) }],
            });
        }

        db.SaveChanges();
    }
}
