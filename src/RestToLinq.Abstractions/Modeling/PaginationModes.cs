namespace RestToLinq.Modeling;

/// <summary>The pagination modes a set accepts.</summary>
[Flags]
public enum PaginationModes
{
    /// <summary>No mode. Not valid for a set; used only as the empty value.</summary>
    None = 0,

    /// <summary>Offset pagination with skip and take.</summary>
    Offset = 1,

    /// <summary>Cursor (keyset) pagination with an opaque cursor and take.</summary>
    Cursor = 2,

    /// <summary>Both offset and cursor pagination.</summary>
    All = Offset | Cursor,
}
