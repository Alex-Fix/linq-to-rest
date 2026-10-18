using System.Linq.Expressions;
using System.Reflection;
using RestToLinq.Modeling;
using RestToLinq.Protocol;
using RestToLinq.Server.Internal;

namespace RestToLinq.Server.Query;

internal sealed partial class QueryBinder
{
    private static readonly MethodInfo[] EnumerableMethods = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static);

    public Shape Select(QueryNode node, Scope scope, Row row)
    {
        switch (node)
        {
            case NewNode shape:
                return new ObjectShape([.. shape.Fields.Select(field => KeyValuePair.Create(field.Key, Select(field.Value, scope, row)))], null);
            case LambdaNode { Op: QueryOperators.Map } map:
                return Map(map, scope, row);
            case MemberNode member:
                switch (Member(member, scope, MemberUse.Select))
                {
                    case ValueBound value:
                        return new ValueShape(row.Add(value.Expression));
                    case EntityBound entity:
                        return DefaultShape(entity, row, null);
                    case CollectionBound { ElementMapping: null } collection:
                        return new ValueShape(row.Add(collection.Value!));
                    case CollectionBound collection:
                        var parameter = Expression.Parameter(collection.ElementType, NextParameterName());
                        var elementRow = new Row(parameter);
                        var element = DefaultShape(new EntityBound(parameter, collection.ElementMapping, null, isNullable: false), elementRow, null);
                        return new CollectionShape(row.Add(RowsList(collection.Source, elementRow)), element);
                }

                break;
        }

        return new ValueShape(row.Add(Value(node, scope, null, MemberUse.Select)));
    }

    /// <summary>The full DTO: every scalar and primitive-collection member, plus included navigations.</summary>
    public Shape DefaultShape(EntityBound entity, Row row, IncludeTree? includes)
    {
        int? presence = entity.NeedsLifting ? row.Add(Presence(entity)) : null;
        var fields = new List<KeyValuePair<string, Shape>>();
        foreach (var property in entity.Mapping.DtoType.Properties)
        {
            fields.Add(new(property.WireName, new ValueShape(row.Add(PropertyValue(entity, property)))));
        }

        foreach (var (navigation, children) in includes?.Children ?? [])
        {
            fields.Add(new(navigation.WireName, NavigationShape(entity, navigation, row, children)));
        }

        return new ObjectShape(fields, presence);
    }

    private Shape NavigationShape(EntityBound entity, DtoNavigation navigation, Row row, IncludeTree children)
    {
        if (!navigation.IsCollection)
        {
            return DefaultShape(Reference(entity, navigation), row, children);
        }

        var map = entity.Mapping.Navigations[navigation];
        var parameter = Expression.Parameter(map.Target.EntityType, NextParameterName());
        var elementRow = new Row(parameter);
        var element = DefaultShape(new EntityBound(parameter, map.Target, null, isNullable: false), elementRow, children);
        return new CollectionShape(row.Add(RowsList(CollectionSource(entity, map), elementRow)), element);
    }

    private CollectionShape Map(LambdaNode node, Scope scope, Row row)
    {
        var (source, parameter, inner) = OpenLambda(node, scope, MemberUse.Select);
        var elementRow = new Row(parameter!);
        var element = Select(node.Body!, inner, elementRow);
        return new CollectionShape(row.Add(RowsList(source, elementRow)), element);
    }

    private MethodCallExpression Aggregate(LambdaNode node, Scope scope, MemberUse use)
    {
        var (source, parameter, inner) = OpenLambda(node, scope, use);
        var elementType = ExpressionHelpers.GetSequenceElementType(source.Type)!;
        switch (node.Op)
        {
            case QueryOperators.Any or QueryOperators.Count:
                return node.Body is null
                    ? Expression.Call(typeof(Enumerable), node.Op == QueryOperators.Any ? nameof(Enumerable.Any) : nameof(Enumerable.Count), [elementType], source)
                    : Expression.Call(typeof(Enumerable), node.Op == QueryOperators.Any ? nameof(Enumerable.Any) : nameof(Enumerable.Count), [elementType], source, Expression.Lambda(Bool(node.Body, inner, MemberUse.Filter), parameter!));
            case QueryOperators.All:
                return Expression.Call(typeof(Enumerable), nameof(Enumerable.All), [elementType], source, Expression.Lambda(Bool(node.Body!, inner, MemberUse.Filter), parameter!));
        }

        parameter ??= Expression.Parameter(elementType, NextParameterName());
        var selector = node.Body is null
            ? ScalarTypes.IsScalar(elementType) ? parameter : throw TypeMismatch(node)
            : Value(node.Body, inner, null, use);
        var selectorType = ExpressionHelpers.Underlying(selector.Type);
        if (node.Op is QueryOperators.Sum or QueryOperators.Avg)
        {
            if (!ScalarTypes.IsNumeric(selectorType))
            {
                throw TypeMismatch(node);
            }

            var target = selectorType == typeof(byte) || selectorType == typeof(short) ? typeof(int) : selectorType;

            // Sum of nothing is 0; average of nothing is null, so it's computed over a nullable selector.
            target = node.Op == QueryOperators.Avg || Nullable.GetUnderlyingType(selector.Type) is not null ? ExpressionHelpers.MakeNullable(target) : target;
            var name = node.Op == QueryOperators.Sum ? nameof(Enumerable.Sum) : nameof(Enumerable.Average);
            var method = EnumerableMethods.Single(method => method.Name == name && method.IsGenericMethodDefinition && method.GetParameters().Length == 2
                && method.GetParameters()[1].ParameterType.GetGenericArguments()[1] == target);
            return Expression.Call(method.MakeGenericMethod(elementType), source, Expression.Lambda(Convert(selector, target), parameter));
        }

        if (!ScalarTypes.IsNumeric(selectorType) && selectorType != typeof(string) && Comparison(ExpressionType.LessThan, selector, selector) is null)
        {
            throw TypeMismatch(node);
        }

        // Min and max of nothing are null, so the selector is lifted to nullable.
        var lifted = Convert(selector, ExpressionHelpers.MakeNullable(selector.Type));
        var minMax = EnumerableMethods.Single(method => method.Name == (node.Op == QueryOperators.Min ? nameof(Enumerable.Min) : nameof(Enumerable.Max))
            && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 2 && method.GetParameters().Length == 2);
        return Expression.Call(minMax.MakeGenericMethod(elementType, lifted.Type), source, Expression.Lambda(lifted, parameter));
    }

    private (Expression Source, ParameterExpression? Parameter, Scope Inner) OpenLambda(LambdaNode node, Scope scope, MemberUse use)
    {
        var collection = node.Source is MemberNode member && Member(member, scope, use) is CollectionBound bound
            ? bound
            : throw TypeMismatch(node.Source);
        if (node.Variable is null)
        {
            return (collection.Source, null, scope);
        }

        if (scope.Variables.ContainsKey(node.Variable))
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, node.Location);
        }

        var parameter = Expression.Parameter(collection.ElementType, node.Variable);
        Bound element = collection.ElementMapping is { } mapping
            ? new EntityBound(parameter, mapping, null, isNullable: false)
            : new ValueBound(parameter, ExpressionHelpers.IsNullable(collection.ElementType));
        var inner = scope.With(node.Variable, element);
        var source = collection.Source;
        if (node.Where is not null)
        {
            source = Expression.Call(typeof(Enumerable), nameof(Enumerable.Where), [collection.ElementType], source, Expression.Lambda(Bool(node.Where, inner, MemberUse.Filter), parameter));
        }

        return (source, parameter, inner);
    }
}

/// <summary>The navigations to include, as a tree.</summary>
internal sealed class IncludeTree
{
    public Dictionary<DtoNavigation, IncludeTree> Children { get; } = [];

    public static IncludeTree Parse(IReadOnlyList<MemberNode> paths, DtoType root, int maxPathDepth)
    {
        var tree = new IncludeTree();
        foreach (var path in paths)
        {
            var (node, type) = (tree, root);
            if (path.Segments.Count > maxPathDepth)
            {
                throw RequestException.LimitExceeded(path.Location, "pathDepth");
            }

            foreach (var segment in path.Segments)
            {
                var navigation = type.FindNavigation(segment) ?? throw QueryBinder.UnknownMember(path);
                if (!node.Children.TryGetValue(navigation, out var child))
                {
                    child = new IncludeTree();
                    node.Children.Add(navigation, child);
                }

                (node, type) = (child, navigation.Target);
            }
        }

        return tree;
    }
}
