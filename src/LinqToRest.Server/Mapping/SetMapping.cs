using System.Linq.Expressions;
using System.Reflection;
using LinqToRest.Modeling;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Query;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.Mapping;

/// <summary>The validated mapping of one set: how each DTO member and navigation is computed from the entity.</summary>
internal abstract class SetMapping(DtoType dtoType, Type entityType)
{
    public DtoType DtoType { get; } = dtoType;

    public Type EntityType { get; } = entityType;

    public Type PolicyType { get; } = typeof(DtoSetPolicy<,>).MakeGenericType(dtoType.ClrType, entityType);

    /// <summary>Gets each member's value as a lambda over the entity.</summary>
    public required IReadOnlyDictionary<DtoProperty, LambdaExpression> Bindings { get; init; }

    /// <summary>Gets the entity property of members assigned directly from one (the reverse binding writes use).</summary>
    public required IReadOnlyDictionary<DtoProperty, PropertyInfo> EntityProperties { get; init; }

    public IReadOnlyDictionary<DtoNavigation, NavigationMapping> Navigations { get; set; } = new Dictionary<DtoNavigation, NavigationMapping>();

    /// <summary>Loads the tracked entity with the given key values, if it passes the policy filter.</summary>
    public abstract Task<object?> FindAsync(DbContext db, LambdaExpression? policyFilter, IReadOnlyList<object?> key, CancellationToken cancellationToken);

    /// <summary><c>e =&gt; e.Key1 == v1 &amp;&amp; …</c> over the mapped key members.</summary>
    public LambdaExpression KeyPredicate(IReadOnlyList<object?> key)
    {
        var entity = Expression.Parameter(EntityType, "e");
        Expression? predicate = null;
        for (var index = 0; index < DtoType.Key.Count; index++)
        {
            var member = ExpressionHelpers.Inline(Bindings[DtoType.Key[index]], entity);
            predicate = ExpressionHelpers.AndAlso(predicate, Expression.Equal(member, ExpressionHelpers.Parameter(key[index], member.Type)));
        }

        return Expression.Lambda(predicate!, entity);
    }

    public abstract Task<QueryResult> ExecuteAsync(DbContext db, QueryPlan plan, CancellationToken cancellationToken);
}

internal sealed class SetMapping<TEntity>(DtoType dtoType, Func<DbContext, IQueryable<TEntity>> source)
    : SetMapping(dtoType, typeof(TEntity))
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

        return await query.Where((Expression<Func<TEntity, bool>>)KeyPredicate(key)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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

/// <summary>A DTO navigation mapped to an entity navigation.</summary>
internal sealed class NavigationMapping(LambdaExpression entity, PropertyInfo? entityProperty, SetMapping target)
{
    /// <summary>Gets the entity navigation as a lambda over the declaring entity.</summary>
    public LambdaExpression Entity { get; } = entity;

    /// <summary>Gets the entity navigation property when it's a direct member access (needed for batch refs).</summary>
    public PropertyInfo? EntityProperty { get; } = entityProperty;

    public SetMapping Target { get; } = target;
}

/// <summary>The DTO model, every set mapping and the server limits.</summary>
internal sealed class ServerModel(DtoModel model, IReadOnlyDictionary<DtoType, SetMapping> mappings, Type contextType, Protocol.QueryLimits limits)
{
    public DtoModel Model { get; } = model;

    public Type ContextType { get; } = contextType;

    public Protocol.QueryLimits Limits { get; } = limits;

    public SetMapping? FindSet(string setName) => Model.FindSet(setName) is { } type ? mappings[type] : null;

    public SetMapping Get(DtoType type) => mappings[type];
}
