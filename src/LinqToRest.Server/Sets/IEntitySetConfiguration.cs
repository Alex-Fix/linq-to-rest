namespace LinqToRest.Server.Sets;

/// <summary>
/// Exposes an EF Core entity as a LinqToRest set, like EF Core's entity type configurations. Only the members,
/// computed values and navigations it configures exist on the wire; the set is read-only until it allows writes.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IEntitySetConfiguration<TEntity>
    where TEntity : class
{
    /// <summary>Configures the set.</summary>
    /// <param name="builder">The set builder.</param>
    void Configure(EntitySetBuilder<TEntity> builder);
}
