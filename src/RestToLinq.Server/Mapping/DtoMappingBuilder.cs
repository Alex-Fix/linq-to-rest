using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RestToLinq.Server.Internal;

namespace RestToLinq.Server.Mapping;

/// <summary>Configures how a DTO is projected from its entity.</summary>
/// <typeparam name="TDto">The DTO type.</typeparam>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class DtoMappingBuilder<TDto, TEntity>
    where TDto : class, IDto
    where TEntity : class
{
    internal DtoMappingBuilder()
    {
    }

    internal LambdaExpression? Source { get; private set; }

    internal Type? SourceContextType { get; private set; }

    internal Expression<Func<TEntity, TDto>>? Projection { get; private set; }

    internal List<NavigationDefinition> Navigations { get; } = [];

    /// <summary>
    /// Sets the query rows come from, for example to apply EF Core query filters or a default include.
    /// Default: <c>db.Set&lt;TEntity&gt;()</c>.
    /// </summary>
    /// <param name="source">The source query.</param>
    /// <typeparam name="TContext">The DbContext type registered with <c>UseDbContext</c>.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoMappingBuilder<TDto, TEntity> FromSource<TContext>(Expression<Func<TContext, IQueryable<TEntity>>> source)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        SourceContextType = typeof(TContext);
        return this;
    }

    /// <summary>
    /// Sets the default projection: a member initializer assigning every scalar and primitive-collection member of
    /// the DTO. Writable members must be assigned directly from an entity member (<c>Name = e.Name</c>); everything
    /// else must be read-only in the DTO model.
    /// </summary>
    /// <param name="projection">The projection.</param>
    /// <returns>The same builder.</returns>
    public DtoMappingBuilder<TDto, TEntity> Project(Expression<Func<TEntity, TDto>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        Projection = projection;
        return this;
    }

    /// <summary>Maps a reference navigation of the DTO to a reference navigation of the entity.</summary>
    /// <param name="navigation">The DTO navigation.</param>
    /// <param name="entityNavigation">The entity navigation; a direct member access allows inserts to reference it.</param>
    /// <typeparam name="TTargetDto">The target DTO type.</typeparam>
    /// <typeparam name="TTargetEntity">The target entity type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoMappingBuilder<TDto, TEntity> HasOne<TTargetDto, TTargetEntity>(
        Expression<Func<TDto, TTargetDto?>> navigation,
        Expression<Func<TEntity, TTargetEntity?>> entityNavigation)
        where TTargetDto : class, IDto
        where TTargetEntity : class
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(entityNavigation);
        Navigations.Add(new NavigationDefinition(MemberAccess.GetProperty(navigation, nameof(navigation)), entityNavigation, typeof(TTargetDto), typeof(TTargetEntity), IsCollection: false));
        return this;
    }

    /// <summary>Maps a collection navigation of the DTO to a collection navigation of the entity.</summary>
    /// <param name="navigation">The DTO navigation.</param>
    /// <param name="entityNavigation">The entity navigation.</param>
    /// <typeparam name="TTargetDto">The target DTO type.</typeparam>
    /// <typeparam name="TTargetEntity">The target entity type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoMappingBuilder<TDto, TEntity> HasMany<TTargetDto, TTargetEntity>(
        Expression<Func<TDto, IEnumerable<TTargetDto>>> navigation,
        Expression<Func<TEntity, IEnumerable<TTargetEntity>>> entityNavigation)
        where TTargetDto : class, IDto
        where TTargetEntity : class
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(entityNavigation);
        Navigations.Add(new NavigationDefinition(MemberAccess.GetProperty(navigation, nameof(navigation)), entityNavigation, typeof(TTargetDto), typeof(TTargetEntity), IsCollection: true));
        return this;
    }
}
