namespace LinqToRest.Modeling;

/// <summary>How a collection navigation's membership changes, when it allows links.</summary>
public enum RestLinkKind
{
    /// <summary>Each item belongs to one parent: linking sets the item's foreign key (<c>oneToMany</c>).</summary>
    OneToMany,

    /// <summary>Items and parents are joined through a join table: linking adds a join row (<c>manyToMany</c>).</summary>
    ManyToMany,
}
