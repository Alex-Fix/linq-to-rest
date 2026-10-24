using System.Text.Json;
using LinqToRest.Protocol;

namespace LinqToRest.Client;

/// <summary>
/// How the client builds and reads one value type (such as an address), with compile-time delegates instead of
/// reflection. Generated contexts build these and pass them to <see cref="SetBinding{T}.Value{TValue}"/>.
/// </summary>
public abstract class ValueBinding
{
    private protected ValueBinding(string typeName, Type clrType)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        TypeName = typeName;
        ClrType = clrType;
    }

    /// <summary>Gets the value type's name in the model document.</summary>
    public string TypeName { get; }

    /// <summary>Gets the class of the values.</summary>
    public Type ClrType { get; }

    /// <summary>Gets the members, in the order the factory takes their values.</summary>
    internal List<MemberBinding> Members { get; } = [];

    /// <summary>Builds a value from its members' values, in <see cref="Members"/> order.</summary>
    private protected abstract object Create(object?[] values);

    /// <summary>Reads a value (or null) from JSON: every member must be present; other keys are ignored.</summary>
    internal object? Read(JsonElement element, string location)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException(ErrorCodes.TypeMismatch, location);
        }

        var values = new object?[Members.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var member = Members[index];
            values[index] = element.TryGetProperty(member.Name, out var value)
                ? member.Read!(value, $"{location}/{member.Name}")
                : throw new ProtocolException(ErrorCodes.TypeMismatch, $"{location}/{member.Name}");
        }

        return Create(values);
    }
}

/// <summary>The binding of a value type to <typeparamref name="T"/>, usually an immutable record.</summary>
/// <typeparam name="T">The class of the values.</typeparam>
public sealed class ValueBinding<T> : ValueBinding
    where T : class
{
    private readonly Func<object?[], T> _create;

    /// <summary>Starts the binding of a value type.</summary>
    /// <param name="typeName">The value type's name in the model document.</param>
    /// <param name="create">
    /// Builds a value from its members' values, in the order the members are bound: <c>v =&gt; new Address { Street =
    /// (string)v[0]!, City = (string)v[1]! }</c>.
    /// </param>
    public ValueBinding(string typeName, Func<object?[], T> create)
        : base(typeName, typeof(T))
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
    }

    /// <summary>Binds a scalar or enum member.</summary>
    /// <param name="name">The wire name.</param>
    /// <param name="member">The CLR property name (queries name it in lambdas).</param>
    /// <param name="get">Reads the member.</param>
    /// <typeparam name="TValue">The member type.</typeparam>
    /// <returns>The same binding.</returns>
    public ValueBinding<T> Property<TValue>(string name, string member, Func<T, TValue> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        Members.Add(new MemberBinding(name, member, MemberKind.Property, typeof(TValue), null)
        {
            Get = value => get((T)value),
            Set = ReadOnly,
            Read = (element, location) => JsonValues.ReadScalar<TValue>(element, location),
        });
        return this;
    }

    /// <summary>Binds a member holding another value type.</summary>
    /// <param name="name">The wire name.</param>
    /// <param name="member">The CLR property name.</param>
    /// <param name="binding">The nested value type's binding.</param>
    /// <param name="get">Reads the member.</param>
    /// <typeparam name="TValue">The nested value type.</typeparam>
    /// <returns>The same binding.</returns>
    public ValueBinding<T> Value<TValue>(string name, string member, ValueBinding<TValue> binding, Func<T, TValue?> get)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(get);
        Members.Add(new MemberBinding(name, member, MemberKind.Value, typeof(TValue), null)
        {
            Get = value => get((T)value),
            Set = ReadOnly,
            Read = binding.Read,
            Value = binding,
        });
        return this;
    }

    private protected override object Create(object?[] values) => _create(values);

    // Values are built whole by the factory, never changed member by member.
    private static void ReadOnly(object value, object? member) => throw new InvalidOperationException("Members of a value are set by its factory.");
}
