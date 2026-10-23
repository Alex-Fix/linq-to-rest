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

    public static IEnumerable<RestValidationError> Check(object entity, IEnumerable<ClientProperty> properties) =>
        properties
            .Select(property => (property, Code: property.Rules?.Check(property.Get(entity))))
            .Where(result => result.Code is not null)
            .Select(result => new RestValidationError(entity, result.property.Name, result.Code!));
}
