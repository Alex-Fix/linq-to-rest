using Catalog.Api;
using LinqToRest.Server.Hosting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<CatalogDbContext>(options =>
{
    // SQLite by default; --Database:Provider=Postgres with docker compose -f samples/docker-compose.yml up -d.
    _ = builder.Configuration["Database:Provider"] switch
    {
        "Sqlite" => options.UseSqlite(ConnectionString("CatalogSqlite")),
        "Postgres" => options.UseNpgsql(ConnectionString("CatalogPostgres")),
        _ => throw new InvalidOperationException("Set Database:Provider to Sqlite or Postgres."),
    };
});
builder.Services.AddLinqToRest(options => options
    .UseDbContext<CatalogDbContext>()
    .AddSetsFromAssembly(typeof(ProductSet).Assembly)
    .AddPolicy<CategoryPolicy>()
    .AddPolicy<BrandPolicy>()
    .AddPolicy<ProductPolicy>()
    .AddPolicy<ProductImagePolicy>());
builder.Services.AddDevLoginAuthentication(builder.Configuration);

// The browser apps call the API directly, so they need CORS, including the QUERY method (browsers preflight it).
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .WithMethods("QUERY", "POST")
    .WithHeaders("Content-Type", "Authorization")));

var app = builder.Build();

// Writes the model the clients are generated from, then exits:
// dotnet run --project samples/Catalog/Catalog.Api -- --export-linqtorest-model model.json
// (dotnet run starts in the project directory, so the path is relative to it).
if (Array.IndexOf(args, "--export-linqtorest-model") is var export and >= 0)
{
    await app.ExportLinqToRestModelAsync(args[export + 1]);
    return;
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await db.Database.EnsureCreatedAsync();
    await CatalogSeed.SeedAsync(db);
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Reads are public, so the group doesn't require authorization; the policies let only editors write.
app.MapLinqToRest("/api");
if (app.Environment.IsDevelopment())
{
    app.MapDevLogin(app.Configuration, app.Services.GetRequiredService<TimeProvider>());
}

await app.RunAsync();

string ConnectionString(string name) =>
    builder.Configuration.GetConnectionString(name)
    ?? throw new InvalidOperationException($"Configure ConnectionStrings:{name}.");

/// <summary>The entry point, visible to the sample's tests.</summary>
public partial class Program;
