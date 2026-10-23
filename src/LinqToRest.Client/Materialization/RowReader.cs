using System.Text.Json;
using LinqToRest.Protocol;

namespace LinqToRest.Client.Materialization;

/// <summary>
/// Reads rows of a set from JSON through the bound delegates: no serializer metadata, no reflection. Keys the model
/// doesn't know are ignored, so a server can add members before a client regenerates.
/// </summary>
internal static class RowReader
{
    public static object Read(ClientSet set, JsonElement element, RowPointer pointer)
    {
        var row = set.Create();
        ReadInto(set, element, row, pointer, navigations: true);
        return row;
    }

    /// <summary>Sets the members present in <paramref name="element"/> on an existing row.</summary>
    public static void ReadInto(ClientSet set, JsonElement element, object row, RowPointer pointer, bool navigations)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Mismatch(set, null, pointer.ToString());
        }

        var next = 0;
        foreach (var member in element.EnumerateObject())
        {
            var index = set.Match(member, next);
            if (index < 0)
            {
                continue;
            }

            next = index + 1;
            var (_, property, navigation) = set.Members[index];
            if (property is not null)
            {
                try
                {
                    // The reader's own pointer is replaced below: pointers are only built for errors.
                    property.ReadInto(row, member.Value, pointer.Parent);
                }
                catch (ProtocolException)
                {
                    throw Mismatch(set, property.WireName, $"{pointer}/{property.WireName}");
                }
            }
            else if (navigations)
            {
                navigation!.Set(row, ReadNavigation(navigation, member.Value, $"{pointer}/{navigation.WireName}"));
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
            return Read(navigation.Target, element, new RowPointer(location));
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Mismatch(navigation.Target, null, location);
        }

        var list = navigation.NewList();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            list.Add(Read(navigation.Target, item, new RowPointer(location, index++)));
        }

        return list;
    }

    private static JsonException Mismatch(ClientSet set, string? member, string location) =>
        new($"The response's value at '{location}' doesn't fit {(member is null ? $"a row of the set '{set.SetName}'" : $"'{set.SetName}.{member}'")}.");
}

/// <summary>The JSON pointer of a row (<paramref name="Parent"/>, or its item <paramref name="Index"/>), as text only for errors.</summary>
internal readonly record struct RowPointer(string Parent, int Index = -1)
{
    public override string ToString() => Index < 0 ? Parent : $"{Parent}/{Index}";
}
