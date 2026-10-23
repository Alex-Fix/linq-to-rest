using System.Text.Json;
using LinqToRest.Protocol;

namespace LinqToRest.Client.Materialization;

/// <summary>
/// Reads rows of a set from JSON through the bound delegates: no serializer metadata, no reflection. Keys the model
/// doesn't know are ignored, so a server can add members before a client regenerates.
/// </summary>
internal static class RowReader
{
    public static object Read(ClientSet set, JsonElement element, string location)
    {
        var row = set.Create();
        ReadInto(set, element, row, location, navigations: true);
        return row;
    }

    /// <summary>Sets the members present in <paramref name="element"/> on an existing row.</summary>
    public static void ReadInto(ClientSet set, JsonElement element, object row, string location, bool navigations)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Mismatch(set, null, location);
        }

        foreach (var member in element.EnumerateObject())
        {
            if (set.FindProperty(member.Name) is { } property)
            {
                object? value;
                try
                {
                    value = property.Read(member.Value, $"{location}/{member.Name}");
                }
                catch (ProtocolException)
                {
                    throw Mismatch(set, member.Name, $"{location}/{member.Name}");
                }

                property.Set(row, value);
            }
            else if (navigations && set.FindNavigation(member.Name) is { } navigation)
            {
                navigation.Set(row, ReadNavigation(navigation, member.Value, $"{location}/{member.Name}"));
            }
        }
    }

    private static object? ReadNavigation(ClientNavigation navigation, JsonElement element, string location)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return navigation.IsCollection ? navigation.NewList() : null;
        }

        if (!navigation.IsCollection)
        {
            return Read(navigation.Target, element, location);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Mismatch(navigation.Target, null, location);
        }

        var list = navigation.NewList();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            list.Add(Read(navigation.Target, item, $"{location}/{index++}"));
        }

        return list;
    }

    private static JsonException Mismatch(ClientSet set, string? member, string location) =>
        new($"The response's value at '{location}' doesn't fit {(member is null ? $"a row of the set '{set.SetName}'" : $"'{set.SetName}.{member}'")}.");
}
