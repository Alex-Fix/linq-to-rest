using System.Text.Json;

namespace LinqToRest.Client.UnitTests;

/// <summary>Grouped queries and whole-query aggregates: what the client sends, reads and refuses locally.</summary>
public sealed class GroupingTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Grouped_results_are_read_through_the_group_select()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{"status":"Active","count":3,"from":12.5},{"status":"Draft","count":1,"from":30}]}""");

        var groups = await shop.Products
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), From = g.Min(p => p.Price) })
            .ToListAsync(Token);

        Assert.Equal([(ProductStatus.Active, 3, 12.5m), (ProductStatus.Draft, 1, 30m)], groups.Select(g => (g.Status, g.Count, g.From)));
        Assert.False(http.LastJson.GetProperty("page").TryGetProperty("take", out _));
    }

    [Fact]
    public async Task Counting_groups_keeps_the_select_that_having_names()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[],"totalCount":2}""");

        var count = await shop.Products.GroupBy(p => p.CategoryId).Select(g => new { Count = g.Count() }).Where(f => f.Count > 1).CountAsync(Token);

        Assert.Equal(2, count);
        Assert.True(http.LastJson.GetProperty("count").GetBoolean());
        Assert.Equal("count", http.LastJson.GetProperty("select").GetProperty("new").EnumerateObject().Single().Name);
        Assert.Equal(0, http.LastJson.GetProperty("page").GetProperty("take").GetInt32());
    }

    [Fact]
    public async Task Whole_query_aggregates_follow_linq_on_empty_results()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[41.5]}""")
            .Respond("""{"items":[]}""")
            .Respond("""{"items":[]}""")
            .Respond("""{"items":[]}""");

        var total = await shop.Products.SumAsync(p => p.Price, Token);
        var none = await shop.Products.Where(p => p.Price < 0).SumAsync(p => p.Price, Token);
        var noMax = await shop.Products.MaxAsync(p => (decimal?)p.Price, Token);
        var noMin = await Assert.ThrowsAsync<InvalidOperationException>(() => shop.Products.MinAsync(p => p.Price, Token));

        Assert.Equal((41.5m, 0m, (decimal?)null), (total, none, noMax));
        Assert.Contains("no rows", noMin.Message, StringComparison.Ordinal);
        Assert.Contains("\"groupBy\":[]", http.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Averages_are_read_as_their_result_type()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[2.5]}""");

        var average = await shop.Set<OrderLineDto>().AverageAsync(l => l.Quantity, Token);

        Assert.Equal(2.5, average);
        Assert.Equal("avg", JsonDocument.Parse(http.LastBody).RootElement.GetProperty("select").EnumerateObject().Single().Name);
    }

    public static readonly TheoryData<string> Refused =
    [
        "non-aggregatable sum", "any over a group", "nested collection in an aggregate", "group without select", "composite key selected whole",
        "where before the group select", "sort by a non-field", "cursor page of groups", "sum after take", "sum after select",
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Unsupported_grouping_fails_locally(string name)
    {
        var (shop, http) = Shop.Create();
        Func<Task> query = name switch
        {
            "non-aggregatable sum" => () => shop.Products.GroupBy(p => p.Status).Select(g => g.Sum(p => p.Id)).ToListAsync(Token),
            "any over a group" => () => shop.Products.GroupBy(p => p.Status).Select(g => g.Any(p => p.Price > 1)).ToListAsync(Token),
            "nested collection in an aggregate" => () => shop.Products.GroupBy(p => p.Status).Select(g => g.Sum(p => p.Lines.Sum(l => l.Quantity))).ToListAsync(Token),
            "group without select" => () => shop.Products.GroupBy(p => p.Status).Take(1).ToListAsync(Token),
            "composite key selected whole" => () => shop.Products.GroupBy(p => new { p.Status, p.CategoryId }).Select(g => g.Key).ToListAsync(Token),
            "where before the group select" => () => shop.Products.GroupBy(p => p.Status).Where(g => g.Count() > 1).Select(g => g.Key).ToListAsync(Token),
            "sort by a non-field" => () => shop.Products.GroupBy(p => p.Status).Select(g => new { g.Key }).OrderBy(f => f.Key.ToString().Length).ToListAsync(Token),
            "cursor page of groups" => () => shop.Products.GroupBy(p => p.Status).Select(g => new { g.Key }).ToCursorPageAsync(cancellationToken: Token),
            "sum after take" => () => shop.Products.Take(5).SumAsync(p => p.Price, Token),
            _ => () => shop.Products.Select(p => new { p.Price }).SumAsync(p => p.Price, Token),
        };

        await Assert.ThrowsAsync<NotSupportedException>(query);
        Assert.Empty(http.Requests);
    }
}
