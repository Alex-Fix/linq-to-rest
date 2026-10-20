namespace LinqToRest.Modeling;

/// <summary>The write operations a set allows through <c>$batch</c>. Sets are read-only by default.</summary>
[Flags]
public enum WriteOperations
{
    /// <summary>No writes: the set is read-only.</summary>
    None = 0,

    /// <summary>Inserting new rows.</summary>
    Insert = 1,

    /// <summary>Updating existing rows.</summary>
    Update = 2,

    /// <summary>Deleting rows.</summary>
    Delete = 4,

    /// <summary>Inserts, updates and deletes.</summary>
    All = Insert | Update | Delete,
}
