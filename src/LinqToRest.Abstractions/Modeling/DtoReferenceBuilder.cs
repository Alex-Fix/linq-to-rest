using System.Linq.Expressions;

namespace LinqToRest.Modeling;

/// <summary>Configures a reference navigation of a DTO.</summary>
/// <typeparam name="TDto">The declaring DTO type.</typeparam>
public sealed class DtoReferenceBuilder<TDto>
    where TDto : class, IDto
{
    private readonly NavigationDefinition _definition;

    internal DtoReferenceBuilder(NavigationDefinition definition) => _definition = definition;

    /// <summary>
    /// Sets the foreign key property of the declaring DTO. Assigning the navigation to a tracked DTO on the client
    /// updates this property, and inserts can reference a DTO inserted in the same batch.
    /// </summary>
    /// <param name="foreignKey">The foreign key property selector.</param>
    /// <typeparam name="TKey">The foreign key type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoReferenceBuilder<TDto> WithForeignKey<TKey>(Expression<Func<TDto, TKey>> foreignKey)
    {
        ArgumentNullException.ThrowIfNull(foreignKey);
        _definition.ForeignKey = MemberExpressions.GetProperty(foreignKey, nameof(foreignKey));
        return this;
    }
}
