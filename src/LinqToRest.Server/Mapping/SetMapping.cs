using System.Linq.Expressions;
using System.Reflection;
using LinqToRest.Modeling;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Query;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.Mapping;

/// <summary>One validated set: its exposed members, navigations and rules, each member as a lambda over the entity.</summary>
internal abstract class SetMapping(RestSetModel model, Type entityType)
{
    private Dictionary<string, SetProperty> _properties = [];
    private Dictionary<string, SetNavigation> _navigations = [];

    /// <summary>Gets the set as described in the model document.</summary>
    public RestSetModel Model { get; } = model;

    public string Name => Model.Name;

    public Type EntityType { get; } = entityType;

    public Type PolicyType { get; } = typeof(SetPolicy<>).MakeGenericType(entityType);

    public RestPaginationModel Pagination => Model.Pagination;

    public SetProperty[] Properties { get; private set; } = [];

    public SetNavigation[] Navigations { get; private set; } = [];

    public SetProperty[] Key { get; private set; } = [];

    public SetProperty? ConcurrencyToken { get; private set; }

    /// <summary>Gets the custom (<c>Must</c>) rules, in the order they were configured.</summary>
    public SetRule[] Rules { get; private set; } = [];

    public SetProperty? FindProperty(string name) => _properties.GetValueOrDefault(name);

    public SetNavigation? FindNavigation(string name) => _navigations.GetValueOrDefault(name);

    public void Initialize(SetProperty[] properties, SetNavigation[] navigations, SetRule[] rules)
    {
        Rules = rules;
        Properties = properties;
        Navigations = navigations;
        _properties = properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        _navigations = navigations.ToDictionary(navigation => navigation.Name, StringComparer.Ordinal);
        Key = [.. Model.Key.Select(name => _properties[name])];
        ConcurrencyToken = Model.ConcurrencyToken is { } token ? _properties[token] : null;
    }

    /// <summary>Loads the tracked entity with the given key values, if it passes the policy filter.</summary>
    public abstract Task<object?> FindAsync(DbContext db, LambdaExpression? policyFilter, IReadOnlyList<object?> key, CancellationToken cancellationToken);

    /// <summary><c>e =&gt; (e.Key1 == v1 &amp;&amp; …) || …</c> over the key members, matching any of <paramref name="keys"/>.</summary>
    public LambdaExpression KeyPredicate(IReadOnlyList<IReadOnlyList<object?>> keys)
    {
        var entity = Expression.Parameter(EntityType, "e");
        var members = Key.Select(property => ExpressionHelpers.Inline(property.Binding, entity)).ToList();
        Expression? predicate = null;
        foreach (var key in keys)
        {
            Expression? equal = null;
            for (var index = 0; index < members.Count; index++)
            {
                equal = ExpressionHelpers.AndAlso(equal, Expression.Equal(members[index], ExpressionHelpers.Parameter(key[index], members[index].Type)));
            }

            predicate = predicate is null ? equal! : Expression.OrElse(predicate, equal!);
        }

        return Expression.Lambda(predicate!, entity);
    }

    public abstract Task<QueryResult> ExecuteAsync(DbContext db, QueryPlan plan, CancellationToken cancellationToken);

    public override string ToString() => Name;
}

internal sealed class SetMapping<TEntity>(RestSetModel model, Func<DbContext, IQueryable<TEntity>> source)
    : SetMapping(model, typeof(TEntity))
    where TEntity : class
{
    private IQueryable<TEntity> Query(DbContext db) => source(db);

    public override async Task<object?> FindAsync(DbContext db, LambdaExpression? policyFilter, IReadOnlyList<object?> key, CancellationToken cancellationToken)
    {
        var query = Query(db);
        if (policyFilter is Expression<Func<TEntity, bool>> filter)
        {
            query = query.Where(filter);
        }

        return await query.Where((Expression<Func<TEntity, bool>>)KeyPredicate([key])).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<QueryResult> ExecuteAsync(DbContext db, QueryPlan plan, CancellationToken cancellationToken)
    {
        var query = Query(db);
        if (plan.PolicyFilter is Expression<Func<TEntity, bool>> policyFilter)
        {
            query = query.Where(policyFilter);
        }

        if (plan.Filter is Expression<Func<TEntity, bool>> filter)
        {
            query = query.Where(filter);
        }

        long? total = plan.Count ? await query.LongCountAsync(cancellationToken).ConfigureAwait(false) : null;
        if (plan.Keyset is Expression<Func<TEntity, bool>> keyset)
        {
            query = query.Where(keyset);
        }

        for (var index = 0; index < plan.Sorts.Count; index++)
        {
            var sort = plan.Sorts[index];
            var method = (index == 0, sort.Descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };
            query = query.Provider.CreateQuery<TEntity>(
                Expression.Call(typeof(Queryable), method, [typeof(TEntity), sort.Key.ReturnType], query.Expression, Expression.Quote(sort.Key)));
        }

        if (plan is { Mode: Protocol.PageMode.Offset, Skip: > 0 })
        {
            query = query.Skip(plan.Skip);
        }

        if (plan.Take == 0)
        {
            return new QueryResult([], null, total);
        }

        // Cursor pages fetch one extra row to know whether another page follows.
        var cursor = plan.Mode == Protocol.PageMode.Cursor;
        var rows = await query
            .Take(cursor ? plan.Take + 1 : plan.Take)
            .Select((Expression<Func<TEntity, object?[]>>)plan.Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? next = null;
        if (cursor && rows.Count > plan.Take)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = Protocol.Cursor.Encode(plan.SortHash!, [.. plan.CursorIndexes.Select(index => last[index])]);
        }

        return new QueryResult(rows, next, total);
    }
}

/// <summary>An exposed scalar or primitive-collection member.</summary>
internal sealed class SetProperty
{
    /// <summary>Gets the wire name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the member's value as a lambda over the entity.</summary>
    public required LambdaExpression Binding { get; init; }

    /// <summary>Gets the entity property writes and key lookups use, for members bound directly to one.</summary>
    public PropertyInfo? EntityProperty { get; init; }

    /// <summary>Gets the value type (the collection type for collections).</summary>
    public Type ClrType => Binding.ReturnType;

    /// <summary>Gets the element type of a primitive collection.</summary>
    public Type? ElementType { get; init; }

    public bool IsCollection => ElementType is not null;

    public required bool IsNullable { get; init; }

    public bool IsWritable { get; init; }

    public bool IsSortable { get; init; }

    public bool IsFilterable { get; init; }

    /// <summary>Gets the built-in validation rules, or <see langword="null"/>.</summary>
    public RestPropertyRules? Rules { get; init; }

    public override string ToString() => Name;
}

/// <summary>A custom rule: <see cref="Member"/> is the property the error points at, or null for the row.</summary>
internal sealed record SetRule(SetProperty? Member, Func<object, bool> IsValid, string Code, bool OnlyWhenSent);

/// <summary>An exposed navigation to another set.</summary>
internal sealed class SetNavigation
{
    /// <summary>Gets the wire name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the entity navigation as a lambda over the declaring entity.</summary>
    public required LambdaExpression Entity { get; init; }

    /// <summary>Gets the entity navigation property (batch refs set it).</summary>
    public required PropertyInfo EntityProperty { get; init; }

    public required SetMapping Target { get; init; }

    public required bool IsCollection { get; init; }

    public required bool IsNullable { get; init; }

    /// <summary>Gets the exposed property holding the target's key, if configured.</summary>
    public SetProperty? ForeignKey { get; init; }

    public override string ToString() => Name;
}

/// <summary>Every set, the model document, the DbContext type and the server limits.</summary>
internal sealed class ServerModel(RestModel model, IReadOnlyList<SetMapping> sets, Type contextType, Protocol.QueryLimits limits)
{
    private readonly Dictionary<string, SetMapping> _sets = sets.ToDictionary(set => set.Name, StringComparer.Ordinal);

    public RestModel Model { get; } = model;

    public IReadOnlyList<SetMapping> Sets { get; } = sets;

    public Type ContextType { get; } = contextType;

    public Protocol.QueryLimits Limits { get; } = limits;

    public SetMapping? FindSet(string setName) => _sets.GetValueOrDefault(setName);
}
