using LinqToRest.Protocol;

namespace LinqToRest.Abstractions.UnitTests.Protocol;

public sealed class CursorTests
{
    private static readonly string Hash = Cursor.HashSort("products", [("price", true), ("id", false)]);
    private static readonly Type[] Types = [typeof(decimal), typeof(int)];

    [Fact]
    public void Encoded_cursor_decodes_to_the_same_values()
    {
        var cursor = Cursor.Encode(Hash, [19.99m, 1042]);

        Assert.Equal([19.99m, 1042], Cursor.Decode(cursor, Hash, Types, "/page/after"));
        Assert.DoesNotContain('=', cursor);
    }

    [Fact]
    public void Sort_hash_depends_on_set_members_and_directions()
    {
        Assert.NotEqual(Hash, Cursor.HashSort("orders", [("price", true), ("id", false)]));
        Assert.NotEqual(Hash, Cursor.HashSort("products", [("price", false), ("id", false)]));
        Assert.NotEqual(Hash, Cursor.HashSort("products", [("name", true), ("id", false)]));
        Assert.Equal(Hash, Cursor.HashSort("products", [("price", true), ("id", false)]));
    }

    public static readonly TheoryData<string> InvalidCursors =
    [
        "not base64!",
        "e30",
        Cursor.Encode(Cursor.HashSort("products", [("name", false)]), [19.99m, 1042]),
        Cursor.Encode(Hash, [19.99m]),
        Cursor.Encode(Hash, ["19.99", 1042]),
        Cursor.Encode(Hash, [19.99m, null]),
    ];

    [Theory]
    [MemberData(nameof(InvalidCursors))]
    public void Invalid_cursors_are_rejected(string cursor)
    {
        var error = Assert.Throws<ProtocolException>(() => Cursor.Decode(cursor, Hash, Types, "/page/after"));

        Assert.Equal((ErrorCodes.InvalidCursor, "/page/after"), (error.Code, error.Location));
    }
}
