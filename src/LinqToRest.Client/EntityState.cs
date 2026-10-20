namespace LinqToRest.Client;

/// <summary>The tracking state of an entity.</summary>
public enum EntityState
{
    /// <summary>Not tracked.</summary>
    Detached,

    /// <summary>Tracked with no changes.</summary>
    Unchanged,

    /// <summary>New; inserted on save.</summary>
    Added,

    /// <summary>Changed; updated on save.</summary>
    Modified,

    /// <summary>Removed; deleted on save.</summary>
    Deleted,
}
