using System.Linq.Expressions;
using System.Reflection;
using LinqToRest.Modeling;
using LinqToRest.Server.Internal;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.Mapping;

/// <summary>A registered <see cref="IDtoMapping{TDto, TEntity}"/>, turned into a validated <see cref="SetMapping"/>.</summary>
internal abstract class MappingRegistration
{
    public abstract Type DtoType { get; }

    protected abstract SetMapping Create(DtoModel model, Type contextType, out IReadOnlyList<NavigationDefinition> navigations);

    public static ServerModel CreateModel(DtoModel model, IReadOnlyList<MappingRegistration> registrations, Type? contextType, Protocol.QueryLimits limits)
    {
        if (contextType is null)
        {
            throw new InvalidOperationException("LinqToRest needs a DbContext. Call UseDbContext<TContext>().");
        }

        var mappings = new Dictionary<DtoType, SetMapping>();
        var navigations = new Dictionary<SetMapping, IReadOnlyList<NavigationDefinition>>();
        foreach (var registration in registrations)
        {
            var mapping = registration.Create(model, contextType, out var definitions);
            if (!mappings.TryAdd(mapping.DtoType, mapping))
            {
                throw new InvalidOperationException($"DTO {mapping.DtoType} is mapped twice.");
            }

            navigations.Add(mapping, definitions);
        }

        foreach (var type in model.Types.Where(type => !mappings.ContainsKey(type)))
        {
            throw new InvalidOperationException($"DTO {type} has no mapping. Add an IDtoMapping<{type.ClrType.Name}, TEntity>.");
        }

        foreach (var (mapping, definitions) in navigations)
        {
            mapping.Navigations = LinkNavigations(mapping, definitions, mappings);
        }

        return new ServerModel(model, mappings, contextType, limits);
    }

    private static Dictionary<DtoNavigation, NavigationMapping> LinkNavigations(
        SetMapping mapping,
        IReadOnlyList<NavigationDefinition> definitions,
        Dictionary<DtoType, SetMapping> mappings)
    {
        var linked = new Dictionary<DtoNavigation, NavigationMapping>();
        foreach (var definition in definitions)
        {
            var navigation = mapping.DtoType.Navigations.FirstOrDefault(navigation => navigation.Name == definition.DtoProperty.Name)
                ?? throw Invalid(mapping, $"maps '{definition.DtoProperty.Name}', which isn't a navigation in the DTO model");
            if (navigation.IsCollection != definition.IsCollection)
            {
                throw Invalid(mapping, $"maps '{navigation.Name}' with {(definition.IsCollection ? "HasMany" : "HasOne")}, but it's a {(navigation.IsCollection ? "collection" : "reference")}");
            }

            if (navigation.Target.ClrType != definition.TargetDto)
            {
                throw Invalid(mapping, $"maps '{navigation.Name}' as a navigation to {definition.TargetDto.Name}, but it targets {navigation.Target}");
            }

            var target = mappings[navigation.Target];
            if (target.EntityType != definition.TargetEntity)
            {
                throw Invalid(mapping, $"maps '{navigation.Name}' to {definition.TargetEntity.Name}, but {navigation.Target} is mapped from {target.EntityType.Name}");
            }

            var parameter = definition.Entity.Parameters[0];
            PropertyInfo? property = MemberAccess.TryGetProperty(definition.Entity.Body, parameter, out var found) ? found : null;
            if (!linked.TryAdd(navigation, new NavigationMapping(definition.Entity, property, target)))
            {
                throw Invalid(mapping, $"maps '{navigation.Name}' twice");
            }
        }

        foreach (var navigation in mapping.DtoType.Navigations.Where(navigation => !linked.ContainsKey(navigation)))
        {
            throw Invalid(mapping, $"doesn't map the navigation '{navigation.Name}'. Call {(navigation.IsCollection ? "HasMany" : "HasOne")}(...)");
        }

        return linked;
    }

    internal static InvalidOperationException Invalid(SetMapping mapping, string problem) =>
        new($"The mapping of DTO {mapping.DtoType} {problem}.");
}

internal sealed class MappingRegistration<TDto, TEntity>(IDtoMapping<TDto, TEntity> mapping) : MappingRegistration
    where TDto : class, IDto
    where TEntity : class
{
    public override Type DtoType => typeof(TDto);

    protected override SetMapping Create(DtoModel model, Type contextType, out IReadOnlyList<NavigationDefinition> navigations)
    {
        var builder = new DtoMappingBuilder<TDto, TEntity>();
        mapping.Configure(builder);
        var type = model.FindType(typeof(TDto))
            ?? throw new InvalidOperationException($"DTO {typeof(TDto).Name} is mapped but isn't part of the DTO model.");
        var projection = builder.Projection
            ?? throw new InvalidOperationException($"The mapping of DTO {type} has no projection. Call Project(...).");
        if (projection.Body is not MemberInitExpression { NewExpression.Arguments.Count: 0 } init)
        {
            throw new InvalidOperationException($"The mapping of DTO {type} must project with a member initializer: e => new {type} {{ ... }}.");
        }

        var parameter = projection.Parameters[0];
        var bindings = new Dictionary<DtoProperty, LambdaExpression>();
        var entityProperties = new Dictionary<DtoProperty, PropertyInfo>();
        foreach (var binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                throw Problem(type, $"must assign '{binding.Member.Name}' directly, without nested initializers");
            }

            var property = type.Properties.FirstOrDefault(property => property.Name == assignment.Member.Name);
            if (property is null)
            {
                throw Problem(type, type.Navigations.Any(navigation => navigation.Name == assignment.Member.Name)
                    ? $"assigns the navigation '{assignment.Member.Name}' in the projection. Map it with HasOne/HasMany instead"
                    : $"assigns '{assignment.Member.Name}', which isn't part of the DTO model");
            }

            bindings.Add(property, Expression.Lambda(assignment.Expression, parameter));
            if (assignment.Expression is MemberExpression { Member: PropertyInfo entityProperty } member && member.Expression == parameter)
            {
                entityProperties.Add(property, entityProperty);
            }
        }

        foreach (var property in type.Properties)
        {
            if (!bindings.ContainsKey(property))
            {
                throw Problem(type, $"doesn't assign '{property.Name}' in the projection");
            }

            ValidateDirectBinding(type, property, entityProperties.GetValueOrDefault(property));
        }

        navigations = builder.Navigations;
        return new SetMapping<TEntity>(type, CreateSource(builder, contextType, type))
        {
            Bindings = bindings,
            EntityProperties = entityProperties,
        };
    }

    private static void ValidateDirectBinding(DtoType type, DtoProperty property, PropertyInfo? entityProperty)
    {
        var kind = property.IsKey ? "key" : property.IsConcurrencyToken ? "concurrency token" : property.IsWritable ? "writable member" : null;
        if (kind is null)
        {
            return;
        }

        if (entityProperty is null || entityProperty.PropertyType != property.ClrType)
        {
            throw Problem(type, $"computes the {kind} '{property.Name}'. Assign it directly from an entity property of type {property.ClrType.Name}{(property.IsWritable ? ", or mark it IsReadOnly() in the DTO model" : "")}");
        }

        if (property.IsWritable && entityProperty.SetMethod?.IsPublic != true)
        {
            throw Problem(type, $"binds the writable member '{property.Name}' to {typeof(TEntity).Name}.{entityProperty.Name}, which has no public setter. Mark it IsReadOnly() in the DTO model");
        }
    }

    private static Func<DbContext, IQueryable<TEntity>> CreateSource(DtoMappingBuilder<TDto, TEntity> builder, Type contextType, DtoType type)
    {
        var db = Expression.Parameter(typeof(DbContext), "db");
        if (builder.Source is null)
        {
            return context => context.Set<TEntity>();
        }

        if (!builder.SourceContextType!.IsAssignableFrom(contextType))
        {
            throw Problem(type, $"reads from {builder.SourceContextType.Name}, but the registered DbContext is {contextType.Name}");
        }

        // Compiled once at startup from the developer's own expression.
        var body = ExpressionHelpers.Inline(builder.Source, Expression.Convert(db, builder.SourceContextType));
        return Expression.Lambda<Func<DbContext, IQueryable<TEntity>>>(body, db).Compile();
    }

    private static InvalidOperationException Problem(DtoType type, string problem) => new($"The mapping of DTO {type} {problem}.");
}
