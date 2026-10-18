namespace RestToLinq.Modeling;

/// <summary>The immutable, validated DTO model shared by RestToLinq clients and servers.</summary>
public sealed class DtoModel
{
    private readonly Dictionary<Type, DtoType> _byClrType;
    private readonly Dictionary<string, DtoType> _bySet;

    internal DtoModel(IReadOnlyList<DtoType> types)
    {
        Types = types;
        _byClrType = types.ToDictionary(type => type.ClrType);
        _bySet = types.ToDictionary(type => type.SetName, StringComparer.Ordinal);
    }

    /// <summary>Gets every DTO type of the model.</summary>
    public IReadOnlyList<DtoType> Types { get; }

    /// <summary>Finds the DTO type for a CLR type.</summary>
    /// <param name="clrType">The DTO CLR type.</param>
    /// <returns>The DTO type, or <see langword="null"/> when it isn't part of the model.</returns>
    public DtoType? FindType(Type clrType) => _byClrType.GetValueOrDefault(clrType);

    /// <summary>Finds the DTO type exposed by a set.</summary>
    /// <param name="setName">The set name (case-sensitive).</param>
    /// <returns>The DTO type, or <see langword="null"/> when no set has that name.</returns>
    public DtoType? FindSet(string setName) => _bySet.GetValueOrDefault(setName);
}
