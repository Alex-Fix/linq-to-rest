namespace LinqToRest.Server.Policies;

/// <summary>An operation a policy can allow or deny.</summary>
public enum SetOperation
{
    /// <summary>A query.</summary>
    Read,

    /// <summary>A batch insert.</summary>
    Insert,

    /// <summary>A batch update.</summary>
    Update,

    /// <summary>A batch delete.</summary>
    Delete,
}
