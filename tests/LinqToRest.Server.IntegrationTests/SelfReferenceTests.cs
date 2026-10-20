using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.IntegrationTests.Shop;
using LinqToRest.Server.Policies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>A set whose navigation targets the same set (a category tree), on both providers.</summary>
public abstract class SelfReferenceTests(ShopDatabase database) : IAsyncLifetime
{
    private WebApplication? _app;
    private IAsyncDisposable? _owned;
    private HttpClient? _client;

    private HttpClient Client
    {
        get
        {
            database.SkipIfUnavailable();
            return _client!;
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (database.SkipReason is not null)
        {
            return;
        }

        var lease = await database.CreateAsync();
        _owned = lease.Owned;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TreeDbContext>(lease.Configure);
        builder.Services.AddLinqToRest(o => o
            .UseDbContext<TreeDbContext>()
            .AddPolicy<NodePolicy>()
            .AddSet<Node>(b =>
            {
                b.ToSet("nodes").AllowWrites(WriteOperations.Insert);
                b.Property(n => n.Name).IsSortable();
                b.Property(n => n.ParentId);
                b.Computed("childCount", n => n.Children.Count);
                b.HasOne(n => n.Parent).WithForeignKey(n => n.ParentId);
                b.HasMany(n => n.Children);
            }));
        _app = builder.Build();
        _app.MapLinqToRest("/api");
        await _app.StartAsync();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TreeDbContext>();
            await db.Database.EnsureCreatedAsync();
            TreeDbContext.Seed(db);
        }

        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        if (_owned is not null)
        {
            await _owned.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Filters_through_the_parent_and_finds_roots()
    {
        var children = await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"parent/name"},"Games"]},"orderBy":[{"member":"name"}]}""");
        var roots = await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"parentId"},null]},"orderBy":[{"member":"name"}]}""");
        var grandchildren = await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"parent/parent/name"},"Games"]}}""");

        Assert.Equal(["Board"], Names(children));
        Assert.Equal(["Books", "Games"], Names(roots));
        Assert.Equal(["Chess"], Names(grandchildren));
    }

    [Fact]
    public async Task Includes_children_and_parents_in_order()
    {
        var games = JsonAssert.Single(await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"name"},"Games"]},"include":["children/children"]}"""));
        var chess = JsonAssert.Single(await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"name"},"Chess"]},"include":["parent/parent"]}"""));

        Assert.Equal(["Board"], games.GetProperty("children").EnumerateArray().Select(child => child.GetProperty("name").GetString()));
        Assert.Equal("Chess", games.GetProperty("children")[0].GetProperty("children")[0].GetProperty("name").GetString());
        Assert.Equal("Board", chess.GetProperty("parent").GetProperty("name").GetString());
        Assert.Equal("Games", chess.GetProperty("parent").GetProperty("parent").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, chess.GetProperty("parent").GetProperty("parent").GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task Lambdas_counts_and_projections_over_children()
    {
        var withChildren = await QueryAsync("""{"v":1,"filter":{"any":{"source":{"member":"children"},"var":"c","body":{"startsWith":[{"member":"c/name"},"B"]}}},"orderBy":[{"member":"name"}]}""");
        var shape = JsonAssert.Single(await QueryAsync("""
            {"v":1,"filter":{"eq":[{"member":"name"},"Games"]},
             "select":{"new":{"name":{"member":"name"},"count":{"member":"childCount"},
                              "children":{"map":{"source":{"member":"children"},"var":"c","body":{"member":"c/name"}}}}}}
            """));

        Assert.Equal(["Games"], Names(withChildren));

        // The hidden child is filtered from the navigation, but a set's own Computed value reads the entity
        // directly, so childCount still counts it (documented: keep computed values within what the set may show).
        JsonAssert.Equal("""{"name":"Games","count":2,"children":["Board"]}""", shape);
    }

    [Fact]
    public async Task Hidden_rows_read_as_missing_through_the_tree()
    {
        var hiddenChild = await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"name"},"Video"]},"include":["children"]}""");
        var underHidden = JsonAssert.Single(await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"name"},"Retro"]},"include":["parent"]}"""));
        var books = JsonAssert.Single(await QueryAsync("""{"v":1,"filter":{"eq":[{"member":"name"},"Books"]},"include":["children"]}"""));

        Assert.Empty(hiddenChild.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, underHidden.GetProperty("parent").ValueKind);
        Assert.Empty(books.GetProperty("children").EnumerateArray());
    }

    [Fact]
    public async Task Cursor_pages_walk_the_whole_tree()
    {
        var names = new List<string?>();
        string? after = null;
        do
        {
            var cursor = after is null ? "" : $",\"after\":\"{after}\"";
            var page = await QueryAsync("{\"v\":1,\"orderBy\":[{\"member\":\"name\"}],\"page\":{\"mode\":\"cursor\",\"take\":2" + cursor + "}}");
            names.AddRange(Names(page));
            after = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (after is not null);

        Assert.Equal(["Board", "Books", "Chess", "Games", "Retro"], names);
    }

    [Fact]
    public async Task Batch_inserts_a_parent_and_child_with_a_ref_to_the_same_set()
    {
        using var response = await Client.PostAsync(
            new Uri("/api/$batch", UriKind.Relative),
            new StringContent("""
                {"v":1,"operations":[
                  {"id":"p","op":"insert","set":"nodes","values":{"name":"Music"}},
                  {"id":"c","op":"insert","set":"nodes","values":{"name":"Vinyl"},"refs":{"parentId":"p"}}]}
                """, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parent = body.GetProperty("results")[0].GetProperty("value");
        var child = body.GetProperty("results")[1].GetProperty("value");
        Assert.Equal(parent.GetProperty("id").GetInt32(), child.GetProperty("parentId").GetInt32());
        Assert.Equal(1, parent.GetProperty("childCount").GetInt32());
    }

    private async Task<JsonElement> QueryAsync(string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Query, "/api/nodes");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static string?[] Names(JsonElement result) =>
        [.. result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString())];
}

public sealed class Node
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsHidden { get; set; }
    public int? ParentId { get; set; }
    public Node? Parent { get; set; }
    public List<Node> Children { get; set; } = [];
}

public sealed class TreeDbContext(DbContextOptions<TreeDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();

    /// <summary>Games → (Board → Chess, Video [hidden] → Retro); Books → (Comics [hidden]).</summary>
    public static void Seed(TreeDbContext db)
    {
        var games = new Node { Name = "Games" };
        var board = new Node { Name = "Board", Parent = games };
        var video = new Node { Name = "Video", Parent = games, IsHidden = true };
        var books = new Node { Name = "Books" };
        db.Nodes.AddRange(
            games,
            board,
            video,
            new Node { Name = "Chess", Parent = board },
            new Node { Name = "Retro", Parent = video },
            books,
            new Node { Name = "Comics", Parent = books, IsHidden = true });
        db.SaveChanges();
    }
}

/// <summary>Hidden nodes are invisible: as rows, as parents and as children.</summary>
public sealed class NodePolicy : SetPolicy<Node>
{
    protected override Expression<Func<Node, bool>> Filter(PolicyContext context) => node => !node.IsHidden;
}

public sealed class SqliteSelfReferenceTests() : SelfReferenceTests(new SqliteDatabase());

public sealed class PostgresSelfReferenceTests(PostgresContainer postgres) : SelfReferenceTests(new PostgresDatabase(postgres));
