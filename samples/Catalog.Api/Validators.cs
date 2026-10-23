using LinqToRest.Server.Validation;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api;

/// <summary>
/// SKUs are unique (there's a unique index too). Checking here turns the database's 409 into a field error the editor
/// shows next to the SKU. Drafts are hidden from shoppers but still count: the code says "taken", nothing more.
/// </summary>
public sealed class UniqueSkuValidator(CatalogDbContext db) : IEntityValidator<Product>
{
    public async ValueTask ValidateAsync(EntityValidationContext<Product> context, CancellationToken cancellationToken)
    {
        var product = context.Entity;
        if (context.IsModified(p => p.Sku)
            && await db.Products.AnyAsync(other => other.Sku == product.Sku && other.Id != product.Id, cancellationToken))
        {
            context.AddError(p => p.Sku, "duplicate");
        }
    }
}
