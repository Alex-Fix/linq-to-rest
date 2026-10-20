using System.Runtime.CompilerServices;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.AspNetCore.Builder;
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
    public async Task Shop_exports_the_checked_in_model()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<ShopDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        builder.Services.AddLinqToRest(o => o.UseDbContext<ShopDbContext>().AddSetsFromAssembly(typeof(ShopDbContext).Assembly));
        await using var app = builder.Build();
        var path = Path.Combine(Path.GetTempPath(), $"linqtorest-model-{Guid.NewGuid():N}.json");

        try
        {
            await app.ExportLinqToRestModelAsync(path, TestContext.Current.CancellationToken);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            if (Environment.GetEnvironmentVariable("LINQTOREST_UPDATE_GOLDEN") == "1")
            {
                await File.WriteAllTextAsync(Path.Combine(ShopDirectory(), "model.json"), json, TestContext.Current.CancellationToken);
                return;
            }

            var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Shop", "model.json"), TestContext.Current.CancellationToken);
            Assert.Equal(expected.ReplaceLineEndings("\n"), json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_fails_on_an_invalid_configuration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<ShopDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        builder.Services.AddLinqToRest(o => o.UseDbContext<ShopDbContext>().AddSet<Product>(b => b.ToSet("products").HasMany(p => p.Lines)));
        await using var app = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExportLinqToRestModelAsync(Path.Combine(Path.GetTempPath(), "unused.json"), TestContext.Current.CancellationToken));

        Assert.Contains("to OrderLine, which has no set", error.Message, StringComparison.Ordinal);
    }

    private static string ShopDirectory([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Shop");
}
