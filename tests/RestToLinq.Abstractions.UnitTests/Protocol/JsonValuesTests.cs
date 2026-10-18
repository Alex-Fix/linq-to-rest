using System.Text.Json;
using RestToLinq.Protocol;

namespace RestToLinq.Abstractions.UnitTests.Protocol;

public sealed class JsonValuesTests
{
    public static readonly TheoryData<object, string> Values = new()
    {
        { true, "true" },
        { (byte)7, "7" },
        { (short)-7, "-7" },
        { 42, "42" },
        { 9_000_000_000L, "9000000000" },
        { 1.5f, "1.5" },
        { 2.25d, "2.25" },
        { 19.99m, "19.99" },
        { "text", "\"text\"" },
        { new Guid("6a2f41a3-c54c-fce8-32d2-0324e1c32e22"), "\"6a2f41a3-c54c-fce8-32d2-0324e1c32e22\"" },
        { new DateTime(2026, 10, 18, 13, 44, 0, DateTimeKind.Utc), "\"2026-10-18T13:44:00Z\"" },
        { new DateTimeOffset(2026, 10, 18, 13, 44, 0, TimeSpan.FromHours(2)), "\"2026-10-18T13:44:00+02:00\"" },
        { new DateOnly(2026, 10, 18), "\"2026-10-18\"" },
        { new TimeOnly(13, 44, 5), "\"13:44:05.0000000\"" },
        { TimeSpan.FromMinutes(90), "\"01:30:00\"" },
        { new byte[] { 1, 2, 3 }, "\"AQID\"" },
        { DayOfWeek.Friday, "\"Friday\"" },
        { new List<string> { "a", "b" }, "[\"a\",\"b\"]" },
        { new[] { 1, 2 }, "[1,2]" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Writes_and_reads_back_every_supported_type(object value, string json)
    {
        var element = JsonValues.ToElement(value);

        Assert.Equal(json, element.GetRawText());
        Assert.Equal(value, JsonValues.Read(element, value.GetType(), "/x"));
    }

    [Fact]
    public void Null_reads_into_nullable_types_only()
    {
        var element = JsonValues.ToElement(null);

        Assert.Null(JsonValues.Read(element, typeof(int?), "/x"));
        Assert.Null(JsonValues.Read(element, typeof(string), "/x"));
        AssertMismatch(element, typeof(int));
    }

    [Theory]
    [InlineData("\"42\"", typeof(int))]
    [InlineData("42.5", typeof(int))]
    [InlineData("300", typeof(byte))]
    [InlineData("42", typeof(string))]
    [InlineData("1", typeof(bool))]
    [InlineData("1", typeof(DayOfWeek))]
    [InlineData("\"1\"", typeof(DayOfWeek))]
    [InlineData("\"friday\"", typeof(DayOfWeek))]
    [InlineData("\"Friday, Monday\"", typeof(DayOfWeek))]
    [InlineData("\"2026-18-10\"", typeof(DateOnly))]
    [InlineData("\"1.02:00\"", typeof(DateOnly))]
    [InlineData("\"not a guid\"", typeof(Guid))]
    [InlineData("1", typeof(List<int>))]
    public void Rejects_values_that_do_not_fit_the_type(string json, Type type)
    {
        using var document = JsonDocument.Parse(json);

        AssertMismatch(document.RootElement, type);
    }

    [Fact]
    public void Collection_mismatch_points_at_the_item()
    {
        using var document = JsonDocument.Parse("[1,\"a\"]");

        var error = Assert.Throws<ProtocolException>(() => JsonValues.Read(document.RootElement, typeof(int[]), "/values/ids"));

        Assert.Equal("/values/ids/1", error.Location);
    }

    [Fact]
    public void Flags_enums_read_combined_names()
    {
        using var document = JsonDocument.Parse("\"Read, Write\"");

        Assert.Equal(FileAccess.ReadWrite, JsonValues.Read(document.RootElement, typeof(FileAccess), "/x"));
    }

    private static void AssertMismatch(JsonElement element, Type type)
    {
        var error = Assert.Throws<ProtocolException>(() => JsonValues.Read(element, type, "/x"));

        Assert.Equal((ErrorCodes.TypeMismatch, "/x"), (error.Code, error.Location));
    }
}
