using System.Linq.Expressions;
using LinqToRest.Server.Policies;
using Shop.Contracts;

namespace Shop.Api;

/// <summary>Every row belongs to a tenant; callers only see and change their own.</summary>
public sealed class ProductPolicy : DtoSetPolicy<ProductDto, Product>
{
    protected override Expression<Func<Product, bool>> Filter(PolicyContext context)
    {
        var tenant = ApiKeyAuthentication.Tenant(context.User);
        return product => product.TenantId == tenant;
    }

    protected override void OnInsert(Product entity, PolicyContext context)
    {
        entity.TenantId = ApiKeyAuthentication.Tenant(context.User);
        entity.CreatedAt = DateTime.UtcNow;
    }
}

public sealed class CategoryPolicy : DtoSetPolicy<CategoryDto, Category>
{
    protected override Expression<Func<Category, bool>> Filter(PolicyContext context)
    {
        var tenant = ApiKeyAuthentication.Tenant(context.User);
        return category => category.TenantId == tenant;
    }

    protected override void OnInsert(Category entity, PolicyContext context) => entity.TenantId = ApiKeyAuthentication.Tenant(context.User);
}
