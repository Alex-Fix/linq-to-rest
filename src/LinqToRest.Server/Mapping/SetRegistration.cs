using System.Linq.Expressions;
using LinqToRest.Modeling;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Sets;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.Mapping;

/// <summary>A registered set configuration; runs it and creates the typed <see cref="SetMapping"/>.</summary>
internal abstract class SetRegistration
{
    public abstract SetDefinition Configure();

    public abstract SetMapping Create(RestSetModel model, SetDefinition definition, Type contextType);
}

internal sealed class SetRegistration<TEntity>(Action<EntitySetBuilder<TEntity>> configure) : SetRegistration
    where TEntity : class
{
    public override SetDefinition Configure()
    {
        var builder = new EntitySetBuilder<TEntity>();
        configure(builder);
        return builder.Definition;
    }

    public override SetMapping Create(RestSetModel model, SetDefinition definition, Type contextType) =>
        new SetMapping<TEntity>(model, CreateSource(model, definition, contextType));

    private static Func<DbContext, IQueryable<TEntity>> CreateSource(RestSetModel model, SetDefinition definition, Type contextType)
    {
        if (definition.Source is null)
        {
            return context => context.Set<TEntity>();
        }

        if (!definition.SourceContextType!.IsAssignableFrom(contextType))
        {
            throw ServerModelBuilder.Invalid(model.Name, typeof(TEntity), $"reads from {definition.SourceContextType.Name}, but the registered DbContext is {contextType.Name}");
        }

        // Compiled once at startup from the developer's own expression.
        var db = Expression.Parameter(typeof(DbContext), "db");
        var body = ExpressionHelpers.Inline(definition.Source, Expression.Convert(db, definition.SourceContextType));
        return Expression.Lambda<Func<DbContext, IQueryable<TEntity>>>(body, db).Compile();
    }
}
