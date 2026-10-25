using LinqToRest.Protocol;

namespace LinqToRest.Client.Tracking;

/// <summary>
/// The model's built-in rules, checked before a save is sent, exactly as the server checks them: inserts check every
/// writable member, updates the members they send. Members set through a reference to a new row are skipped; their
/// value only exists once that row is saved.
/// </summary>
internal static class LocalValidation
{
    public static List<RestValidationError> Check(IEnumerable<Entry> entries)
    {
        var errors = new List<RestValidationError>();
        foreach (var entry in entries.Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            IEnumerable<ClientProperty> sent = entry.State == EntityState.Added ? entry.Type.Properties.Where(property => property.IsWritable) : entry.Modified;
            errors.AddRange(Check(entry.Entity, sent.Where(property => !entry.References.ContainsKey(property))));
        }

        return errors;
    }

    /// <summary>
    /// Checks members in model order. A value (an address) is required when it's non-nullable; its members' rules are
    /// reported by dotted CLR path, such as <c>Address.City</c>.
    /// </summary>
    public static IEnumerable<RestValidationError> Check(object entity, IEnumerable<ClientProperty> properties)
    {
        foreach (var property in properties)
        {
            var value = property.Get(entity);
            if (property.ValueType is not { } valueType)
            {
                if (property.Rules?.Check(value) is { } code)
                {
                    yield return new RestValidationError(entity, property.Name, code);
                }
            }
            else if (value is null)
            {
                if (!property.IsNullable)
                {
                    yield return new RestValidationError(entity, property.Name, ErrorCodes.Required);
                }
            }
            else
            {
                foreach (var error in CheckValue(entity, property.Name, valueType, value))
                {
                    yield return error;
                }
            }
        }
    }

    private static IEnumerable<RestValidationError> CheckValue(object entity, string path, ClientValueType valueType, object value)
    {
        foreach (var member in valueType.Properties)
        {
            var memberValue = member.Get(value);
            if (member.ValueType is { } nested)
            {
                if (memberValue is not null)
                {
                    foreach (var error in CheckValue(entity, $"{path}.{member.Name}", nested, memberValue))
                    {
                        yield return error;
                    }
                }
            }
            else if (member.Rules?.Check(memberValue) is { } code)
            {
                yield return new RestValidationError(entity, $"{path}.{member.Name}", code);
            }
        }
    }
}
