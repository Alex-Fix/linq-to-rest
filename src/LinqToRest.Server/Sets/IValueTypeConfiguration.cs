namespace LinqToRest.Server.Sets;

/// <summary>
/// Exposes a value type: an EF Core complex type or owned type (<c>OwnsOne</c>, also with <c>ToJson()</c>) that sets use
/// with <c>Value(...)</c>, such as an address. Only the members it configures exist on the wire, wherever the type is
/// used; values are written as a whole.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IValueTypeConfiguration<TValue>
    where TValue : class
{
    /// <summary>Configures the value type.</summary>
    /// <param name="builder">The value type builder.</param>
    void Configure(ValueTypeBuilder<TValue> builder);
}
