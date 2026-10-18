namespace RestToLinq.Modeling;

/// <summary>Configures one scalar or primitive-collection property of a DTO.</summary>
public sealed class DtoPropertyBuilder
{
    private readonly PropertyDefinition _definition;

    internal DtoPropertyBuilder(PropertyDefinition definition) => _definition = definition;

    /// <summary>Marks the property as read-only: clients can read and filter it but never write it.</summary>
    /// <returns>The same builder.</returns>
    public DtoPropertyBuilder IsReadOnly()
    {
        _definition.IsReadOnly = true;
        return this;
    }

    /// <summary>Allows clients to sort by the property. Keys are always sortable.</summary>
    /// <returns>The same builder.</returns>
    public DtoPropertyBuilder IsSortable()
    {
        _definition.IsSortable = true;
        return this;
    }

    /// <summary>Prevents clients from filtering by the property (it can still be selected).</summary>
    /// <returns>The same builder.</returns>
    public DtoPropertyBuilder IsNotFilterable()
    {
        _definition.IsNotFilterable = true;
        return this;
    }
}
