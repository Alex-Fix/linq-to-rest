using System.Collections;
using LinqToRest.Modeling;

namespace LinqToRest.Client.Tracking;

/// <summary>Identity map and snapshot-based change detection for the entities of one context.</summary>
internal sealed class ChangeTracker
{
    private readonly Dictionary<object, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EntityKey, Entry> _identity = [];

    public IEnumerable<Entry> Entries => _entries.Values;

    public Entry? Find(object entity) => _entries.GetValueOrDefault(entity);

    /// <summary>Returns the tracked instance for a queried row, tracking it (and its loaded navigations) if it's new.</summary>
    public object TrackQueried(object entity, ClientSet type)
    {
        var key = EntityKey.Of(entity, type);
        if (_identity.TryGetValue(key, out var existing))
        {
            return existing.Entity;
        }

        foreach (var navigation in type.Navigations)
        {
            var value = navigation.PropertyInfo.GetValue(entity);
            if (navigation.IsCollection && value is IList list)
            {
                for (var index = 0; index < list.Count; index++)
                {
                    list[index] = list[index] is { } item ? TrackQueried(item, navigation.Target) : null;
                }
            }
            else if (!navigation.IsCollection && value is not null)
            {
                navigation.PropertyInfo.SetValue(entity, TrackQueried(value, navigation.Target));
            }
        }

        var entry = new Entry(entity, type) { State = EntityState.Unchanged, Key = key };
        entry.TakeSnapshot();
        _entries.Add(entity, entry);
        _identity.Add(key, entry);
        return entity;
    }

    public void Add(object entity, ClientSet type)
    {
        RequireWrite(type, WriteOperations.Insert, "inserts");
        if (_entries.TryGetValue(entity, out var entry))
        {
            throw new InvalidOperationException($"This {type} is already tracked as {entry.State}.");
        }

        _entries.Add(entity, new Entry(entity, type) { State = EntityState.Added });
    }

    public Entry Attach(object entity, ClientSet type)
    {
        if (_entries.TryGetValue(entity, out var entry))
        {
            return entry;
        }

        var key = EntityKey.Of(entity, type);
        if (_identity.ContainsKey(key))
        {
            throw new InvalidOperationException($"Another {type} instance with the same key is already tracked.");
        }

        entry = new Entry(entity, type) { State = EntityState.Unchanged, Key = key };
        entry.TakeSnapshot();
        _entries.Add(entity, entry);
        _identity.Add(key, entry);
        return entry;
    }

    public void Update(object entity, ClientSet type)
    {
        RequireWrite(type, WriteOperations.Update, "updates");
        var entry = Attach(entity, type);
        foreach (var property in type.Properties.Where(property => property.IsWritable))
        {
            entry.Forced.Add(property);
        }
    }

    public void Remove(object entity, ClientSet type)
    {
        if (Find(entity) is { State: EntityState.Added } added)
        {
            Detach(added);
            return;
        }

        RequireWrite(type, WriteOperations.Delete, "deletes");
        Attach(entity, type).State = EntityState.Deleted;
    }

    /// <summary>Fails before anything is sent when the set doesn't allow the write (the server would refuse it).</summary>
    public static void RequireWrite(ClientSet type, WriteOperations operation, string description)
    {
        if (!type.Allows(operation))
        {
            throw new InvalidOperationException($"The set '{type.SetName}' doesn't allow {description}.");
        }
    }

    public void Detach(Entry entry)
    {
        _entries.Remove(entry.Entity);
        if (entry.Key is { } key)
        {
            _identity.Remove(key);
        }

        entry.State = EntityState.Detached;
    }

    /// <summary>Marks a saved entry unchanged with its new key and values.</summary>
    public void AcceptSaved(Entry entry)
    {
        if (entry.Key is null)
        {
            entry.Key = EntityKey.Of(entry.Entity, entry.Type);
            _identity[entry.Key.Value] = entry;
        }

        entry.State = EntityState.Unchanged;
        entry.Forced.Clear();
        entry.Modified.Clear();
        entry.References.Clear();
        entry.TakeSnapshot();
    }

    public void DetectChanges()
    {
        foreach (var entry in _entries.Values)
        {
            DetectChanges(entry);
        }
    }

    public void DetectChanges(Entry entry)
    {
        if (entry.State is EntityState.Deleted or EntityState.Detached)
        {
            return;
        }

        SyncForeignKeys(entry);
        if (entry.State == EntityState.Added)
        {
            return;
        }

        for (var index = 0; index < entry.Type.Navigations.Length; index++)
        {
            var navigation = entry.Type.Navigations[index];
            if (navigation.IsCollection && !Entry.SameItems(entry.Accessors.Navigations[index](entry.Entity), entry.NavigationSnapshot[index]))
            {
                throw new NotSupportedException($"Items were added to or removed from {navigation}. Set the foreign key of the child entity instead.");
            }
        }

        entry.Modified.Clear();
        for (var index = 0; index < entry.Type.Properties.Length; index++)
        {
            var property = entry.Type.Properties[index];
            if (property.IsWritable && (entry.Forced.Contains(property) || !Entry.SameValue(entry.Accessors.Properties[index](entry.Entity), entry.Snapshot[index])))
            {
                entry.Modified.Add(property);
            }
        }

        entry.State = entry.Modified.Count > 0 || entry.References.Count > 0 ? EntityState.Modified : EntityState.Unchanged;
    }

    /// <summary>Copies the key of a referenced entity into the foreign key, or records a ref to an added one.</summary>
    private void SyncForeignKeys(Entry entry)
    {
        for (var index = 0; index < entry.Type.Navigations.Length; index++)
        {
            var navigation = entry.Type.Navigations[index];
            if (navigation.IsCollection || navigation.ForeignKey is not { } foreignKey)
            {
                continue;
            }

            var target = entry.Accessors.Navigations[index](entry.Entity);
            var original = entry.NavigationSnapshot.Length > 0 ? entry.NavigationSnapshot[index] : null;
            if (target is null)
            {
                entry.References.Remove(foreignKey);
                if (original is not null && foreignKey.IsNullable)
                {
                    foreignKey.PropertyInfo.SetValue(entry.Entity, null);
                }

                continue;
            }

            if (ReferenceEquals(target, original) && !entry.References.ContainsKey(foreignKey))
            {
                continue;
            }

            var targetEntry = Find(target)
                ?? throw new InvalidOperationException($"{navigation} points at an entity this context doesn't track. Add or attach it first.");
            if (targetEntry.State == EntityState.Added)
            {
                entry.References[foreignKey] = targetEntry;
            }
            else
            {
                entry.References.Remove(foreignKey);
                foreignKey.PropertyInfo.SetValue(entry.Entity, targetEntry.Key!.Value.Values[0]);
            }
        }
    }
}

/// <summary>The tracking state of one entity.</summary>
internal sealed class Entry(object entity, ClientSet type)
{
    public object Entity { get; } = entity;

    public ClientSet Type { get; } = type;

    public Accessors Accessors { get; } = Accessors.For(type);

    public EntityState State { get; set; }

    public EntityKey? Key { get; set; }

    public object?[] Snapshot { get; private set; } = [];

    public object?[] NavigationSnapshot { get; private set; } = [];

    /// <summary>Gets the members marked modified explicitly (Update, IsModified = true).</summary>
    public HashSet<ClientProperty> Forced { get; } = [];

    public HashSet<ClientProperty> Modified { get; } = [];

    /// <summary>Gets foreign keys whose value is the key of an added entity, known only after the save.</summary>
    public Dictionary<ClientProperty, Entry> References { get; } = [];

    public void TakeSnapshot()
    {
        Snapshot = [.. Accessors.Properties.Select(get => Copy(get(Entity)))];
        NavigationSnapshot = [.. Accessors.Navigations.Select(get => Copy(get(Entity)))];
    }

    public object? Original(ClientProperty property) => Snapshot[IndexOf(property)];

    public void ResetSnapshot(ClientProperty property)
    {
        var index = IndexOf(property);
        Snapshot[index] = Copy(Accessors.Properties[index](Entity));
    }

    public static bool SameValue(object? current, object? snapshot) => current switch
    {
        byte[] bytes => snapshot is byte[] other && bytes.AsSpan().SequenceEqual(other),
        IEnumerable items and not string => SameItems(items, snapshot),
        _ => Equals(current, snapshot),
    };

    public static bool SameItems(object? current, object? snapshot) =>
        current is IEnumerable items && snapshot is object?[] copy
            ? items.Cast<object?>().SequenceEqual(copy)
            : current is null && snapshot is null;

    private int IndexOf(ClientProperty property)
    {
        for (var index = 0; index < Type.Properties.Length; index++)
        {
            if (Type.Properties[index] == property)
            {
                return index;
            }
        }

        throw new ArgumentException($"{property} doesn't belong to {Type}.", nameof(property));
    }

    private static object? Copy(object? value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        IEnumerable items and not string => items.Cast<object?>().ToArray(),
        _ => value,
    };
}

/// <summary>An entity type and its key values.</summary>
internal readonly struct EntityKey(ClientSet type, object?[] values) : IEquatable<EntityKey>
{
    public ClientSet Type { get; } = type;

    public object?[] Values { get; } = values;

    public static EntityKey Of(object entity, ClientSet type) =>
        new(type, [.. Accessors.For(type).Key.Select(get => get(entity))]);

    public bool Equals(EntityKey other) => Type == other.Type && Values.SequenceEqual(other.Values);

    public override bool Equals(object? obj) => obj is EntityKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        foreach (var value in Values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
