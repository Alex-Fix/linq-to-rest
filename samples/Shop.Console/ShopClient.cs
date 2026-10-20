using LinqToRest.Client;
using Shop.Contracts;

namespace Shop.Console;

public sealed class ShopClient(RestDataContextOptions<ShopClient> options) : RestDataContext(options)
{
    public RestSet<ProductDto> Products => Set<ProductDto>();
    public RestSet<CategoryDto> Categories => Set<CategoryDto>();
}
