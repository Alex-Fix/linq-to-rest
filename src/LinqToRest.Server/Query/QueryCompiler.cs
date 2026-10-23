using System.Collections.Immutable;
using System.Linq.Expressions;
using LinqToRest.Modeling;
using LinqToRest.Protocol;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;

namespace LinqToRest.Server.Query;

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
        if (request.GroupBy is not null)
        {
            return CompileGrouped(server, mapping, request, policies);
        }

        // Only a grouped query filters groups.
        if (request.Having is not null)
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, "/having");
        }

        var binder = new QueryBinder(server, policies);
        var root = Expression.Parameter(mapping.EntityType, "e");
        var rootBound = new EntityBound(root, mapping, null, isNullable: false);
        var scope = new Scope(rootBound, ImmutableDictionary<string, Bound>.Empty);
        var pagination = mapping.Pagination;

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
            ? binder.DefaultShape(rootBound, row, IncludeTree.Parse(request.Include, mapping, server.Limits.MaxPathDepth))
            : binder.Select(request.Select, scope, row);

        string? hash = null;
        LambdaExpression? keyset = null;
        IReadOnlyList<int> cursorIndexes = [];
        if (mode == PageMode.Cursor)
        {
            hash = Cursor.HashSort(mapping.Name, sorts.Select(sort => (sort.Path, sort.Descending)));
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

    /// <summary>
    /// A grouped query: rows filtered (policy first), grouped by groupable keys, the select projected per group, then
    /// having and order over the select's fields, keys last as tie-breakers, and offset pages (or the group cap).
    /// </summary>
    private static QueryPlan CompileGrouped(ServerModel server, SetMapping mapping, QueryRequest request, RequestPolicies policies)
    {
        if (request.Include.Count > 0)
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, "/include");
        }

        if (request.Page is { Mode: PageMode.Cursor })
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, "/page/mode");
        }

        var select = request.Select ?? throw RequestException.Invalid(ErrorCodes.InvalidQuery, "/select");
        var binder = new QueryBinder(server, policies);
        var root = Expression.Parameter(mapping.EntityType, "e");
        var rootBound = new EntityBound(root, mapping, null, isNullable: false);
        var scope = new Scope(rootBound, ImmutableDictionary<string, Bound>.Empty);
        var filter = request.Filter is null ? null : Expression.Lambda(binder.Predicate(request.Filter, scope), root);

        var keys = request.GroupBy!
            .Select(path => binder.Member(path, scope, MemberUse.Group) as ValueBound ?? throw RequestException.Invalid(ErrorCodes.NotGroupable, path.Location))
            .ToList();
        var keySelector = GroupKeys.Selector([.. keys.Select(key => key.Expression)], root);
        var group = Expression.Parameter(typeof(IGrouping<,>).MakeGenericType(keySelector.ReturnType, mapping.EntityType), "g");
        var keyBounds = keys.Select((key, index) => new ValueBound(GroupKeys.Key(group, index), key.IsNullable)).ToList();

        var row = new Row(group);
        var groupScope = scope with { Group = new GroupScope(group, keyBounds, mapping), RootHidden = true };
        var shape = binder.Select(select, groupScope, row);

        // Having and order name the select's fields (top-level values of an object select).
        var fields = shape is ObjectShape objectShape && select is NewNode shapeNode
            ? shapeNode.Fields.Zip(objectShape.Fields)
                .Where(pair => pair.Second.Value is ValueShape)
                .ToDictionary(pair => pair.First.Key, pair => new ValueBound(row[((ValueShape)pair.Second.Value).Index], isNullable: true), StringComparer.Ordinal)
            : [];
        var fieldScope = scope with { RootHidden = true, Fields = fields };
        var having = request.Having is null ? null : Expression.Lambda(binder.Predicate(request.Having, fieldScope), group);

        var sorts = new List<SortKey>();
        for (var index = 0; index < request.OrderBy.Count; index++)
        {
            var item = request.OrderBy[index];
            var value = ((ValueBound)binder.Member(item.Member, fieldScope, MemberUse.Sort)).Expression;
            if (QueryBinder.Comparison(ExpressionType.LessThan, value, value) is null && ExpressionHelpers.Underlying(value.Type) != typeof(bool))
            {
                throw RequestException.Invalid(ErrorCodes.NotSortable, $"/orderBy/{index}");
            }

            sorts.Add(new SortKey(Expression.Lambda(value, group), item.Descending, item.Member.Path));
        }

        // Keys end the order, so pages of groups are deterministic.
        sorts.AddRange(keyBounds.Select((key, index) => new SortKey(Expression.Lambda(key.Expression, group), false, $"key{index}")));

        var pagination = mapping.Pagination;
        int? maxGroups = request.Page?.Take is null ? server.Limits.MaxGroups : null;
        var take = request.Page?.Take ?? server.Limits.MaxGroups + 1;
        if (maxGroups is null && take > pagination.MaxPageSize)
        {
            throw RequestException.LimitExceeded("/page/take", "pageSize");
        }

        var skip = request.Page?.Skip ?? 0;
        if (skip > pagination.MaxSkip)
        {
            throw RequestException.LimitExceeded("/page/skip", "skip");
        }

        return new QueryPlan
        {
            PolicyFilter = policies.Filter(mapping),
            Filter = filter,
            Sorts = [],
            Projection = row.ToLambda(),
            Shape = shape,
            Mode = PageMode.Offset,
            Skip = skip,
            Take = take,
            Count = request.Count,
            Group = new GroupPlan(keySelector, having, sorts, maxGroups),
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
        foreach (var key in mapping.Key.Where(key => sorts.TrueForAll(sort => sort.Path != key.Name)))
        {
            sorts.Add(new SortKey(Expression.Lambda(ExpressionHelpers.Inline(key.Binding, root), root), false, key.Name));
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
