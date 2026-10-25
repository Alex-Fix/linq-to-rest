using System.Collections.Frozen;
using System.Linq.Expressions;
using LinqToRest.Server.Internal;

namespace LinqToRest.Server.Query;

/// <summary>
/// Orders null smallest on every provider, as LINQ does (first ascending, last descending). Providers that already
/// order NULL that way keep a plain <c>ORDER BY x</c>, which an index can serve; any other provider (PostgreSQL orders
/// NULL largest) gets <c>x == null</c> before each nullable sort, in the opposite direction.
/// </summary>
internal static class NullOrdering
{
    private static readonly FrozenSet<string> NullsSmallest = FrozenSet.Create(
        StringComparer.Ordinal,
        "Microsoft.EntityFrameworkCore.Sqlite",
        "Microsoft.EntityFrameworkCore.SqlServer");

    /// <summary>Whether the provider already orders NULL smallest; unknown providers don't, to stay correct.</summary>
    public static bool IsNative(string? providerName) => providerName is not null && NullsSmallest.Contains(providerName);

    /// <summary>Applies the sorts to a query of <paramref name="elementType"/> rows.</summary>
    public static Expression Sort(Expression source, Type elementType, IReadOnlyList<SortKey> sorts, bool native)
    {
        var first = true;
        foreach (var sort in sorts)
        {
            var key = sort.Key;
            if (!native && sort.IsNullable && ExpressionHelpers.IsNullable(key.ReturnType))
            {
                var isNull = Expression.Lambda(Expression.Equal(key.Body, Expression.Constant(null, key.ReturnType)), key.Parameters);
                source = OrderBy(source, elementType, isNull, !sort.Descending, first);
                first = false;
            }

            source = OrderBy(source, elementType, key, sort.Descending, first);
            first = false;
        }

        return source;
    }

    private static MethodCallExpression OrderBy(Expression source, Type elementType, LambdaExpression key, bool descending, bool first)
    {
        var method = (first, descending) switch
        {
            (true, false) => nameof(Queryable.OrderBy),
            (true, true) => nameof(Queryable.OrderByDescending),
            (false, false) => nameof(Queryable.ThenBy),
            (false, true) => nameof(Queryable.ThenByDescending),
        };
        return Expression.Call(typeof(Queryable), method, [elementType, key.ReturnType], source, Expression.Quote(key));
    }
}
