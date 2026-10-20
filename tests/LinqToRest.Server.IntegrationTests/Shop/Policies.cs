using System.Linq.Expressions;
using LinqToRest.Server.Policies;

namespace LinqToRest.Server.IntegrationTests.Shop;

/// <summary>Tenant from the X-Tenant header; X-Read-Only denies writes, X-No-Read denies reads.</summary>
public static class Tenant
{
    public static int Of(PolicyContext context) =>
        int.TryParse(context.HttpContext.Request.Headers["X-Tenant"], out var tenant) ? tenant : 0;
}

public sealed class ProductPolicy : SetPolicy<Product>
{
    protected override Expression<Func<Product, bool>> Filter(PolicyContext context)
    {
        var tenant = Tenant.Of(context);
        return product => product.TenantId == tenant;
    }

    protected override ValueTask<bool> CanAsync(SetOperation operation, PolicyContext context)
    {
        var headers = context.HttpContext.Request.Headers;
        var denied = operation == SetOperation.Read ? headers.ContainsKey("X-No-Read") : headers.ContainsKey("X-Read-Only");
        return ValueTask.FromResult(!denied);
    }

    protected override void OnInsert(Product entity, PolicyContext context)
    {
        entity.TenantId = Tenant.Of(context);
        entity.CreatedAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        entity.Version = Guid.NewGuid();
    }

    protected override void OnUpdate(Product entity, PolicyContext context) => entity.Description ??= $"updated for tenant {Tenant.Of(context)}";
}

public sealed class CategoryPolicy : SetPolicy<Category>
{
    protected override Expression<Func<Category, bool>> Filter(PolicyContext context)
    {
        var tenant = Tenant.Of(context);
        return category => category.TenantId == tenant;
    }

    protected override void OnInsert(Category entity, PolicyContext context) => entity.TenantId = Tenant.Of(context);
}
