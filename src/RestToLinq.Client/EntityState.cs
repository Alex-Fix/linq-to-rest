namespace RestToLinq.Client;

/// <summary>The tracking state of a DTO.</summary>
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
