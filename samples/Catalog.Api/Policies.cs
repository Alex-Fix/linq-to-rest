using System.Linq.Expressions;
using LinqToRest.Server.Policies;

namespace Catalog.Api;

/// <summary>Everyone may read; only editors may insert, update or delete.</summary>
public abstract class CatalogPolicy<TEntity> : SetPolicy<TEntity>
    where TEntity : class
{
    public const string EditorRole = "editor";

    protected static bool IsEditor(PolicyContext context) => context.User.IsInRole(EditorRole);

    protected override ValueTask<bool> CanAsync(SetOperation operation, PolicyContext context) =>
        ValueTask.FromResult(operation == SetOperation.Read || IsEditor(context));
}

public sealed class CategoryPolicy : CatalogPolicy<Category>;

public sealed class BrandPolicy : CatalogPolicy<Brand>;

/// <summary>Drafts and discontinued products are visible to editors only: as rows, through navigations and counts.</summary>
public sealed class ProductPolicy(TimeProvider time) : CatalogPolicy<Product>
{
    protected override Expression<Func<Product, bool>>? Filter(PolicyContext context) =>
        IsEditor(context) ? null : product => product.Status == ProductStatus.Active;

    protected override void OnInsert(Product entity, PolicyContext context)
    {
        entity.CreatedAt = time.GetUtcNow().UtcDateTime;
        entity.Version = Guid.NewGuid();
    }
}

/// <summary>Images follow their product: nobody but editors sees images of products they can't see.</summary>
public sealed class ProductImagePolicy : CatalogPolicy<ProductImage>
{
    protected override Expression<Func<ProductImage, bool>>? Filter(PolicyContext context) =>
        IsEditor(context) ? null : image => image.Product.Status == ProductStatus.Active;
}
