namespace RestToLinq.Server.Mapping;

/// <summary>
/// Maps a DTO to the entity it projects, like EF Core's entity type configurations. Lives only on the server; the
/// entity type never appears in shared code.
/// </summary>
/// <typeparam name="TDto">The DTO type.</typeparam>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IDtoMapping<TDto, TEntity>
    where TDto : class, IDto
    where TEntity : class
{
    /// <summary>Configures the mapping.</summary>
    /// <param name="builder">The mapping builder.</param>
    void Configure(DtoMappingBuilder<TDto, TEntity> builder);
}
