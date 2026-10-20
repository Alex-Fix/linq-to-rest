using System.Linq.Expressions;
using LinqToRest.Client.Tracking;

namespace LinqToRest.Client;

/// <summary>The tracking information of one entity.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class EntityEntry<TEntity>
    where TEntity : class
{
    private readonly ChangeTracker _tracker;
    private readonly ClientSet _type;

    internal EntityEntry(ChangeTracker tracker, ClientSet type, TEntity entity)
    {
        _tracker = tracker;
        _type = type;
        Entity = entity;
    }

    /// <summary>Gets the entity.</summary>
    public TEntity Entity { get; }

    /// <summary>Gets the current state, detecting changes first.</summary>
    public EntityState State
    {
        get
        {
            if (_tracker.Find(Entity) is not { } entry)
            {
                return EntityState.Detached;
            }

            _tracker.DetectChanges(entry);
            return entry.State;
        }
    }

    /// <summary>Gets the tracking information of one member.</summary>
    /// <param name="property">The member selector.</param>
    /// <typeparam name="TProperty">The member type.</typeparam>
    /// <returns>The member entry.</returns>
    public PropertyEntry Property<TProperty>(Expression<Func<TEntity, TProperty>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var name = property.Body is MemberExpression member && member.Expression == property.Parameters[0]
            ? member.Member.Name
            : throw new ArgumentException("The expression must be a direct member access, such as 'd => d.Price'.", nameof(property));
        var dtoProperty = _type.Properties.FirstOrDefault(candidate => candidate.Name == name)
            ?? throw new ArgumentException($"{_type}.{name} isn't a scalar member of the model.", nameof(property));
        return new PropertyEntry(_tracker, Entity, dtoProperty);
    }
}

/// <summary>The tracking information of one entity member.</summary>
public sealed class PropertyEntry
{
    private readonly ChangeTracker _tracker;
    private readonly object _entity;
    private readonly ClientProperty _property;

    internal PropertyEntry(ChangeTracker tracker, object entity, ClientProperty property)
    {
        _tracker = tracker;
        _entity = entity;
        _property = property;
    }

    /// <summary>
    /// Gets or sets whether the member is sent with the next save. Setting <see langword="false"/> accepts the
    /// current value as unchanged.
    /// </summary>
    public bool IsModified
    {
        get
        {
            if (_tracker.Find(_entity) is not { } entry)
            {
                return false;
            }

            _tracker.DetectChanges(entry);
            return entry.Modified.Contains(_property);
        }

        set
        {
            var entry = _tracker.Find(_entity) ?? throw new InvalidOperationException("The entity isn't tracked.");
            if (!_property.IsWritable)
            {
                throw new InvalidOperationException($"{_property} isn't writable.");
            }

            if (value)
            {
                entry.Forced.Add(_property);
            }
            else
            {
                entry.Forced.Remove(_property);
                entry.ResetSnapshot(_property);
            }
        }
    }
}
