using System.Linq.Expressions;
using LinqToRest.Server.Query;

namespace LinqToRest.Server.UnitTests.Query;

/// <summary>
/// The keyset predicate run with LINQ-to-Objects, which orders null smallest like the server: walking pages from each
/// last row must give back the full order, across nulls and ties. Translation is proven by the integration tests.
/// </summary>
public sealed class KeysetTests
{
    private static readonly ParameterExpression Parameter = Expression.Parameter(typeof(Row), "e");

    private static readonly Row[] Rows =
    [
        .. Enumerable.Range(1, 24).Select(id => new Row
        {
            Id = id,
            Number = (id % 4) switch { 0 => null, 1 => 10, 2 => 20, _ => id },
            Text = (id % 3) switch { 0 => null, 1 => "a", _ => "b" },
            Level = (id % 5) switch { 0 => null, 1 => Grade.Low, _ => Grade.High },
            Required = id % 2 == 0 ? "x" : "y",
        }),
    ];

    public enum Grade
    {
        Low,
        High,
    }

    public static TheoryData<string, bool> Sorts => new()
    {
        { nameof(Row.Number), false },
        { nameof(Row.Number), true },
        { nameof(Row.Text), false },
        { nameof(Row.Text), true },
        { nameof(Row.Level), false },
        { nameof(Row.Level), true },
        { nameof(Row.Required), false },
        { nameof(Row.Required), true },
    };

    [Theory]
    [MemberData(nameof(Sorts))]
    public void Walking_pages_gives_the_full_order(string member, bool descending)
    {
        foreach (var take in new[] { 1, 2, 5 })
        {
            var sorts = new List<SortKey> { Sort(member, descending), Sort(nameof(Row.Id), descending: false) };
            Assert.Equal(Ordered(sorts).Select(row => row.Id), Walk(sorts, take));
        }
    }

    [Fact]
    public void Walking_two_nullable_members_gives_the_full_order()
    {
        var sorts = new List<SortKey> { Sort(nameof(Row.Text), descending: true), Sort(nameof(Row.Number), descending: false), Sort(nameof(Row.Id), descending: false) };

        Assert.Equal(Ordered(sorts).Select(row => row.Id), Walk(sorts, take: 3));
    }

    [Fact]
    public void Nulls_sort_first_ascending_and_last_descending()
    {
        var ascending = Ordered([Sort(nameof(Row.Number), descending: false), Sort(nameof(Row.Id), descending: false)]);
        var descending = Ordered([Sort(nameof(Row.Number), descending: true), Sort(nameof(Row.Id), descending: false)]);

        Assert.Null(ascending[0].Number);
        Assert.Null(descending[^1].Number);
    }

    private static SortKey Sort(string member, bool descending)
    {
        var property = Expression.Property(Parameter, member);
        var nullable = !property.Type.IsValueType || Nullable.GetUnderlyingType(property.Type) is not null;
        return new SortKey(Expression.Lambda(property, Parameter), descending, member, IsNullable: nullable && member != nameof(Row.Required));
    }

    private static List<Row> Ordered(List<SortKey> sorts)
    {
        IOrderedEnumerable<Row>? ordered = null;
        foreach (var sort in sorts)
        {
            var key = Key(sort);
            ordered = (ordered, sort.Descending) switch
            {
                (null, false) => Rows.OrderBy(key, Comparer<object?>.Create(Compare)),
                (null, true) => Rows.OrderByDescending(key, Comparer<object?>.Create(Compare)),
                (_, false) => ordered.ThenBy(key, Comparer<object?>.Create(Compare)),
                (_, true) => ordered.ThenByDescending(key, Comparer<object?>.Create(Compare)),
            };
        }

        return [.. ordered!];
    }

    // Null smallest; the test strings are single ASCII letters, which order the same ordinally and by culture.
    private static int Compare(object? left, object? right) => (left, right) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (string a, string b) => string.CompareOrdinal(a, b),
        _ => Comparer<object>.Default.Compare(left, right),
    };

    private static List<int> Walk(List<SortKey> sorts, int take)
    {
        var ordered = Ordered(sorts);
        var page = ordered.Take(take).ToList();
        var ids = page.Select(row => row.Id).ToList();
        while (page.Count == take)
        {
            var last = page[^1];
            var values = sorts.Select(sort => Key(sort)(last)).ToList();
            var after = Expression.Lambda<Func<Row, bool>>(QueryCompiler.Keyset(sorts, values), Parameter).Compile();
            page = [.. ordered.Where(after).Take(take)];
            ids.AddRange(page.Select(row => row.Id));
        }

        return ids;
    }

    private static Func<Row, object?> Key(SortKey sort) =>
        Expression.Lambda<Func<Row, object?>>(Expression.Convert(sort.Key.Body, typeof(object)), sort.Key.Parameters).Compile();

    public sealed class Row
    {
        public int Id { get; init; }

        public int? Number { get; init; }

        public string? Text { get; init; }

        public Grade? Level { get; init; }

        public string Required { get; init; } = "";
    }
}
