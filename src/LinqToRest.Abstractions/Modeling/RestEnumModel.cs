namespace LinqToRest.Modeling;

/// <summary>An enum used by properties of a <see cref="RestModel"/>. Values travel as member names.</summary>
public sealed class RestEnumModel
{
    /// <summary>Gets the type name, for example <c>ProductStatus</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the member names, in declaration order.</summary>
    public required IReadOnlyList<string> Members
    {
        get;
        init => field = [.. value];
    } = [];

    /// <inheritdoc/>
    public override string ToString() => Name;
}
