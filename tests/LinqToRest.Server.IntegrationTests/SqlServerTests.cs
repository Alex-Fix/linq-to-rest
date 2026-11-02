using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Server.IntegrationTests.Shop;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>
/// What SQL Server defines differently from SQLite and PostgreSQL: string comparisons follow its default collation
/// (case-insensitive, trailing spaces ignored), <c>uniqueidentifier</c> sorts differently from .NET, and a command
/// takes at most 2,100 parameters. String results follow the database; everything else matches the other providers.
/// </summary>
public sealed class SqlServerTests(SqlServerContainer sqlServer) : IAsyncLifetime
{
    private readonly SqlServerDatabase _database = new(sqlServer);
    private ShopServer? _started;

    public async ValueTask InitializeAsync() => _started = _database.SkipReason is null ? await ShopServer.StartAsync(_database) : null;

    public async ValueTask DisposeAsync()
    {
        if (_started is not null)
        {
            await _started.DisposeAsync();
        }
    }

    private ShopServer Server
    {
        get
        {
            _database.SkipIfUnavailable();
            return _started!;
        }
    }

    [Theory]
    [InlineData("""{"eq":[{"member":"name"},"BOOKS"]}""")]
    [InlineData("""{"eq":[{"member":"name"},"Books   "]}""")]
    [InlineData("""{"contains":[{"member":"name"},"OOK"]}""")]
    [InlineData("""{"startsWith":[{"member":"name"},"bo"]}""")]
    [InlineData("""{"eq":[{"toLower":[{"member":"name"}]},{"toLower":["BOOKS"]}]}""")]
    public async Task String_comparisons_follow_the_case_insensitive_collation(string filter)
    {
        var result = await Server.QueryOkAsync("categories", $$$"""{"v":1,"filter":{{{filter}}},"page":{"mode":"cursor"}}""");

        Assert.Equal([1], JsonAssert.Ids(result));
    }

    [Fact]
    public async Task Sorting_interleaves_case_variants()
    {
        await BatchOkAsync("""
            {"v":1,"operations":[
              {"id":"a","op":"insert","set":"categories","values":{"name":"apple"}},
              {"id":"z","op":"insert","set":"categories","values":{"name":"Zebra"}}]}
            """);

        var result = await Server.QueryOkAsync("categories", """{"v":1,"orderBy":[{"member":"name"}],"select":{"member":"name"},"page":{"mode":"cursor"}}""");

        // Ordinal order would put "apple" last.
        Assert.Equal(["apple", "Books", "Games", "Tools", "Zebra"], result.GetProperty("items").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Cursor_pages_over_names_equal_under_the_collation_neither_skip_nor_repeat()
    {
        await BatchOkAsync("""
            {"v":1,"operations":[
              {"id":"a","op":"insert","set":"categories","values":{"name":"Gadget"}},
              {"id":"b","op":"insert","set":"categories","values":{"name":"gadget"}},
              {"id":"c","op":"insert","set":"categories","values":{"name":"GADGET "}}]}
            """);
        var all = JsonAssert.Ids(await Server.QueryOkAsync("categories", """{"v":1,"orderBy":[{"member":"name"}],"page":{"mode":"cursor","take":20}}"""));

        foreach (var take in new[] { 1, 2 })
        {
            Assert.Equal(all, await WalkAsync("categories", """[{"member":"name"}]""", take));
        }

        Assert.Equal(6, all.Length);
    }

    [Fact]
    public async Task Cursor_pages_over_guid_keys_follow_the_database_order()
    {
        // Keys are generated (sequential GUIDs, whose SQL Server order differs from Guid.CompareTo).
        var inserts = Enumerable.Range(0, 6).Select(i => $$$"""{"id":"b{{{i}}}","op":"insert","set":"bins","values":{"label":"B{{{i}}}"}}""");
        await BatchOkAsync($$$"""{"v":1,"operations":[{{{string.Join(",", inserts)}}}]}""");
        var offset = Labels(await Server.QueryOkAsync("bins", """{"v":1,"page":{"mode":"offset","take":20}}"""));

        var walked = new List<string>();
        string? cursor = null;
        do
        {
            var after = cursor is null ? "" : $",\"after\":\"{cursor}\"";
            var page = await Server.QueryOkAsync("bins", $$$"""{"v":1,"page":{"mode":"cursor","take":3{{{after}}}}}""");
            walked.AddRange(Labels(page));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);

        Assert.Equal(8, offset.Count);
        Assert.Equal(offset, walked);
    }

    [Fact]
    public async Task A_decimal_the_column_cant_hold_is_a_bad_request_in_a_query()
    {
        var problem = await Server.QueryProblemAsync("products", """{"v":1,"filter":{"gt":[{"member":"price"},100000000000000000000]}}""");

        JsonAssert.Problem(problem, "type_mismatch", "");
    }

    [Fact]
    public async Task A_decimal_the_column_cant_hold_is_a_bad_request_in_a_batch()
    {
        // SqlClient refuses the parameter before the database sees it, as in a query.
        using var response = await SendBatchAsync("""
            {"v":1,"operations":[{"id":"p","op":"insert","set":"products","values":{"name":"Big","price":100000000000000000000,"status":"Active","isFeatured":false}}]}
            """);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains("type_mismatch", body, StringComparison.Ordinal);
        Assert.DoesNotContain("100000000000000000000", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_in_list_at_the_limit_stays_within_the_parameter_limit()
    {
        var ids = string.Join(",", Enumerable.Range(1, 500));

        var result = await Server.QueryOkAsync("products", $$$"""{"v":1,"filter":{"and":[{"in":[{"member":"id"},[{{{ids}}}]]},{"in":[{"member":"name"},["Chess","Dune","Go","Hammer"]]}]},"page":{"mode":"offset"}}""");

        Assert.Equal([1, 2, 3, 4], JsonAssert.Ids(result));
    }

    private async Task<int[]> WalkAsync(string set, string orderBy, int take)
    {
        var seen = new List<int>();
        string? cursor = null;
        do
        {
            var after = cursor is null ? "" : $",\"after\":\"{cursor}\"";
            var page = await Server.QueryOkAsync(set, $$$"""{"v":1,"orderBy":{{{orderBy}}},"page":{"mode":"cursor","take":{{{take}}}{{{after}}}}}""");
            seen.AddRange(JsonAssert.Ids(page));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);

        return [.. seen];
    }

    private static List<string> Labels(JsonElement result) =>
        [.. result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("label").GetString()!)];

    private async Task BatchOkAsync(string json)
    {
        using var response = await SendBatchAsync(json);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
    }

    private async Task<HttpResponseMessage> SendBatchAsync(string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/$batch");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Tenant", "1");
        return await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
