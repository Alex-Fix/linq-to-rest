namespace LinqToRest.Server.IntegrationTests;

/// <summary>A many-to-many navigation (products ↔ labels over a pure join table) in filters, projections and includes.</summary>
public abstract class ManyToManyQueryTests(ShopFixture shop)
{
    // Chess (1): Gift. Go (3): Gift, New. Puzzle (6): Eco.
    [Fact]
    public async Task Filters_through_the_join_table()
    {
        shop.Server.Sql.Clear();

        var result = await shop.Server.QueryOkAsync("products", """
            {"v":1,"filter":{"any":{"source":{"member":"labels"},"var":"l","body":{"eq":[{"member":"l/name"},"Gift"]}}},"orderBy":[{"member":"id"}]}
            """);

        Assert.Equal([1, 3], JsonAssert.Ids(result));
        Assert.Contains("ProductLabel", Assert.Single(shop.Server.Sql), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projects_counts_and_names_in_sql()
    {
        shop.Server.Sql.Clear();

        var result = await shop.Server.QueryOkAsync("products", """
            {"v":1,"filter":{"in":[{"member":"id"},[1,2,3]]},"orderBy":[{"member":"id"}],
             "select":{"new":{"id":{"member":"id"},
                              "count":{"count":{"source":{"member":"labels"}}},
                              "names":{"map":{"source":{"member":"labels"},"var":"l","body":{"member":"l/name"}}}}}}
            """);

        JsonAssert.Equal("""[{"id":1,"count":1,"names":["Gift"]},{"id":2,"count":0,"names":[]},{"id":3,"count":2,"names":["Gift","New"]}]""", result.GetProperty("items"));
        Assert.Contains("ProductLabel", Assert.Single(shop.Server.Sql), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Includes_the_labels_ordered_by_key()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"eq":[{"member":"id"},3]},"include":["labels"]}""");

        JsonAssert.Equal("""[{"id":1,"name":"Gift"},{"id":2,"name":"New"}]""", JsonAssert.Single(result).GetProperty("labels"));
    }

    [Fact]
    public async Task Labels_are_a_set_of_their_own()
    {
        var result = await shop.Server.QueryOkAsync("labels", """{"v":1,"orderBy":[{"member":"name"}],"select":{"member":"name"}}""");

        Assert.Equal(["Eco", "Gift", "New"], result.GetProperty("items").EnumerateArray().Select(item => item.GetString()));
    }
}

public sealed class SqliteManyToManyQueryTests(SqliteShopFixture shop) : ManyToManyQueryTests(shop), IClassFixture<SqliteShopFixture>;

public sealed class PostgresManyToManyQueryTests(PostgresShopFixture shop) : ManyToManyQueryTests(shop), IClassFixture<PostgresShopFixture>;
