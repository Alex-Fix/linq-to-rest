# linq-to-rest

[![ci](https://github.com/Alex-Fix/linq-to-rest/actions/workflows/ci.yml/badge.svg)](https://github.com/Alex-Fix/linq-to-rest/actions/workflows/ci.yml)

LinqToRest covers both ends of a .NET REST API:

- **Server** — exposes EF Core entities as sets: only the members, computed values and navigations you configure are
  reachable, and a set is read-only until it allows writes. It validates each query against that model, runs it on
  EF Core, and applies batches in one transaction. Entities never leave the server.
- **Model document** — the server exports what it exposes as versioned JSON; the C# and TypeScript clients are
  generated from it, so they always match.
- **Client** — write LINQ and unit-of-work code against a generated `RestDataContext`. Queries are translated into a
  JSON expression tree and sent with HTTP `QUERY`; `SaveChangesAsync` sends every insert, update and delete as one
  atomic `POST /$batch`.

```csharp
var games = await shop.Products
    .Where(p => p.Category!.Name == "Games" && p.Price < 25)
    .OrderBy(p => p.Price)
    .Select(p => new { p.Name, Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice) })
    .Take(10)
    .ToListAsync();

var chess = await shop.Products.FirstAsync(p => p.Name == "Chess");
chess.Price += 1;
shop.Products.Add(new Product { Name = "Cube", Price = 9.5m, Category = puzzles });
await shop.SaveChangesAsync();      // one atomic batch
```

For a realistic app (roles, a category tree, React and Blazor front ends) see [`samples/`](samples/README.md);
what comes next is in the [roadmap](docs/plans/2026-10-20-roadmap.md).

Design and rationale: [`docs/plans/2026-10-18-linqtorest-v1-design.md`](docs/plans/2026-10-18-linqtorest-v1-design.md),
with the model reworked by [`docs/plans/2026-10-20-entity-sets-design.md`](docs/plans/2026-10-20-entity-sets-design.md).

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `LinqToRest.Abstractions` | The model document (`RestModel`), query tree and wire contracts | BCL only |
| `LinqToRest.Client` | `RestDataContext`, `RestSet<T>`, `ClientModel`, LINQ provider, change tracking | `Microsoft.Extensions.Http` |
| `LinqToRest.Server` | Entity sets, query and batch execution, policies, endpoints, model export | EF Core, ASP.NET Core |
| `LinqToRest.CodeGen` | `linqtorest-gen` .NET tool: generates the C# and TypeScript clients from the model document | — |
| npm `linqtorest` | TypeScript client: typed queries and unit of work (see [its README](clients/typescript/README.md)) | none |

Targets .NET 10 (`HttpMethod.Query` and `HttpMethods.Query` are built in).

## Quick start

### 1. Server: expose entities as sets

```csharp
public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> b)
    {
        b.ToSet("products").AllowWrites(WriteOperations.All);   // read-only unless you allow writes
        b.Property(p => p.Name);                                // the key is exposed automatically
        b.Property(p => p.Price).IsSortable();                  // sorting is opt-in
        b.HasConcurrencyToken(p => p.Version);
        b.Property(p => p.CategoryId);
        b.Computed("tags", p => p.Tags.Select(t => t.Value).ToList());   // read-only, computed in SQL
        b.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);    // to another configured set
        b.HasMany(p => p.Lines);
        b.HasPagination(p => p.DefaultPageSize(20).MaxPageSize(100));
    }
}

builder.Services.AddLinqToRest(o => o
    .UseDbContext<ShopDbContext>()
    .AddSetsFromAssembly(typeof(ProductSet).Assembly)
    .AddPolicy<ProductPolicy>()
    .Limits(l => l.MaxNodes = 200));

app.MapLinqToRest("/api").RequireAuthorization();   // QUERY /api/{set}, POST /api/{set}/$query, POST /api/$batch
```

Only what a set configures exists on the wire: an entity column you don't expose (`TenantId`, `PasswordHash`, one
added next month) can't be selected, filtered, sorted or written. Exposed properties are filterable unless
`IsNotFilterable()`, and writable when the set allows inserts or updates, unless they're keys, tokens or
`IsReadOnly()`. `$batch` is only mapped when some set allows writes, and an operation a set doesn't allow is refused
with 403 `operation_not_allowed` before anything runs. `MapLinqToRest` validates every set against the EF Core model
and fails at startup on a mistake.

Policies decide per request which rows are visible and which operations are allowed:

```csharp
public sealed class ProductPolicy : SetPolicy<Product>
{
    protected override Expression<Func<Product, bool>> Filter(PolicyContext c)
    {
        var tenant = c.User.FindFirstValue("tenant");
        return p => p.TenantId == tenant;           // reads, counts, cursors, navigations, updates, deletes
    }

    protected override ValueTask<bool> CanAsync(SetOperation op, PolicyContext c) => ValueTask.FromResult(op == SetOperation.Read || c.User.IsInRole("editor"));

    protected override void OnInsert(Product e, PolicyContext c) => e.TenantId = c.User.FindFirstValue("tenant")!;
}
```

Rows hidden by a filter behave as missing: updates and deletes get 404, and hidden navigation targets read as
`null`. Filters don't apply inside your own `Computed` expressions (`p => p.Category!.Name` reads the category
regardless of the category policy), so keep computed values within what the set may show.

### 2. Export the model and generate the clients

```csharp
var app = builder.Build();
if (Array.IndexOf(args, "--export-linqtorest-model") is var export and >= 0)
{
    await app.ExportLinqToRestModelAsync(args[export + 1]);   // in the real host: same services and EF Core model
    return;
}
```

```sh
dotnet run --project Shop.Api -- --export-linqtorest-model model.json   # relative to the project directory
dotnet linqtorest-gen cs --model Shop.Api/model.json --namespace Shop.Client --context ShopContext --out Shop.Client/ShopContext.generated.cs
dotnet linqtorest-gen ts --model Shop.Api/model.json --name Shop --out web/src/shop.generated.ts
```

Check the model and the generated files in, and run the same commands with `--check` in CI. The model document lists
sets, members, types and the writes each set allows, nothing about entities or expressions.
`MapLinqToRest(...).WithModelEndpoint()` also serves it at `GET /api/$model` (off by default).

### 3. Client

The generated `ShopContext` has a `RestSet<T>` per set, the classes of its rows, and a registration extension:

```csharp
services.AddShopContext(http => http.BaseAddress = new("https://shop.example/api/"))
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

Queries never run synchronously. Full-row results are tracked (identity map); projections aren't. Change tracked rows
and call `SaveChangesAsync`: inserts get their generated keys, every saved row gets its new concurrency token and
computed values. `Add`, `Update` and `Remove` throw right away on a set that doesn't allow that write. Failures throw
`RestSaveException` / `RestConcurrencyException` and leave the tracked state as it was.

### 4. TypeScript client

Generated from the same model document (step 2); each set only offers the writes the server allows.

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

→ `{"results":[{"id":"o0","value":{…}},…]}`, each value re-read through its set.

Errors are `application/problem+json` with a stable `code`, a JSON `pointer` into the request, and `limit` or
`operationId(s)` where relevant. They never contain the rejected value.

| Status | Codes |
|---|---|
| 400 | `invalid_json` `unknown_node` `bad_arity` `unknown_member` `not_filterable` `not_sortable` `not_writable` `type_mismatch` `invalid_cursor` `cursor_nullable_sort` `limit_exceeded` `unsupported_version` `invalid_query` `invalid_batch` |
| 403 / 404 | `forbidden`, `operation_not_allowed` / `not_found` |
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
  body to `POST /api/{set}/$query`.
- **CORS**: browsers always preflight `QUERY` (it isn't a "simple" method) and requests with `application/json` or an
  `Authorization` header. A front end on another origin needs the API to allow its origin, the methods and those
  headers; never allow any origin together with credentials:

  ```csharp
  builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
      .WithOrigins("https://shop.example")
      .WithMethods("QUERY", "POST")
      .WithHeaders("Content-Type", "Authorization")));
  app.UseCors();
  ```
- **OpenAPI**: OpenAPI 3.1 (what ASP.NET Core 10's `AddOpenApi()` generates) can't describe QUERY, so the QUERY route is
  excluded from descriptions; `POST {set}/$query` and `POST $batch` carry full metadata.
- **Concurrency tokens** must be EF Core concurrency tokens (`IsConcurrencyToken()` / `IsRowVersion()`); the server
  refuses to start with one that isn't.

## Development

```sh
dotnet test --solution LinqToRest.slnx                       # unit tests, plus integration and end-to-end tests on SQLite and PostgreSQL
dotnet format LinqToRest.slnx --verify-no-changes
dotnet tool restore && dotnet jb inspectcode LinqToRest.slnx --format=Text --output=- --no-build

dotnet run --project samples/Catalog.Api                                 # the sample API (SQLite): see samples/README.md
dotnet run --project samples/Catalog.Blazor                              # Blazor front end, http://localhost:5174
(cd samples/catalog-web && npm install && npm run dev)                   # React front end, http://localhost:5173
dotnet run --project samples/Catalog.Api -- --export-linqtorest-model model.json   # after changing a set
(cd clients/typescript && npm ci && npm run check && npm run test:e2e)   # TypeScript client (e2e starts the sample API)
```

The PostgreSQL tests start a throwaway container with Testcontainers, so they need Docker. Without it they are
reported as **skipped**, with the reason. `LINQTOREST_REQUIRE_POSTGRES=1` makes that a failure instead, which is how CI
runs them. CI (`.github/workflows/ci.yml`) also checks formatting, ReSharper inspections and vulnerable packages on
every push to `main` and every pull request.

## License

[MIT](LICENSE)
