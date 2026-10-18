using RestToLinq.Client;
using Shop.Contracts;

namespace Shop.Console;

public sealed class ShopClient(RestDataContextOptions<ShopClient> options) : RestDataContext(options)
{
    public DtoSet<ProductDto> Products => Set<ProductDto>();
    public DtoSet<CategoryDto> Categories => Set<CategoryDto>();
}
