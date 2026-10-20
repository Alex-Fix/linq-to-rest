using System.Linq.Expressions;

namespace LinqToRest.Server.Policies;

/// <summary>
/// Optional rules for the sets of one entity type, resolved from DI per request: which operations are allowed,
/// which rows are visible, and server-owned values on insert and update. A set's own write operations
/// (<c>AllowWrites</c>) are checked first.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public abstract class SetPolicy<TEntity> : ISetPolicy
    where TEntity : class
{
    /// <summary>
    /// Returns the predicate a row must satisfy to be visible, or <see langword="null"/> for all rows. Applied to
    /// queries, counts, cursor pages, navigations and the rows an update or delete may touch; hidden rows behave as
    /// missing (404).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The predicate.</returns>
    protected virtual Expression<Func<TEntity, bool>>? Filter(PolicyContext context) => null;

    /// <summary>Returns whether the operation is allowed for this request. Default: allowed.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="context">The request.</param>
    /// <returns><see langword="true"/> to allow it.</returns>
    protected virtual ValueTask<bool> CanAsync(SetOperation operation, PolicyContext context) => ValueTask.FromResult(true);

    /// <summary>Sets server-owned values on a new entity, after client values were applied.</summary>
    /// <param name="entity">The new entity.</param>
    /// <param name="context">The request.</param>
    protected virtual void OnInsert(TEntity entity, PolicyContext context)
    {
    }

    /// <summary>Sets server-owned values on an updated entity, after client values were applied.</summary>
    /// <param name="entity">The updated entity.</param>
    /// <param name="context">The request.</param>
    protected virtual void OnUpdate(TEntity entity, PolicyContext context)
    {
    }

    LambdaExpression? ISetPolicy.GetFilter(PolicyContext context) => Filter(context);

    ValueTask<bool> ISetPolicy.CanAsync(SetOperation operation, PolicyContext context) => CanAsync(operation, context);

    void ISetPolicy.OnInsert(object entity, PolicyContext context) => OnInsert((TEntity)entity, context);

    void ISetPolicy.OnUpdate(object entity, PolicyContext context) => OnUpdate((TEntity)entity, context);
}

internal interface ISetPolicy
{
    LambdaExpression? GetFilter(PolicyContext context);

    ValueTask<bool> CanAsync(SetOperation operation, PolicyContext context);

    void OnInsert(object entity, PolicyContext context);

    void OnUpdate(object entity, PolicyContext context);
}
