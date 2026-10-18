using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using RestToLinq.Server.Hosting;
using Scalar.AspNetCore;
using Shop.Api;
using Shop.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ShopDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Shop")));
builder.Services.AddAuthentication(ApiKeyAuthentication.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    // Lets Scalar send the API key with every request it tries.
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
    {
        [ApiKeyAuthentication.SchemeName] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Api-Key" },
    };
    document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKeyAuthentication.SchemeName, document)] = [] }];
    return Task.CompletedTask;
}));
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
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.AddApiKeyAuthentication(ApiKeyAuthentication.SchemeName, key => key.Value = "dev-acme-key"));
}

await app.RunAsync();
