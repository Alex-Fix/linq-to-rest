namespace LinqToRest.Modeling;

/// <summary>What unlinking does to an item of a one-to-many collection, as its EF Core relationship says.</summary>
public enum RestUnlinkBehavior
{
    /// <summary>The item's foreign key is required, so the orphaned item is deleted (<c>delete</c>).</summary>
    Delete,

    /// <summary>The item's foreign key is optional, so it's set to null (<c>detach</c>).</summary>
    Detach,
}
