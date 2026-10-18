using System.Collections.Immutable;
using System.Linq.Expressions;
using RestToLinq.Modeling;
using RestToLinq.Protocol;
using RestToLinq.Server.Internal;
using RestToLinq.Server.Mapping;
using RestToLinq.Server.Policies;

namespace RestToLinq.Server.Query;

/// <summary>Turns a query request into a <see cref="QueryPlan"/> for one set.</summary>
internal static class QueryCompiler
{
    private static readonly HashSet<Type> CursorTypes =
    [
        typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal), typeof(string),
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan),
    ];

    public static QueryPlan Compile(ServerModel server, SetMapping mapping, QueryRequest request, RequestPolicies policies)
    {
        var binder = new QueryBinder(server, policies);
        var root = Expression.Parameter(mapping.EntityType, "e");
        var rootBound = new EntityBound(root, mapping, null, isNullable: false);
        var scope = new Scope(rootBound, ImmutableDictionary<string, Bound>.Empty);
        var pagination = mapping.DtoType.Pagination;

        var page = request.Page;
        var mode = page?.Mode ?? (pagination.Modes.HasFlag(PaginationModes.Offset) ? PageMode.Offset : PageMode.Cursor);
        if (!pagination.Modes.HasFlag(mode == PageMode.Offset ? PaginationModes.Offset : PaginationModes.Cursor))
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, "/page/mode");
        }

        var take = page?.Take ?? pagination.DefaultPageSize;
        if (take > pagination.MaxPageSize)
        {
            throw RequestException.LimitExceeded("/page/take", "pageSize");
        }

        var skip = page?.Skip ?? 0;
        if (skip > pagination.MaxSkip)
        {
            throw RequestException.LimitExceeded("/page/skip", "skip");
        }

        var filter = request.Filter is null ? null : Expression.Lambda(binder.Predicate(request.Filter, scope), root);
        var sorts = Sorts(binder, mapping, request, scope, root, mode == PageMode.Cursor);

        if (request.Select is not null && request.Include.Count > 0)
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, "/include");
        }

        var row = new Row(root);
        var shape = request.Select is null
            ? binder.DefaultShape(rootBound, row, IncludeTree.Parse(request.Include, mapping.DtoType, server.Limits.MaxPathDepth))
            : binder.Select(request.Select, scope, row);

        string? hash = null;
        LambdaExpression? keyset = null;
        IReadOnlyList<int> cursorIndexes = [];
        if (mode == PageMode.Cursor)
        {
            hash = Cursor.HashSort(mapping.DtoType.SetName, sorts.Select(sort => (sort.Path, sort.Descending)));
            cursorIndexes = [.. sorts.Select(sort => row.Add(sort.Key.Body))];
            if (page?.After is { } after)
            {
                var values = Cursor.Decode(after, hash, [.. sorts.Select(sort => sort.Key.ReturnType)], "/page/after");
                keyset = Expression.Lambda(Keyset(sorts, values), root);
            }
        }

        return new QueryPlan
        {
            PolicyFilter = policies.Filter(mapping),
            Filter = filter,
            Sorts = sorts,
            Keyset = keyset,
            Projection = row.ToLambda(),
            Shape = shape,
            Mode = mode,
            Skip = skip,
            Take = take,
            Count = request.Count,
            SortHash = hash,
            CursorIndexes = cursorIndexes,
        };
    }

    private static List<SortKey> Sorts(QueryBinder binder, SetMapping mapping, QueryRequest request, Scope scope, ParameterExpression root, bool cursor)
    {
        var sorts = new List<SortKey>();
        for (var index = 0; index < request.OrderBy.Count; index++)
        {
            var item = request.OrderBy[index];
            if (binder.Member(item.Member, scope, MemberUse.Sort) is not ValueBound value)
            {
                throw RequestException.Invalid(ErrorCodes.NotSortable, $"/orderBy/{index}");
            }

            if (cursor && value.IsNullable)
            {
                throw RequestException.Invalid(ErrorCodes.CursorNullableSort, $"/orderBy/{index}");
            }

            if (cursor && !CursorTypes.Contains(ExpressionHelpers.Underlying(value.Expression.Type)) && !value.Expression.Type.IsEnum)
            {
                throw RequestException.Invalid(ErrorCodes.NotSortable, $"/orderBy/{index}");
            }

            sorts.Add(new SortKey(Expression.Lambda(value.Expression, root), item.Descending, item.Member.Path));
        }

        // The key always ends the sort, so pages are deterministic and cursors identify one row.
        foreach (var key in mapping.DtoType.Key.Where(key => sorts.TrueForAll(sort => sort.Path != key.WireName)))
        {
            sorts.Add(new SortKey(Expression.Lambda(ExpressionHelpers.Inline(mapping.Bindings[key], root), root), false, key.WireName));
        }

        return sorts;
    }

    /// <summary><c>k1 &gt; v1 OR (k1 == v1 AND k2 &gt; v2) OR …</c>, with &lt; for descending members.</summary>
    private static Expression Keyset(List<SortKey> sorts, IReadOnlyList<object?> values)
    {
        Expression? result = null;
        Expression? equalPrefix = null;
        for (var index = 0; index < sorts.Count; index++)
        {
            var key = ToComparable(sorts[index].Key.Body);
            var value = ToComparable(ExpressionHelpers.Parameter(values[index], sorts[index].Key.ReturnType));
            var after = QueryBinder.Comparison(sorts[index].Descending ? ExpressionType.LessThan : ExpressionType.GreaterThan, key, value)!;
            var term = equalPrefix is null ? after : Expression.AndAlso(equalPrefix, after);
            result = result is null ? term : Expression.OrElse(result, term);
            var equal = Expression.Equal(key, value);
            equalPrefix = equalPrefix is null ? equal : Expression.AndAlso(equalPrefix, equal);
        }

        return result!;
    }

    private static Expression ToComparable(Expression value) =>
        value.Type.IsEnum ? Expression.Convert(value, Enum.GetUnderlyingType(value.Type)) : value;
}
