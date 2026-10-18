using System.Text.Json;

namespace RestToLinq.Server.IntegrationTests;

internal static class JsonAssert
{
    public static int[] Ids(JsonElement result) =>
        [.. result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32())];

    public static JsonElement Single(JsonElement result) => Assert.Single(result.GetProperty("items").EnumerateArray());

    public static void Problem(JsonElement problem, string code, string location)
    {
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(location, problem.GetProperty("pointer").GetString());
        Assert.Equal($"urn:resttolinq:error:{code}", problem.GetProperty("type").GetString());
    }

    public static void Equal(string expected, JsonElement actual)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual), $"Expected {expected}{Environment.NewLine}Actual   {actual.GetRawText()}");
    }
}
