namespace LinqToRest.Server.Validation;

/// <summary>
/// Checks inserted and updated rows of a set with code that needs services or async work, such as a uniqueness query.
/// Registered with <c>ValidateWith&lt;TValidator&gt;()</c> on the set and created once per request from its services,
/// so the constructor can take the <c>DbContext</c>. It runs after the values of every operation are applied and after
/// the built-in and <c>Must</c> rules, inside the batch transaction; report problems with
/// <see cref="EntityValidationContext{TEntity}.AddError(string)"/>. Error codes reach the client, so don't let them
/// reveal rows the caller can't see.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IEntityValidator<TEntity>
    where TEntity : class
{
    /// <summary>Checks one inserted or updated row.</summary>
    /// <param name="context">The row, the operation and where errors go.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>A task that completes when the row is checked.</returns>
    ValueTask ValidateAsync(EntityValidationContext<TEntity> context, CancellationToken cancellationToken);
}
