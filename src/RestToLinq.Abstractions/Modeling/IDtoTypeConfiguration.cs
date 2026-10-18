namespace RestToLinq.Modeling;

/// <summary>Configures one DTO type of a <see cref="DtoModel"/>, like EF Core's entity type configurations.</summary>
/// <typeparam name="TDto">The DTO type being configured.</typeparam>
public interface IDtoTypeConfiguration<TDto>
    where TDto : class, IDto
{
    /// <summary>Configures the DTO type.</summary>
    /// <param name="builder">The builder for the DTO type.</param>
    void Configure(DtoTypeBuilder<TDto> builder);
}
