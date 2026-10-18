using System.Linq.Expressions;

namespace RestToLinq.Server.Policies;

/// <summary>
/// Optional rules for one set, resolved from DI per request: which operations are allowed, which rows are visible,
/// and server-owned values on insert and update.
/// </summary>
/// <typeparam name="TDto">The DTO type.</typeparam>
/// <typeparam name="TEntity">The entity type.</typeparam>
public abstract class DtoSetPolicy<TDto, TEntity> : IDtoSetPolicy
    where TDto : class, IDto
    where TEntity : class
{
    /// <summary>
    /// Returns the predicate a row must satisfy to be visible, or <see langword="null"/> for all rows. Applied to
    /// queries, counts, cursor pages, navigations and the rows an update or delete may touch; hidden rows behave as
    /// missing (404).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The predicate.</returns>
    public virtual Expression<Func<TEntity, bool>>? Filter(PolicyContext context) => null;

    /// <summary>Returns whether the operation is allowed for this request. Default: allowed.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="context">The request.</param>
    /// <returns><see langword="true"/> to allow it.</returns>
    public virtual ValueTask<bool> CanAsync(DtoOperation operation, PolicyContext context) => ValueTask.FromResult(true);

    /// <summary>Sets server-owned values on a new entity, after client values were applied.</summary>
    /// <param name="entity">The new entity.</param>
    /// <param name="context">The request.</param>
    public virtual void OnInsert(TEntity entity, PolicyContext context)
    {
    }

    /// <summary>Sets server-owned values on an updated entity, after client values were applied.</summary>
    /// <param name="entity">The updated entity.</param>
    /// <param name="context">The request.</param>
    public virtual void OnUpdate(TEntity entity, PolicyContext context)
    {
    }

    LambdaExpression? IDtoSetPolicy.GetFilter(PolicyContext context) => Filter(context);

    void IDtoSetPolicy.OnInsert(object entity, PolicyContext context) => OnInsert((TEntity)entity, context);

    void IDtoSetPolicy.OnUpdate(object entity, PolicyContext context) => OnUpdate((TEntity)entity, context);
}

internal interface IDtoSetPolicy
{
    LambdaExpression? GetFilter(PolicyContext context);

    ValueTask<bool> CanAsync(DtoOperation operation, PolicyContext context);

    void OnInsert(object entity, PolicyContext context);

    void OnUpdate(object entity, PolicyContext context);
}
