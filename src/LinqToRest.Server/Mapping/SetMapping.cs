using System.Linq.Expressions;
using System.Reflection;
using LinqToRest.Modeling;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Query;
using LinqToRest.Server.Validation;
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

    /// <summary>Gets the validator types (<c>ValidateWith</c>), in the order they were configured.</summary>
    public Type[] Validators { get; private set; } = [];

    public SetProperty? FindProperty(string name) => _properties.GetValueOrDefault(name);

    public SetNavigation? FindNavigation(string name) => _navigations.GetValueOrDefault(name);

    public void Initialize(SetProperty[] properties, SetNavigation[] navigations, SetRule[] rules, Type[] validators)
    {
        Rules = rules;
        Validators = validators;
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

    /// <summary>Runs one validator (an <c>IEntityValidator&lt;TEntity&gt;</c> of this set) on a row.</summary>
    public abstract ValueTask ValidateAsync(object validator, object entity, ValidationScope scope, CancellationToken cancellationToken);

    public override string ToString() => Name;
}

internal sealed class SetMapping<TEntity>(RestSetModel model, Func<DbContext, IQueryable<TEntity>> source)
    : SetMapping(model, typeof(TEntity))
    where TEntity : class
{
    private IQueryable<TEntity> Query(DbContext db) => source(db);

    public override ValueTask ValidateAsync(object validator, object entity, ValidationScope scope, CancellationToken cancellationToken) =>
        ((IEntityValidator<TEntity>)validator).ValidateAsync(new EntityValidationContext<TEntity>((TEntity)entity, scope), cancellationToken);

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

        if (plan.Group is { } group)
        {
            return await ExecuteGroupedAsync(query, plan, group, cancellationToken).ConfigureAwait(false);
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

    /// <summary>GroupBy, having, order, page and the per-group projection, composed as one query over the filtered rows.</summary>
    private static async Task<QueryResult> ExecuteGroupedAsync(IQueryable<TEntity> rows, QueryPlan plan, GroupPlan group, CancellationToken cancellationToken)
    {
        var groupType = plan.Projection.Parameters[0].Type;
        Expression groups = Expression.Call(typeof(Queryable), nameof(Queryable.GroupBy), [typeof(TEntity), group.Key.ReturnType], rows.Expression, Expression.Quote(group.Key));
        if (group.Having is { } having)
        {
            groups = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [groupType], groups, Expression.Quote(having));
        }

        long? total = null;
        if (plan.Count)
        {
            var counted = Expression.Call(typeof(Queryable), nameof(Queryable.Select), [groupType, typeof(int)], groups, Expression.Quote(Expression.Lambda(Expression.Constant(1), plan.Projection.Parameters[0])));
            total = await rows.Provider.CreateQuery<int>(counted).LongCountAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < group.Sorts.Count; index++)
        {
            var sort = group.Sorts[index];
            var method = (index == 0, sort.Descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };
            groups = Expression.Call(typeof(Queryable), method, [groupType, sort.Key.ReturnType], groups, Expression.Quote(sort.Key));
        }

        if (plan.Skip > 0)
        {
            groups = Expression.Call(typeof(Queryable), nameof(Queryable.Skip), [groupType], groups, Expression.Constant(plan.Skip));
        }

        if (plan.Take == 0)
        {
            return new QueryResult([], null, total);
        }

        groups = Expression.Call(typeof(Queryable), nameof(Queryable.Take), [groupType], groups, Expression.Constant(plan.Take));
        var projected = Expression.Call(typeof(Queryable), nameof(Queryable.Select), [groupType, typeof(object?[])], groups, Expression.Quote(plan.Projection));
        var result = await rows.Provider.CreateQuery<object?[]>(projected).ToListAsync(cancellationToken).ConfigureAwait(false);
        return group.MaxGroups is { } max && result.Count > max
            ? throw RequestException.LimitExceeded("/groupBy", "groups")
            : new QueryResult(result, null, total);
    }
}

/// <summary>An exposed scalar, primitive-collection or value-type member (of a set or of a value type).</summary>
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

    /// <summary>Gets the value type of a member exposed with <c>Value(...)</c>.</summary>
    public ServerValueType? ValueType { get; init; }

    public required bool IsNullable { get; init; }

    public bool IsWritable { get; init; }

    public bool IsSortable { get; init; }

    public bool IsFilterable { get; init; }

    public bool IsGroupable { get; init; }

    public bool IsAggregatable { get; init; }

    /// <summary>Gets the built-in validation rules, or <see langword="null"/>.</summary>
    public RestPropertyRules? Rules { get; init; }

    public override string ToString() => Name;
}

/// <summary>An exposed value type: its members as lambdas over the value, and how batches build one.</summary>
internal sealed class ServerValueType(RestValueTypeModel model, Type clrType)
{
    private Dictionary<string, SetProperty> _properties = [];

    /// <summary>Gets the value type as described in the model document.</summary>
    public RestValueTypeModel Model { get; } = model;

    public string Name => Model.Name;

    public Type ClrType { get; } = clrType;

    public SetProperty[] Properties { get; private set; } = [];

    /// <summary>Gets how batches construct a value, or <see langword="null"/> when no writable property uses the type.</summary>
    public ValueConstruction? Construction { get; private set; }

    public SetProperty? FindProperty(string name) => _properties.GetValueOrDefault(name);

    public void Initialize(SetProperty[] properties, ValueConstruction? construction)
    {
        Properties = properties;
        Construction = construction;
        _properties = properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
    }

    public override string ToString() => Name;
}

/// <summary>
/// Builds a value from its members: the constructor and the members passed to it in parameter order, then the members
/// assigned through their setters. Resolved once at startup from the configured members only.
/// </summary>
internal sealed record ValueConstruction(ConstructorInfo Constructor, SetProperty[] Arguments, SetProperty[] Setters);

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
