namespace LinqToRest.Modeling;

/// <summary>A navigation from a <see cref="RestSetModel"/> to another set.</summary>
public sealed class RestNavigationModel
{
    /// <summary>Gets the wire name, for example <c>category</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the name of the target set.</summary>
    public required string Target { get; init; }

    /// <summary>Gets whether the navigation is a collection (one-to-many).</summary>
    public bool IsCollection { get; init; }

    /// <summary>Gets whether a reference navigation may be null.</summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Gets the names of the declaring set's properties that hold the target's key, in key order. Empty when the
    /// reference has no foreign key exposed; always empty for collections.
    /// </summary>
    public IReadOnlyList<string> ForeignKey
    {
        get;
        init => field = [.. value];
    } = [];

    /// <inheritdoc/>
    public override string ToString() => Name;
}
