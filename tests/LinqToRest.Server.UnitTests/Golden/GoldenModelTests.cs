using System.Runtime.CompilerServices;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.UnitTests.Golden;

/// <summary>
/// tests/golden/model.json is the model the client tests, both generators and the TypeScript client are built from.
/// LINQTOREST_UPDATE_GOLDEN=1 rewrites it from the sets in GoldenShop.cs.
/// </summary>
public sealed class GoldenModelTests
{
    [Fact]
    public async Task Golden_sets_export_the_golden_model()
    {
        var services = new ServiceCollection()
            .AddDbContext<GoldenContext>(options => options.UseSqlite("DataSource=:memory:"))
            .AddLinqToRest(o => o.UseDbContext<GoldenContext>().AddSet(new GoldenProductSet()).AddSet(new GoldenCategorySet()).AddSet(new GoldenOrderLineSet()));
        await using var provider = services.BuildServiceProvider();
        var json = provider.GetRequiredService<ServerModel>().Model.ToJson() + "\n";

        if (Environment.GetEnvironmentVariable("LINQTOREST_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(GoldenDirectory(), "model.json"), json, TestContext.Current.CancellationToken);
            return;
        }

        var golden = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "model.json"), TestContext.Current.CancellationToken);
        Assert.Equal(golden.ReplaceLineEndings("\n"), json);
    }

    private static string GoldenDirectory([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "golden"));
}
