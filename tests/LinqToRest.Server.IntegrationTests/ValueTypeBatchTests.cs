using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Server.IntegrationTests.Shop;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>Batches write values whole: replaced in place on every mapping, checked member by member.</summary>
public abstract class ValueTypeBatchTests(ShopDatabase database) : IAsyncLifetime
{
    private ShopServer? _started;

    public async ValueTask InitializeAsync() => _started = database.SkipReason is null ? await ShopServer.StartAsync(database) : null;

    public async ValueTask DisposeAsync()
    {
        if (_started is not null)
        {
            await _started.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private ShopServer Server
    {
        get
        {
            database.SkipIfUnavailable();
            return _started!;
        }
    }

    [Fact]
    public async Task Complex_values_are_replaced_whole_and_cleared()
    {
        var result = await BatchOkAsync(Server, """
            {"v":1,"operations":[
              {"id":"a","op":"update","set":"products","key":{"id":1},"token":"00000001-0000-0000-0000-000000000000",
               "values":{"size":{"width":41,"height":40,"weight":{"amount":1.3,"unit":"g"}},"origin":null}},
              {"id":"b","op":"update","set":"products","key":{"id":2},"token":"00000002-0000-0000-0000-000000000000","values":{"origin":{"country":"DE","city":"Berlin"}}}]}
            """);

        var values = result.GetProperty("results");
        JsonAssert.Equal("""{"width":41,"height":40,"weight":{"amount":1.3,"unit":"g"}}""", values[0].GetProperty("value").GetProperty("size"));
        Assert.Equal(JsonValueKind.Null, values[0].GetProperty("value").GetProperty("origin").ValueKind);
        JsonAssert.Equal("""{"country":"DE","city":"Berlin"}""", values[1].GetProperty("value").GetProperty("origin"));
        await using var db = Db();
        var saved = await db.Products.AsNoTracking().Where(p => p.Id <= 2).OrderBy(p => p.Id).Select(p => new { p.Size, p.Origin }).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Dimensions { Width = 41, Height = 40, Weight = new Weight(1.3, "g") }, saved[0].Size);
        Assert.Null(saved[0].Origin);
        Assert.Equal(new Place("DE", "Berlin"), saved[1].Origin);
    }

    [Fact]
    public async Task Owned_values_are_inserted_replaced_and_cleared()
    {
        var result = await BatchOkAsync(Server, """
            {"v":1,"operations":[
              {"id":"a","op":"update","set":"categories","key":{"id":1},"values":{"contact":{"email":"books@shop.example","phone":null}}},
              {"id":"b","op":"update","set":"categories","key":{"id":3},"values":{"contact":null}},
              {"id":"c","op":"insert","set":"categories","values":{"name":"Puzzles","contact":{"email":"puzzles@shop.example","phone":"1"}}},
              {"id":"d","op":"insert","set":"products","values":{"name":"Cube","origin":{"country":"HU","city":"Budapest"},
               "size":{"width":5.7,"height":5.7,"weight":{"amount":0.1,"unit":"kg"}}}}]}
            """);

        var values = result.GetProperty("results");
        JsonAssert.Equal("""{"email":"books@shop.example","phone":null}""", values[0].GetProperty("value").GetProperty("contact"));
        Assert.Equal(JsonValueKind.Null, values[1].GetProperty("value").GetProperty("contact").ValueKind);
        JsonAssert.Equal("""{"email":"puzzles@shop.example","phone":"1"}""", values[2].GetProperty("value").GetProperty("contact"));
        JsonAssert.Equal("""{"country":"HU","city":"Budapest"}""", values[3].GetProperty("value").GetProperty("origin"));
        await using var db = Db();
        var contacts = await db.Categories.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Contact == null ? null : c.Contact.Email).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["books@shop.example", "games@shop.example", null, "puzzles@shop.example"], contacts);
    }

    [Fact]
    public async Task Inserts_keep_the_entity_default_of_a_value_they_do_not_send()
    {
        var result = await BatchOkAsync(Server, """{"v":1,"operations":[{"id":"a","op":"insert","set":"products","values":{"name":"Cube"}}]}""");

        JsonAssert.Equal("""{"width":0,"height":0,"weight":{"amount":0,"unit":"kg"}}""", result.GetProperty("results")[0].GetProperty("value").GetProperty("size"));
    }

    [Theory]
    [InlineData("""{"size":{"width":1,"height":1,"weight":{"amount":1,"unit":"kg"},"depth":1}}""", "unknown_member", "/operations/0/values/size")]
    [InlineData("""{"size":{"width":1,"weight":{"amount":1,"unit":"kg"}}}""", "invalid_batch", "/operations/0/values/size/height")]
    [InlineData("""{"size":{"width":1,"height":1,"weight":{"amount":1}}}""", "invalid_batch", "/operations/0/values/size/weight/unit")]
    [InlineData("""{"size":{"width":"wide","height":1,"weight":{"amount":1,"unit":"kg"}}}""", "type_mismatch", "/operations/0/values/size/width")]
    [InlineData("""{"size":{"width":1,"height":1,"weight":{"amount":1,"unit":null}}}""", "type_mismatch", "/operations/0/values/size/weight/unit")]
    [InlineData("""{"size":[1,2]}""", "type_mismatch", "/operations/0/values/size")]
    [InlineData("""{"size":{"width":1,"height":1,"weight":{"amount":-1,"unit":"kg"}}}""", "type_mismatch", "/operations/0/values/size/weight")]
    [InlineData("""{"size":null}""", "type_mismatch", "/operations/0/values/size")]
    [InlineData("""{"origin":{"country":null,"city":"Lviv"}}""", "type_mismatch", "/operations/0/values/origin/country")]
    public async Task Malformed_values_are_rejected(string values, string code, string location)
    {
        var problem = await BatchProblemAsync(Server, """{"v":1,"operations":[{"id":"a","op":"update","set":"products","key":{"id":1},"token":"00000001-0000-0000-0000-000000000000","values":""" + values + "}]}");

        JsonAssert.Problem(problem, code, location);
        await using var db = Db();
        Assert.Equal(40, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == 1, TestContext.Current.CancellationToken)).Size.Width);
    }

    [Fact]
    public async Task Value_members_are_validated_with_nested_pointers()
    {
        var problem = await BatchProblemAsync(Server, """
            {"v":1,"operations":[
              {"id":"a","op":"update","set":"products","key":{"id":1},"token":"00000001-0000-0000-0000-000000000000",
               "values":{"size":{"width":-1,"height":1,"weight":{"amount":1,"unit":"kilograms"}},"origin":{"country":"Ukraine","city":null}}},
              {"id":"b","op":"update","set":"categories","key":{"id":2},"values":{"contact":{"email":" ","phone":null}}}]}
            """, (HttpStatusCode)422);

        Assert.Equal(
            [("a", "/operations/0/values/size/width", "size/width", "out_of_range"),
             ("a", "/operations/0/values/size/weight/unit", "size/weight/unit", "too_long"),
             ("a", "/operations/0/values/origin/country", "origin/country", "invalid_format"),
             ("b", "/operations/1/values/contact/email", "contact/email", "required")],
            Errors(problem));
    }

    [Fact]
    public async Task Missing_required_values_and_validators_report_on_value_members()
    {
        await using var server = await ShopServer.StartAsync(
            database,
            options => options.AddPolicy<SizeDroppingPolicy>(),
            sets: options =>
            {
                options.AddSet<Product>(b =>
                {
                    b.ToSet("products").AllowWrites(WriteOperations.All).ValidateWith<OriginValidator>().Value(p => p.Size);
                    b.Value(p => p.Origin);
                });
                options.AddShopValueTypes();
            });

        var problem = await BatchProblemAsync(server, """
            {"v":1,"operations":[{"id":"a","op":"insert","set":"products","values":{"origin":{"country":"UA","city":"Atlantis"}}}]}
            """, (HttpStatusCode)422);

        Assert.Equal(
            [("a", "/operations/0/values/size", "size", "required"),
             ("a", "/operations/0/values/origin/city", "origin/city", "unknown_city")],
            Errors(problem));
    }

    [Fact]
    public async Task Read_only_values_are_not_writable()
    {
        await using var server = await ShopServer.StartAsync(database, sets: options =>
        {
            options.AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.Update).Value(p => p.Origin).IsReadOnly());
            options.AddShopValueTypes();
        });

        var problem = await BatchProblemAsync(server, """{"v":1,"operations":[{"id":"a","op":"update","set":"products","key":{"id":1},"values":{"origin":null}}]}""");

        JsonAssert.Problem(problem, "not_writable", "/operations/0/values/origin");
    }

    private static List<(string?, string?, string?, string)> Errors(JsonElement problem) =>
        [.. problem.GetProperty("errors").EnumerateArray().Select(error => (Text(error, "operation"), Text(error, "pointer"), Text(error, "member"), Text(error, "code")!))];

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private ShopDbContext Db() => Server.Services.CreateScope().ServiceProvider.GetRequiredService<ShopDbContext>();

    private static async Task<JsonElement> BatchOkAsync(ShopServer server, string json)
    {
        var (status, body) = await SendAsync(server, json);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
        return body;
    }

    private static async Task<JsonElement> BatchProblemAsync(ShopServer server, string json, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        var (actual, body) = await SendAsync(server, json);
        Assert.True(actual == status, $"{(int)actual}: {body}");
        return body;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(ShopServer server, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/$batch");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone());
    }

    /// <summary>Leaves inserted products without a size, as an entity without a default would.</summary>
    public sealed class SizeDroppingPolicy : SetPolicy<Product>
    {
        protected override void OnInsert(Product entity, PolicyContext context) => entity.Size = null!;
    }

    public sealed class OriginValidator : IEntityValidator<Product>
    {
        public ValueTask ValidateAsync(EntityValidationContext<Product> context, CancellationToken cancellationToken)
        {
            if (context.IsModified(p => p.Origin!.City) && context.Entity.Origin?.City == "Atlantis")
            {
                context.AddError(p => p.Origin!.City, "unknown_city");
            }

            return ValueTask.CompletedTask;
        }
    }
}

public sealed class SqliteValueTypeBatchTests() : ValueTypeBatchTests(new SqliteDatabase());

public sealed class PostgresValueTypeBatchTests(PostgresContainer postgres) : ValueTypeBatchTests(new PostgresDatabase(postgres));
