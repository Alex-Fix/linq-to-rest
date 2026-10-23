using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Server.IntegrationTests.Shop;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Sets;
using LinqToRest.Server.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>Built-in and custom rules on batch writes: every error at once, as one 422, before anything is written.</summary>
public abstract class ValidationTests(ShopDatabase database) : IAsyncLifetime
{
    private ShopServer? _started;

    public async ValueTask InitializeAsync() =>
        _started = database.SkipReason is null
            ? await ShopServer.StartAsync(database, options => options.Limits(limits => limits.MaxValidationErrors = 4), sets: options => options
                .AddSet<Product>(ConfigureProducts)
                .AddSet(new CategorySet())
                .AddSet(new OrderLineSet()))
            : null;

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
    public async Task Every_error_of_every_operation_comes_back_at_once_and_nothing_is_written()
    {
        var problem = await BatchAsync("""
            {"v":1,"operations":[
              {"id":"a","op":"insert","set":"categories","values":{"name":"Valid"}},
              {"id":"b","op":"insert","set":"products","values":{"name":"Abcdefghijklmnopqrstuvwxyz","price":0,"status":"Active"}},
              {"id":"c","op":"insert","set":"products","values":{"name":"Ok","price":1.005,"rating":9,"status":"Active"}}]}
            """, HttpStatusCode.UnprocessableEntity);

        JsonAssert.Problem(problem, "validation_failed", "/operations");
        Assert.Equal("Invalid values.", problem.GetProperty("title").GetString());
        Assert.Equal(
            [
                ("b", "/operations/1/values/name", "name", "too_long"),
                ("b", "/operations/1/values/price", "price", "out_of_range"),
                ("c", "/operations/2/values/price", "price", "too_precise"),
                ("c", "/operations/2/values/rating", "rating", "rating_needs_feature"),
            ],
            Errors(problem));
        Assert.False(await Db(db => db.Categories.AnyAsync(c => c.Name == "Valid", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_required_member_missing_from_an_insert_points_where_its_value_would_be()
    {
        var problem = await BatchAsync("""{"v":1,"operations":[{"id":"p","op":"insert","set":"products","values":{"price":5,"status":"Active"}}]}""", HttpStatusCode.UnprocessableEntity);

        Assert.Equal([("p", "/operations/0/values/name", "name", "required")], Errors(problem));
    }

    [Fact]
    public async Task Row_rules_without_a_member_point_at_the_operation()
    {
        var problem = await BatchAsync("""{"v":1,"operations":[{"id":"p","op":"insert","set":"products","values":{"name":"Same","description":"Same","price":5,"status":"Active"}}]}""", HttpStatusCode.UnprocessableEntity);

        Assert.Equal([("p", "/operations/0", null, "description_repeats_name")], Errors(problem));
    }

    [Fact]
    public async Task Updates_check_the_members_they_send_and_every_row_rule()
    {
        // Older data that a newer rule rejects: a lowercase name.
        await Db(db => db.Products.Where(p => p.Id == 2).ExecuteUpdateAsync(set => set.SetProperty(p => p.Name, "dune"), TestContext.Current.CancellationToken));

        var sent = await BatchAsync("""{"v":1,"operations":[{"id":"u","op":"update","set":"products","key":{"id":2},"values":{"name":"dune!"},"token":"00000002-0000-0000-0000-000000000000"}]}""", HttpStatusCode.UnprocessableEntity);
        var row = await BatchAsync("""{"v":1,"operations":[{"id":"u","op":"update","set":"products","key":{"id":1},"values":{"description":"Chess"},"token":"00000001-0000-0000-0000-000000000000"}]}""", HttpStatusCode.UnprocessableEntity);

        // Rejected writes left the token alone; this one succeeds although the stored name breaks the pattern.
        var unrelated = await BatchAsync("""{"v":1,"operations":[{"id":"u","op":"update","set":"products","key":{"id":2},"values":{"price":13},"token":"00000002-0000-0000-0000-000000000000"}]}""", HttpStatusCode.OK);

        Assert.Equal(13m, unrelated.GetProperty("results")[0].GetProperty("value").GetProperty("price").GetDecimal());
        Assert.Equal([("u", "/operations/0/values/name", "name", "invalid_format")], Errors(sent));
        Assert.Equal([("u", "/operations/0", null, "description_repeats_name")], Errors(row));
    }

    [Fact]
    public async Task A_member_that_broke_a_built_in_rule_skips_its_custom_rules()
    {
        var problem = await BatchAsync("""{"v":1,"operations":[{"id":"p","op":"insert","set":"products","values":{"name":"Cube","price":5000.005,"status":"Active"}}]}""", HttpStatusCode.UnprocessableEntity);

        Assert.Equal([("p", "/operations/0/values/price", "price", "out_of_range")], Errors(problem));
    }

    [Fact]
    public async Task Validators_query_the_database_and_report_member_and_row_errors()
    {
        var problem = await BatchAsync("""
            {"v":1,"operations":[
              {"id":"a","op":"insert","set":"products","values":{"name":"Chess","price":5,"status":"Active","isFeatured":true}},
              {"id":"b","op":"update","set":"products","key":{"id":3},"values":{"name":"Dune"},"token":"00000003-0000-0000-0000-000000000000"}]}
            """, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(
            [
                ("a", "/operations/0/values/name", "name", "duplicate"),
                ("a", "/operations/0", null, "featured_needs_description"),
                ("b", "/operations/1/values/name", "name", "duplicate"),
            ],
            Errors(problem));
    }

    [Fact]
    public async Task Validators_see_which_members_an_update_sends()
    {
        // Product 6 shares its name with a row the validator would flag only if the name were sent.
        await Db(db => db.Products.Where(p => p.Id == 6).ExecuteUpdateAsync(set => set.SetProperty(p => p.Name, "Chess"), TestContext.Current.CancellationToken));

        await BatchAsync("""{"v":1,"operations":[{"id":"u","op":"update","set":"products","key":{"id":6},"values":{"price":21},"token":"00000006-0000-0000-0000-000000000000"}]}""", HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invalid_code_from_a_validator_is_a_server_error_and_is_not_echoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/$batch");
        request.Content = new StringContent("""{"v":1,"operations":[{"id":"p","op":"insert","set":"products","values":{"name":"Boom","price":5,"status":"Active"}}]}""", Encoding.UTF8, "application/json");

        var exception = await Record.ExceptionAsync(async () =>
        {
            using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("Not a code", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        });

        // The test server may rethrow the unhandled exception instead of answering 500.
        if (exception is not null)
        {
            Assert.Contains("invalid error code", Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Valid_writes_are_saved()
    {
        var response = await BatchAsync("""{"v":1,"operations":[{"id":"p","op":"insert","set":"products","values":{"name":"Cube","description":"Puzzle","price":9.5,"releasedOn":"2026-01-01","status":"Active"}}]}""", HttpStatusCode.OK);

        Assert.Equal("Cube", response.GetProperty("results")[0].GetProperty("value").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_error_list_is_capped()
    {
        var operations = string.Join(",", Enumerable.Range(0, 3).Select(i => $$$"""{"id":"o{{{i}}}","op":"insert","set":"products","values":{"name":"x","price":0,"status":"Active"}}"""));

        var problem = await BatchAsync($$"""{"v":1,"operations":[{{operations}}]}""", HttpStatusCode.UnprocessableEntity);

        var errors = Errors(problem);
        Assert.Equal(5, errors.Count);
        Assert.Equal((null, null, null, "too_many_errors"), errors[^1]);
    }

    private static List<(string? Operation, string? Pointer, string? Member, string Code)> Errors(JsonElement problem) =>
        [.. problem.GetProperty("errors").EnumerateArray().Select(error => (Text(error, "operation"), Text(error, "pointer"), Text(error, "member"), Text(error, "code")!))];

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private async Task<T> Db<T>(Func<ShopDbContext, Task<T>> action)
    {
        using var scope = Server.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>());
    }

    private async Task<JsonElement> BatchAsync(string json, HttpStatusCode status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/$batch");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    // A lambda, not a configuration class: the other tests' servers scan this assembly for those.
    private static void ConfigureProducts(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").AllowWrites(WriteOperations.All);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(20).HasPattern("^[A-Z][a-z]*$");
        builder.Property(p => p.Description).HasMinLength(3);
        builder.Property(p => p.Price).HasRange(0.01m, 1000m);
        builder.Property(p => p.Status);
        builder.HasConcurrencyToken(p => p.Version);
        builder.Property(p => p.ReleasedOn).HasMin(new DateOnly(2000, 1, 1));
        builder.Property(p => p.Rating);
        builder.Property(p => p.IsFeatured);
        builder.Property(p => p.CategoryId);
        builder.Must(p => p.Price, price => price % 0.01m == 0, "too_precise");
        builder.Must(p => p.Rating <= 5 || p.IsFeatured, "rating_needs_feature", p => p.Rating);
        builder.Must(p => p.Name != p.Description, "description_repeats_name");
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasMany(p => p.Lines);
        builder.ValidateWith<UniqueNameValidator>();
    }

    // Public: created by dependency injection, like a real validator.
    public sealed class UniqueNameValidator(ShopDbContext db) : IEntityValidator<Product>
    {
        public async ValueTask ValidateAsync(EntityValidationContext<Product> context, CancellationToken cancellationToken)
        {
            var product = context.Entity;
            if (context.IsModified(p => p.Name)
                && await db.Products.AnyAsync(p => p.Name == product.Name && p.Id != product.Id, cancellationToken))
            {
                context.AddError(p => p.Name, "duplicate");
            }

            if (context.Operation == SetOperation.Insert && product is { IsFeatured: true, Description: null })
            {
                context.AddError("featured_needs_description");
            }

            if (product.Name == "Boom")
            {
                context.AddError("Not a code");
            }
        }
    }
}

public sealed class SqliteValidationTests() : ValidationTests(new SqliteDatabase());

public sealed class PostgresValidationTests(PostgresContainer postgres) : ValidationTests(new PostgresDatabase(postgres));
