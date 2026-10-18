using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using RestToLinq.Server.Hosting;
using Shop.Api;
using Shop.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ShopDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Shop")));
builder.Services.AddAuthentication(ApiKeyAuthentication.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddRestToLinq(ShopModel.Create(), options => options
    .UseDbContext<ShopDbContext>()
    .AddMappingsFromAssembly(typeof(ProductMapping).Assembly)
    .AddPolicy<ProductPolicy>()
    .AddPolicy<CategoryPolicy>()
    .Limits(limits => limits.MaxNodes = 200));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.SeedAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapRestToLinq("/api").RequireAuthorization();
await app.RunAsync();
