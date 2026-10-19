# linq-to-rest

[![ci](https://github.com/Alex-Fix/linq-to-rest/actions/workflows/ci.yml/badge.svg)](https://github.com/Alex-Fix/linq-to-rest/actions/workflows/ci.yml)

LinqToRest covers both ends of a .NET REST API with one set of shared DTOs:

- **Client** — write LINQ and unit-of-work code against a `RestDataContext`. Queries are translated into a JSON
  expression tree and sent with HTTP `QUERY`; `SaveChangesAsync` sends every insert, update and delete as one
  atomic `POST /$batch`.
- **Server** — validates those trees against the DTO model and runs them on EF Core, where each DTO is the default
  projection of an entity. Entities never leave the server; batches run in one transaction.

```csharp
var games = await shop.Products
    .Where(p => p.Category!.Name == "Games" && p.Price < 25)
    .OrderBy(p => p.Price)
    .Select(p => new { p.Name, Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice) })
    .Take(10)
    .ToListAsync();

var chess = await shop.Products.FirstAsync(p => p.Name == "Chess");
chess.Price += 1;
shop.Products.Add(new ProductDto { Name = "Cube", Price = 9.5m, Category = puzzles });
await shop.SaveChangesAsync();      // one atomic batch
```

Design and rationale: [`docs/plans/2026-10-18-linqtorest-v1-design.md`](docs/plans/2026-10-18-linqtorest-v1-design.md).

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `LinqToRest.Abstractions` | `IDto`, the EF-style DTO model builder, query tree and wire contracts | BCL only |
| `LinqToRest.Client` | `RestDataContext`, `DtoSet<T>`, LINQ provider, change tracking | `Microsoft.Extensions.Http` |
| `LinqToRest.Server` | DTO → entity mappings, query and batch execution, policies, endpoints | EF Core, ASP.NET Core |
| `LinqToRest.TypeScript` | `linqtorest-ts` .NET tool: generates the TypeScript model from your contracts | — |
| npm `linqtorest` | TypeScript client: typed queries and unit of work (see [its README](clients/typescript/README.md)) | none |

Targets .NET 10 (`HttpMethod.Query` and `HttpMethods.Query` are built in).

## Quick start

### 1. Shared contracts

```csharp
public sealed class ProductDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public Guid Version { get; set; }
    public int? CategoryId { get; set; }
    public CategoryDto? Category { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed class ProductConfiguration : IDtoTypeConfiguration<ProductDto>
{
    public void Configure(DtoTypeBuilder<ProductDto> b)
    {
        b.ToSet("products").HasKey(d => d.Id).HasConcurrencyToken(d => d.Version);
        b.HasOne(d => d.Category).WithForeignKey(d => d.CategoryId);
        b.Property(d => d.Price).IsSortable();          // sorting is opt-in
        b.Property(d => d.Tags).IsReadOnly();           // computed on the server
        b.HasPagination(p => p.DefaultPageSize(20).MaxPageSize(100));
    }
}

DtoModel model = new DtoModelBuilder().ApplyConfigurationsFromAssembly(typeof(ProductDto).Assembly).Build();
```

Every DTO member is selectable and filterable unless configured otherwise (`IsNotFilterable()`); members are
writable unless they're keys, concurrency tokens or `IsReadOnly()`. `Build()` validates the model and fails fast.

### 2. Server

```csharp
public sealed class ProductMapping : IDtoMapping<ProductDto, Product>
{
    public void Configure(DtoMappingBuilder<ProductDto, Product> m)
    {
        m.Project(p => new ProductDto
        {
            Id = p.Id, Name = p.Name, Price = p.Price, Version = p.Version, CategoryId = p.CategoryId,
            Tags = p.Tags.Select(t => t.Value).ToList(),
        });
        m.HasOne(d => d.Category, p => p.Category);
    }
}

builder.Services.AddLinqToRest(model, o => o
    .UseDbContext<ShopDbContext>()
    .AddMappingsFromAssembly(typeof(ProductMapping).Assembly)
    .AddPolicy<ProductPolicy>()
    .Limits(l => l.MaxNodes = 200));

app.MapLinqToRest("/api").RequireAuthorization();   // QUERY /api/{set}, POST /api/{set}/$query, POST /api/$batch
```

Mappings are validated at startup: every member must be assigned, and writable members, keys and tokens must be
assigned directly from an entity property of the same type. That direct binding is the only way writes reach
an entity.

Policies decide per request which rows are visible and which operations are allowed:

```csharp
public sealed class ProductPolicy : DtoSetPolicy<ProductDto, Product>
{
    protected override Expression<Func<Product, bool>> Filter(PolicyContext c)
    {
        var tenant = c.User.FindFirstValue("tenant");
        return p => p.TenantId == tenant;           // reads, counts, cursors, navigations, updates, deletes
    }

    protected override ValueTask<bool> CanAsync(DtoOperation op, PolicyContext c) => ValueTask.FromResult(op == DtoOperation.Read || c.User.IsInRole("editor"));

    protected override void OnInsert(Product e, PolicyContext c) => e.TenantId = c.User.FindFirstValue("tenant")!;
}
```

Rows hidden by a filter behave as missing: updates and deletes get 404, and hidden navigation targets read as
`null`. Filters don't apply inside your own projection expressions (`CategoryName = p.Category.Name` reads the
category regardless of the category policy), so keep computed members within what the DTO's set may show.

### 3. Client

```csharp
public sealed class ShopClient(RestDataContextOptions<ShopClient> options) : RestDataContext(options)
{
    public DtoSet<ProductDto> Products => Set<ProductDto>();
}

services.AddRestDataContext<ShopClient>(model, http => http.BaseAddress = new("https://shop.example/api/"))
    .AddHttpMessageHandler<AuthHandler>();          // a normal IHttpClientBuilder
```

Supported LINQ, in this order: `Where`* (any number), `Include`*, `OrderBy`/`ThenBy`*, `Select`, and paging with
`After(cursor)` or `Skip`, then `Take`. Inside lambdas: comparisons, `&&` `||` `!`, arithmetic, `?:`, `??`,
string `Contains`/`StartsWith`/`EndsWith`/`ToLower`/`ToUpper`/`Trim`/`Length`/`+`/`Concat`/`Equals`/`CompareOrdinal`,
date parts, `Any`/`All`/`Count`/`Sum`/`Min`/`Max`/`Average`/`Select`/`Where` on collections, and
`list.Contains(d.X)` with captured lists. Anything else throws `NotSupportedException` before a request is sent.

| Operator | Notes |
|---|---|
| `ToListAsync` / `ToArrayAsync` | Requires `Take`, so a server page can't pass for "all rows". |
| `ToCursorPageAsync` | One keyset page with `NextCursor` (and optionally `TotalCount`). |
| `AsAsyncEnumerable` | Follows cursor pages to the end. |
| `FirstAsync`, `SingleAsync`, `CountAsync`, `AnyAsync` (+ `OrDefault`, `LongCount`, predicate overloads) | Fetch only what they need. |
| `Include`, `After`, `AsNoTracking` | LinqToRest operators. |

Queries never run synchronously. Full-DTO results are tracked (identity map); projections aren't. Change tracked DTOs
and call `SaveChangesAsync`: inserts get their generated keys, every saved DTO gets its new concurrency token and
computed members. Failures throw `RestSaveException` / `RestConcurrencyException` and leave the tracked state as it was.

### 4. TypeScript client

```sh
dotnet linqtorest-ts --assembly Shop.Contracts.dll --name Shop --out web/src/shop.generated.ts
```

```ts
const shop = createShop({ baseUrl: "https://shop.example/api/" });
const games = await shop.products
  .where((p) => p.category.name.eq("Games").and(p.price.lt(25)))
  .orderBy((p) => p.price)
  .select((p) => ({ name: p.name, total: p.lines.sum((l) => l.quantity.mul(l.unitPrice)) }))
  .take(10)
  .toList();
```

Same protocol, same rules, same unit of work: both clients write identical request bodies, checked against the shared
golden files in `tests/golden/`. Details in [clients/typescript/README.md](clients/typescript/README.md).

## Wire protocol

`QUERY /api/products` (`application/json`):

```json
{"v":1,
 "filter":{"and":[{"gt":[{"member":"price"},10]},
                  {"any":{"source":{"member":"tags"},"var":"t","body":{"eq":[{"var":"t"},"sale"]}}}]},
 "orderBy":[{"member":"price","desc":true}],
 "select":{"new":{"id":{"member":"id"},"label":{"concat":[{"member":"name"},"!"]}}},
 "page":{"mode":"cursor","take":20},
 "count":true}
```

→ `{"items":[…],"nextCursor":"…","totalCount":42}`

Nodes: `member`, `var`, literals, `new` (shapes), `and` `or` `not` `eq` `ne` `gt` `ge` `lt` `le` `in` `add` `sub` `mul`
`div` `mod` `neg` `contains` `startsWith` `endsWith` `toLower` `toUpper` `length` `trim` `concat` `year` `month` `day`
`hour` `minute` `date` `if` `coalesce`, and the collection lambdas `any` `all` `count` `sum` `min` `max` `avg` `map`.
The list is closed: anything else is rejected.

`POST /api/$batch`:

```json
{"v":1,"operations":[
  {"id":"o0","op":"insert","set":"categories","values":{"name":"Puzzles"}},
  {"id":"o1","op":"insert","set":"products","values":{"name":"Cube","price":9.5},"refs":{"categoryId":"o0"}},
  {"id":"o2","op":"update","set":"products","key":{"id":7},"values":{"price":12},"token":"…"},
  {"id":"o3","op":"delete","set":"products","key":{"id":9},"token":"…"}]}
```

→ `{"results":[{"id":"o0","value":{…}},…]}`, each value re-read through the default projection.

Errors are `application/problem+json` with a stable `code`, a JSON `pointer` into the request, and `limit` or
`operationId(s)` where relevant. They never contain the rejected value.

| Status | Codes |
|---|---|
| 400 | `invalid_json` `unknown_node` `bad_arity` `unknown_member` `not_filterable` `not_sortable` `not_writable` `type_mismatch` `invalid_cursor` `cursor_nullable_sort` `limit_exceeded` `unsupported_version` `invalid_query` `invalid_batch` |
| 403 / 404 | `forbidden` / `not_found` |
| 409 / 412 | `conflict` (constraint violation) / `concurrency_conflict` |
| 415 | `unsupported_media_type` |

## Limits

| Limit | Default |
|---|---|
| Query body / batch body | 64 KB / 1 MB |
| Node depth / nodes | 16 / 500 |
| Lambda nesting / `in` list | 3 / 500 |
| Navigations per path | 3 |
| Page size | per set: default 50, max 200 |
| Skip (offset mode) | per set: 10 000 |
| Batch operations | 100 |

Every query is paged and every sort ends with the key. Cursor pagination only sorts by non-nullable members, because
providers order `NULL` differently.

## Databases

Every query and batch test runs on **SQLite** and **PostgreSQL**. LinqToRest only builds standard EF Core
expressions, so other EF Core providers should work, but these two are the ones verified.

**`DateTime` values:**

- A literal with an offset (`2026-03-01T14:00:00Z`, `…+02:00`) is that UTC instant (`Kind = Utc`). One without an
  offset is a plain local timestamp (`Kind = Unspecified`). The server's own time zone never matters.
- On PostgreSQL, Npgsql maps `DateTime` to `timestamp with time zone`, which only accepts UTC. Keep every value UTC
  and add a converter that treats offset-less values as UTC. EF Core also applies it to the query parameters compared
  with those columns:

  ```csharp
  protected override void ConfigureConventions(ModelConfigurationBuilder b) =>
      b.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

  public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
      v => v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime(),
      v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
  ```

- If a provider still refuses a value the client sent, the server answers 400 `type_mismatch` and logs a warning
  naming the set, instead of a 500.

**SQLite** stores `decimal` as text, which it can't compare, sort or sum. Convert those columns to `double` on SQLite
only, as the tests and sample do.

## Deployment notes

- **HTTP QUERY**: some proxies, WAFs and CDNs reject unknown methods. Allow `QUERY` on the API routes, or send the same
  body to `POST /api/{set}/$query`. Browsers send a CORS preflight for QUERY.
- **OpenAPI**: OpenAPI 3.1 (what ASP.NET Core 10's `AddOpenApi()` generates) can't describe QUERY, so the QUERY route is
  excluded from descriptions; `POST {set}/$query` and `POST $batch` carry full metadata. The sample serves the
  document and a Scalar UI at `/scalar/v1` in Development.
- **Concurrency tokens** must be EF Core concurrency tokens (`IsConcurrencyToken()` / `IsRowVersion()`); the server
  refuses a token that isn't one.

## Development

```sh
dotnet test --solution LinqToRest.slnx                       # unit tests, plus integration and end-to-end tests on SQLite and PostgreSQL
dotnet format LinqToRest.slnx --verify-no-changes
dotnet tool restore && dotnet jb inspectcode LinqToRest.slnx --format=Text --output=- --no-build

docker compose -f samples/docker-compose.yml up -d                   # PostgreSQL for the sample
dotnet run --project samples/Shop.Api --urls http://localhost:5080   # Development: API key dev-acme-key, Scalar at /scalar/v1
dotnet run --project samples/Shop.Api --urls http://localhost:5080 -- --Database:Provider=Sqlite   # without Docker
dotnet run --project samples/Shop.Console
(cd clients/typescript && npm ci && npm run check && npm run test:e2e)   # TypeScript client
(cd samples/shop-ts && npm install && npm start)                         # TypeScript sample (API running)
```

The PostgreSQL tests start a throwaway container with Testcontainers, so they need Docker. Without it they are
reported as **skipped**, with the reason. `LINQTOREST_REQUIRE_POSTGRES=1` makes that a failure instead, which is how CI
runs them. CI (`.github/workflows/ci.yml`) also checks formatting, ReSharper inspections and vulnerable packages on
every push to `main` and every pull request.

## License

[MIT](LICENSE)
