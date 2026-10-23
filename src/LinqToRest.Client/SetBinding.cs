using System.Collections;
using System.Text.Json;
using LinqToRest.Protocol;

namespace LinqToRest.Client;

/// <summary>
/// How the client creates, reads and writes the class of one set, with compile-time delegates instead of reflection.
/// Generated contexts build these; pass them to <see cref="ClientModel.Bind(LinqToRest.Modeling.RestModel, SetBinding[])"/>.
/// </summary>
public abstract class SetBinding
{
    private protected SetBinding(string setName, Type clrType, Func<object> create)
    {
        ArgumentException.ThrowIfNullOrEmpty(setName);
        SetName = setName;
        ClrType = clrType;
        Create = create;
    }

    /// <summary>Gets the set name.</summary>
    public string SetName { get; }

    /// <summary>Gets the class of the set's rows.</summary>
    public Type ClrType { get; }

    internal Func<object> Create { get; }

    internal List<MemberBinding> Members { get; } = [];
}

/// <summary>The binding of one set to <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The class of the set's rows.</typeparam>
public sealed class SetBinding<T> : SetBinding
    where T : class
{
    /// <summary>Starts the binding of a set.</summary>
    /// <param name="setName">The set name in the model document.</param>
    /// <param name="create">Creates an empty row.</param>
    public SetBinding(string setName, Func<T> create)
        : base(setName, typeof(T), Untyped(create))
    {
    }

    /// <summary>Binds a scalar or enum property.</summary>
    /// <param name="name">The wire name.</param>
    /// <param name="member">The CLR property name (queries name it in lambdas).</param>
    /// <param name="get">Reads the property.</param>
    /// <param name="set">Writes the property.</param>
    /// <typeparam name="TValue">The property type.</typeparam>
    /// <returns>The same binding.</returns>
    public SetBinding<T> Property<TValue>(string name, string member, Func<T, TValue> get, Action<T, TValue> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        return Add(new MemberBinding(name, member, MemberKind.Property, typeof(TValue), null)
        {
            Get = entity => get((T)entity),
            Set = (entity, value) => set((T)entity, (TValue)value!),
            Read = (element, location) => JsonValues.ReadScalar<TValue>(element, location),
            ReadInto = (entity, element, location) => set((T)entity, JsonValues.ReadScalar<TValue>(element, location)),
        });
    }

    /// <summary>Binds a primitive-collection property, such as tags.</summary>
    /// <param name="name">The wire name.</param>
    /// <param name="member">The CLR property name.</param>
    /// <param name="get">Reads the property.</param>
    /// <param name="set">Writes the property.</param>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <returns>The same binding.</returns>
    public SetBinding<T> Values<TElement>(string name, string member, Func<T, List<TElement>> get, Action<T, List<TElement>> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        return Add(new MemberBinding(name, member, MemberKind.Values, typeof(List<TElement>), typeof(TElement))
        {
            Get = entity => get((T)entity),
            Set = (entity, value) => set((T)entity, (List<TElement>)value!),
            Read = JsonValues.ReadList<TElement>,
            ReadInto = (entity, element, location) => set((T)entity, JsonValues.ReadList<TElement>(element, location)!),
        });
    }

    /// <summary>Binds a reference navigation to another set.</summary>
    /// <param name="name">The wire name.</param>
    /// <param name="member">The CLR property name.</param>
    /// <param name="get">Reads the navigation.</param>
    /// <param name="set">Writes the navigation.</param>
    /// <typeparam name="TTarget">The target set's class.</typeparam>
    /// <returns>The same binding.</returns>
    public SetBinding<T> Reference<TTarget>(string name, string member, Func<T, TTarget?> get, Action<T, TTarget?> set)
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        return Add(new MemberBinding(name, member, MemberKind.Reference, typeof(TTarget), typeof(TTarget))
        {
            Get = entity => get((T)entity),
            Set = (entity, value) => set((T)entity, (TTarget?)value),
        });
    }

    /// <summary>Binds a collection navigation to another set.</summary>
    /// <param name="name">The wire name.</param>
    /// <param name="member">The CLR property name.</param>
    /// <param name="get">Reads the navigation.</param>
    /// <param name="set">Writes the navigation.</param>
    /// <typeparam name="TTarget">The target set's class.</typeparam>
    /// <returns>The same binding.</returns>
    public SetBinding<T> Collection<TTarget>(string name, string member, Func<T, List<TTarget>> get, Action<T, List<TTarget>> set)
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        return Add(new MemberBinding(name, member, MemberKind.Collection, typeof(List<TTarget>), typeof(TTarget))
        {
            Get = entity => get((T)entity),
            Set = (entity, value) => set((T)entity, (List<TTarget>)value!),
            NewList = static () => new List<TTarget>(),
        });
    }

    private static Func<object> Untyped(Func<T> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        return create;
    }

    private SetBinding<T> Add(MemberBinding member)
    {
        Members.Add(member);
        return this;
    }
}

internal enum MemberKind
{
    Property,
    Values,
    Reference,
    Collection,
}

/// <summary>One bound member: names, types and untyped accessors, whichever way the binding was made.</summary>
internal sealed class MemberBinding
{
    public MemberBinding(string name, string member, MemberKind kind, Type valueType, Type? elementType)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(member);
        (Name, Member, Kind, ValueType, ElementType) = (name, member, kind, valueType, elementType);
    }

    /// <summary>Gets the wire name.</summary>
    public string Name { get; }

    /// <summary>Gets the CLR property name.</summary>
    public string Member { get; }

    public MemberKind Kind { get; }

    /// <summary>Gets the property type.</summary>
    public Type ValueType { get; }

    /// <summary>Gets the element type of a collection, or the target class of a reference.</summary>
    public Type? ElementType { get; }

    public required Func<object, object?> Get { get; init; }

    public required Action<object, object?> Set { get; init; }

    /// <summary>Gets the reader of a scalar or primitive-collection value.</summary>
    public Func<JsonElement, string, object?>? Read { get; init; }

    /// <summary>Gets the reader of a scalar or primitive-collection value that sets it on a row, typed (no boxing).</summary>
    public Action<object, JsonElement, string>? ReadInto { get; init; }

    /// <summary>Gets a factory of an empty list, for collection navigations.</summary>
    public Func<IList>? NewList { get; init; }
}
