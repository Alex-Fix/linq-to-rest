using System.Linq.Expressions;

namespace LinqToRest.Modeling;

/// <summary>Configures a DTO type: its set, key, concurrency token, members, navigations and pagination.</summary>
/// <typeparam name="TDto">The DTO type being configured.</typeparam>
public sealed class DtoTypeBuilder<TDto>
    where TDto : class, IDto
{
    internal DtoTypeBuilder() => Definition = new DtoTypeDefinition(typeof(TDto));

    internal DtoTypeDefinition Definition { get; }

    /// <summary>Sets the name of the set that exposes this DTO, used in request paths (for example <c>products</c>).</summary>
    /// <param name="name">A name starting with a lowercase letter, followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public DtoTypeBuilder<TDto> ToSet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Definition.SetName = name;
        return this;
    }

    /// <summary>Sets the key, either one property (<c>d =&gt; d.Id</c>) or several (<c>d =&gt; new { d.A, d.B }</c>).</summary>
    /// <param name="key">The key selector.</param>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoTypeBuilder<TDto> HasKey<TKey>(Expression<Func<TDto, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Definition.Key = MemberExpressions.GetProperties(key, nameof(key));
        return this;
    }

    /// <summary>Sets the optimistic concurrency token sent back with every update and delete.</summary>
    /// <param name="token">The token property selector.</param>
    /// <typeparam name="TToken">The token type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoTypeBuilder<TDto> HasConcurrencyToken<TToken>(Expression<Func<TDto, TToken>> token)
    {
        ArgumentNullException.ThrowIfNull(token);
        Definition.ConcurrencyToken = MemberExpressions.GetProperty(token, nameof(token));
        return this;
    }

    /// <summary>Configures a scalar or primitive-collection property.</summary>
    /// <param name="property">The property selector.</param>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <returns>A builder for the property.</returns>
    public DtoPropertyBuilder Property<TProperty>(Expression<Func<TDto, TProperty>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new DtoPropertyBuilder(Definition.Property(MemberExpressions.GetProperty(property, nameof(property))));
    }

    /// <summary>Excludes a property from the model, so it's never sent, filtered or written.</summary>
    /// <param name="property">The property selector.</param>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoTypeBuilder<TDto> Ignore<TProperty>(Expression<Func<TDto, TProperty>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Definition.Ignored.Add(MemberExpressions.GetProperty(property, nameof(property)));
        return this;
    }

    /// <summary>Configures a reference navigation to another DTO type.</summary>
    /// <param name="navigation">The navigation property selector.</param>
    /// <typeparam name="TTarget">The target DTO type.</typeparam>
    /// <returns>A builder for the navigation.</returns>
    public DtoReferenceBuilder<TDto> HasOne<TTarget>(Expression<Func<TDto, TTarget?>> navigation)
        where TTarget : class, IDto
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var property = MemberExpressions.GetProperty(navigation, nameof(navigation));
        return new DtoReferenceBuilder<TDto>(Definition.Navigation(property, typeof(TTarget), isCollection: false));
    }

    /// <summary>Configures a collection navigation to another DTO type.</summary>
    /// <param name="navigation">The navigation property selector.</param>
    /// <typeparam name="TTarget">The target DTO type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoTypeBuilder<TDto> HasMany<TTarget>(Expression<Func<TDto, IEnumerable<TTarget>>> navigation)
        where TTarget : class, IDto
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var property = MemberExpressions.GetProperty(navigation, nameof(navigation));
        Definition.Navigation(property, typeof(TTarget), isCollection: true);
        return this;
    }

    /// <summary>Configures the pagination modes and page limits of the set.</summary>
    /// <param name="configure">Configures the pagination.</param>
    /// <returns>The same builder.</returns>
    public DtoTypeBuilder<TDto> HasPagination(Action<DtoPaginationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new DtoPaginationBuilder(Definition.Pagination));
        return this;
    }
}
