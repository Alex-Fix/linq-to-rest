using System.Linq.Expressions;
using LinqToRest.Modeling;
using LinqToRest.Protocol;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Mapping;

namespace LinqToRest.Server.Query;

internal sealed partial class QueryBinder
{
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
                        return new CollectionShape(row.Add(RowsList(collection.Source, elementRow, collection.ElementMapping)), element);
                }

                break;
        }

        return new ValueShape(row.Add(Value(node, scope, null, MemberUse.Select)));
    }

    /// <summary>The full row: every scalar and primitive-collection member, plus included navigations.</summary>
    public Shape DefaultShape(EntityBound entity, Row row, IncludeTree? includes)
    {
        int? presence = entity.NeedsLifting ? row.Add(Presence(entity)) : null;
        var fields = new List<KeyValuePair<string, Shape>>();
        foreach (var property in entity.Mapping.Properties)
        {
            fields.Add(new(property.Name, new ValueShape(row.Add(PropertyValue(entity, property)))));
        }

        foreach (var (navigation, children) in includes?.Children ?? [])
        {
            fields.Add(new(navigation.Name, NavigationShape(entity, navigation, row, children)));
        }

        return new ObjectShape(fields, presence);
    }

    private Shape NavigationShape(EntityBound entity, SetNavigation navigation, Row row, IncludeTree children)
    {
        if (!navigation.IsCollection)
        {
            return DefaultShape(Reference(entity, navigation), row, children);
        }

        var parameter = Expression.Parameter(navigation.Target.EntityType, NextParameterName());
        var elementRow = new Row(parameter);
        var element = DefaultShape(new EntityBound(parameter, navigation.Target, null, isNullable: false), elementRow, children);
        return new CollectionShape(row.Add(RowsList(CollectionSource(entity, navigation), elementRow, navigation.Target)), element);
    }

    private CollectionShape Map(LambdaNode node, Scope scope, Row row)
    {
        var (source, parameter, inner) = OpenLambda(node, scope, MemberUse.Select);
        var elementRow = new Row(parameter!);
        var element = Select(node.Body!, inner, elementRow);
        var elementMapping = inner.Variables[node.Variable!] is EntityBound entity ? entity.Mapping : null;
        return new CollectionShape(row.Add(RowsList(source, elementRow, elementMapping)), element);
    }

    private MethodCallExpression Aggregate(LambdaNode node, Scope scope, MemberUse use)
    {
        var (source, parameter, inner) = OpenLambda(node, scope, use);
        var elementType = ExpressionHelpers.GetSequenceElementType(source.Type)!;
        switch (node.Op)
        {
            case QueryOperators.Any or QueryOperators.Count:
                return node.Body is null
                    ? Expression.Call((node.Op == QueryOperators.Any ? EnumerableMethods.Any : EnumerableMethods.Count).MakeGenericMethod(elementType), source)
                    : Expression.Call((node.Op == QueryOperators.Any ? EnumerableMethods.AnyWhere : EnumerableMethods.CountWhere).MakeGenericMethod(elementType), source, Expression.Lambda(Bool(node.Body, inner, MemberUse.Filter), parameter!));
            case QueryOperators.All:
                return Expression.Call(EnumerableMethods.All.MakeGenericMethod(elementType), source, Expression.Lambda(Bool(node.Body!, inner, MemberUse.Filter), parameter!));
        }

        parameter ??= Expression.Parameter(elementType, NextParameterName());

        // Sum, min, max and avg over a group read only aggregatable members.
        var selector = node.Body is null
            ? ScalarTypes.IsScalar(elementType) ? parameter : throw TypeMismatch(node)
            : Value(node.Body, inner, null, node.Source is GroupNode ? MemberUse.Aggregate : use);
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
            var method = (node.Op == QueryOperators.Sum ? EnumerableMethods.Sum : EnumerableMethods.Average)[target];
            return Expression.Call(method.MakeGenericMethod(elementType), source, Expression.Lambda(Convert(selector, target), parameter));
        }

        if (!ScalarTypes.IsNumeric(selectorType) && selectorType != typeof(string) && Comparison(ExpressionType.LessThan, selector, selector) is null)
        {
            throw TypeMismatch(node);
        }

        // Min and max of nothing are null, so the selector is lifted to nullable.
        var lifted = Convert(selector, ExpressionHelpers.MakeNullable(selector.Type));
        var minMax = node.Op == QueryOperators.Min ? EnumerableMethods.Min : EnumerableMethods.Max;
        return Expression.Call(minMax.MakeGenericMethod(elementType, lifted.Type), source, Expression.Lambda(lifted, parameter));
    }

    private (Expression Source, ParameterExpression? Parameter, Scope Inner) OpenLambda(LambdaNode node, Scope scope, MemberUse use)
    {
        CollectionBound collection;
        if (node.Source is GroupNode)
        {
            // The group's rows: only direct aggregates, with no group-level nodes or other collections inside.
            if (scope.Group is not { } group || node.Op is not (QueryOperators.Count or QueryOperators.Sum or QueryOperators.Min or QueryOperators.Max or QueryOperators.Avg))
            {
                throw RequestException.Invalid(ErrorCodes.InvalidQuery, node.Location);
            }

            collection = new CollectionBound(group.Group, group.Mapping.EntityType, group.Mapping, null);
            scope = scope with { Group = null, RootHidden = true, InGroupAggregate = true };
        }
        else if (scope.InGroupAggregate)
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, node.Location);
        }
        else
        {
            collection = node.Source is MemberNode member && Member(member, scope, use) is CollectionBound bound
                ? bound
                : throw TypeMismatch(node.Source);
        }

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
            source = Expression.Call(EnumerableMethods.Where.MakeGenericMethod(collection.ElementType), source, Expression.Lambda(Bool(node.Where, inner, MemberUse.Filter), parameter));
        }

        return (source, parameter, inner);
    }
}

/// <summary>The navigations to include, as a tree.</summary>
internal sealed class IncludeTree
{
    public Dictionary<SetNavigation, IncludeTree> Children { get; } = [];

    public static IncludeTree Parse(IReadOnlyList<MemberNode> paths, SetMapping root, int maxPathDepth)
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
