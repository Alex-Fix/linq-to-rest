using System.Linq.Expressions;
using LinqToRest.Server.Query;

namespace LinqToRest.Server.UnitTests.Query;

public sealed class NullOrderingTests
{
    private static readonly ParameterExpression Parameter = Expression.Parameter(typeof(Row), "e");

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", true)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", true)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", false)]
    [InlineData("Some.Other.Provider", false)]
    [InlineData(null, false)]
    public void Only_providers_ordering_null_smallest_are_native(string? provider, bool native) =>
        Assert.Equal(native, NullOrdering.IsNative(provider));

    [Fact]
    public void Native_providers_sort_by_the_members_alone() =>
        Assert.Equal(["OrderBy(Number)", "ThenBy(Id)"], Calls(native: true, Sort(nameof(Row.Number), descending: false), Sort(nameof(Row.Id), descending: false)));

    [Fact]
    public void Ascending_nullable_sorts_put_nulls_first_on_other_providers() =>
        Assert.Equal(
            ["OrderByDescending((Number == null))", "ThenBy(Number)", "ThenBy(Id)"],
            Calls(native: false, Sort(nameof(Row.Number), descending: false), Sort(nameof(Row.Id), descending: false)));

    [Fact]
    public void Descending_nullable_sorts_put_nulls_last_on_other_providers() =>
        Assert.Equal(
            ["ThenBy((Text == null))", "ThenByDescending(Text)", "ThenBy(Id)"],
            Calls(native: false, Sort(nameof(Row.Id), descending: false), Sort(nameof(Row.Text), descending: true), Sort(nameof(Row.Id), descending: false)).Skip(1));

    [Fact]
    public void Required_members_get_no_null_key() =>
        Assert.Equal(["OrderBy(Required)", "ThenBy(Id)"], Calls(native: false, Sort(nameof(Row.Required), descending: false), Sort(nameof(Row.Id), descending: false)));

    [Fact]
    public void Rows_come_back_with_nulls_smallest()
    {
        var rows = new[] { new Row { Id = 1, Number = 5 }, new Row { Id = 2 }, new Row { Id = 3, Number = 1 } }.AsQueryable();
        var sorted = rows.Provider.CreateQuery<Row>(
            NullOrdering.Sort(rows.Expression, typeof(Row), [Sort(nameof(Row.Number), descending: true), Sort(nameof(Row.Id), descending: false)], native: false));

        Assert.Equal([1, 3, 2], sorted.Select(row => row.Id));
    }

    private static SortKey Sort(string member, bool descending) =>
        new(Expression.Lambda(Expression.Property(Parameter, member), Parameter), descending, member, IsNullable: member is nameof(Row.Number) or nameof(Row.Text));

    private static List<string> Calls(bool native, params SortKey[] sorts)
    {
        var source = Array.Empty<Row>().AsQueryable().Expression;
        var calls = new List<string>();
        for (var node = NullOrdering.Sort(source, typeof(Row), sorts, native); node is MethodCallExpression call; node = call.Arguments[0])
        {
            var key = (LambdaExpression)((UnaryExpression)call.Arguments[1]).Operand;
            calls.Insert(0, $"{call.Method.Name}({key.Body.ToString().Replace("e.", "", StringComparison.Ordinal)})");
        }

        return calls;
    }

    public sealed class Row
    {
        public int Id { get; init; }

        public int? Number { get; init; }

        public string? Text { get; init; }

        public string Required { get; init; } = "";
    }
}
