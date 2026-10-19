using System.Linq.Expressions;
using System.Reflection;

namespace LinqToRest.Server.Mapping;

internal sealed record NavigationDefinition(
    PropertyInfo DtoProperty,
    LambdaExpression Entity,
    Type TargetDto,
    Type TargetEntity,
    bool IsCollection);
