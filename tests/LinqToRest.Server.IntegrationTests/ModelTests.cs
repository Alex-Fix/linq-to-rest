using System.Runtime.CompilerServices;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.IntegrationTests.Shop;
using LinqToRest.Server.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>
/// Shop/model.json is the shop's exported model, which the end-to-end tests' client is generated from.
/// LINQTOREST_UPDATE_GOLDEN=1 rewrites it.
/// </summary>
public sealed class ModelTests
{
    [Fact]
    public async Task Shop_sets_export_the_checked_in_model()
    {
        var services = new ServiceCollection()
            .AddDbContext<ShopDbContext>(options => options.UseSqlite("DataSource=:memory:"))
            .AddLinqToRest(o => o.UseDbContext<ShopDbContext>().AddSetsFromAssembly(typeof(ShopDbContext).Assembly));
        await using var provider = services.BuildServiceProvider();
        var json = provider.GetRequiredService<ServerModel>().Model.ToJson() + "\n";

        if (Environment.GetEnvironmentVariable("LINQTOREST_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(ShopDirectory(), "model.json"), json, TestContext.Current.CancellationToken);
            return;
        }

        var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Shop", "model.json"), TestContext.Current.CancellationToken);
        Assert.Equal(expected.ReplaceLineEndings("\n"), json);
    }

    private static string ShopDirectory([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Shop");
}
