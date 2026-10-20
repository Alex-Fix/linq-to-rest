using LinqToRest.Server.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Shop.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ShopDbContext>(options =>
{
    // PostgreSQL by default (docker compose -f samples/docker-compose.yml up -d); --Database:Provider=Sqlite runs without Docker.
    var provider = builder.Configuration["Database:Provider"];
    _ = provider switch
    {
        "Postgres" => options.UseNpgsql(ConnectionString("ShopPostgres")),
        "Sqlite" => options.UseSqlite(ConnectionString("ShopSqlite")),
        _ => throw new InvalidOperationException("Set Database:Provider to Postgres or Sqlite."),
    };
});
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
builder.Services.AddLinqToRest(options => options
    .UseDbContext<ShopDbContext>()
    .AddSetsFromAssembly(typeof(ProductSet).Assembly)
    .AddPolicy<ProductPolicy>()
    .AddPolicy<CategoryPolicy>()
    .Limits(limits => limits.MaxNodes = 200));

var app = builder.Build();

// Writes the model clients are generated from, then exits:
// dotnet run --project samples/Shop.Api -- --Database:Provider=Sqlite --export-linqtorest-model model.json
// (dotnet run starts in samples/Shop.Api, so the path is relative to it).
if (Array.IndexOf(args, "--export-linqtorest-model") is var export and >= 0)
{
    await app.ExportLinqToRestModelAsync(args[export + 1]);
    return;
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.SeedAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapLinqToRest("/api").RequireAuthorization();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.AddApiKeyAuthentication(ApiKeyAuthentication.SchemeName, key => key.Value = "dev-acme-key"));
}

await app.RunAsync();

string ConnectionString(string name) =>
    builder.Configuration.GetConnectionString(name)
    ?? throw new InvalidOperationException($"Configure ConnectionStrings:{name} (appsettings.Development.json has a local one).");
