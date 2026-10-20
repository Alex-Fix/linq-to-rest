using Catalog.Blazor;
using Catalog.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

var api = new Uri(new Uri(builder.Configuration["CatalogApi"] ?? "http://localhost:5095/"), "api/");
builder.Services.AddSingleton<Session>();
builder.Services.AddTransient<BearerTokenHandler>();

// The generated registration: a named HttpClient for CatalogContext, plus the token handler.
builder.Services.AddCatalogContext(http => http.BaseAddress = api).AddHttpMessageHandler<BearerTokenHandler>();

await builder.Build().RunAsync();
