# LinqToRest v1 — design
Status: approved, implemented (see Implementation notes; model superseded by `2026-10-20-entity-sets-design.md`) · Date: 2026-10-18

## Problem

.NET apps that talk to their own REST APIs either hand-write an endpoint per query shape or adopt OData's full
stack. We want both ends in one library: a client that lets developers write LINQ and unit-of-work code against a
`DataContext` of shared DTOs, and a server that safely translates those queries and change sets onto EF Core
entities — with DTOs, not entities, as the public contract.

## Goals / Non-goals

**Goals (v1)**
- Shared DTO model configured EF-style (`IDtoTypeConfiguration<T>`, `DtoModelBuilder`), used by client and server.
- Server: each DTO is the default projection of an entity; mapping configured EF-style (`IDtoMapping<TDto, TEntity>`).
- Queries: filter, sort, offset **and** cursor (keyset) pagination, navigations + collection lambdas,
  full-expression projection, include, count.
- Transport: HTTP **QUERY** with a JSON expression tree as body.
- Client: own `IQueryable<T>` / `IQueryProvider` (EF's are not reused), async terminal operators.
- Full unit of work: change tracking, identity map, `SaveChangesAsync` as one atomic `POST /$batch`.
- Optimistic concurrency, per-set server policies (authorization, row filter, insert hooks).

**Non-goals (v1)**
- URL / query-string syntax, GET queries, POST fallback for blocked QUERY (can be added later without breaking).
- `GroupBy`, joins across sets, top-level aggregates other than count.
- Owned / complex DTO types without their own set.
- Cursor pagination over nullable sort members.
- Changing `HasMany` collection membership without a child FK.
- Non-.NET client SDKs (the JSON contract is documented, but only a .NET client ships).
- Native AOT / trimming support for the client (anonymous-type materialization uses reflection).

## Constraints check

| Constraint | This design |
|---|---|
| **Translatable** | Closed node list; every node and operator has a SQLite translation test that also asserts no client evaluation. Server substitutes DTO member accesses with their mapped entity expressions instead of composing over `Select(projection)`. Cursor mode restricted to non-nullable sort members because null ordering differs across providers. |
| **Untrusted input** | Body size, depth, node count, lambda nesting, `in` length, path depth, page size, skip and batch size are all limited and enforced while reading JSON. Sorting is opt-in per member. Writes only touch members that map directly to entity members (no mass assignment). Per-set policies for authorization and row-level filtering; hidden rows → 404. |
| **No dynamic code** | Server builds `Expression` nodes from a validated, closed AST; never deserializes .NET expressions, never `Compile()`s on the request path. The **client** evaluates captured variables (trusted developer code) — the CLAUDE.md rule is narrowed to the server. |
| **Syntax** | JSON tree, one single-key object per node. QUERY-only accepted risk: proxies/WAFs/CDNs that block unknown methods, CORS preflight in browsers, weak OpenAPI tooling. A `POST /{set}/$query` alias can be added non-breakingly. |
| **Public API** | Large (builders, mapping, DataContext, sets, async operators, wire contract). Wire contract carries `"v": 1` from day one. |
| **Dependencies** | Abstractions and Client: BCL only (System.Text.Json, HttpClient). Server: EF Core + ASP.NET Core. |
| **Errors** | ProblemDetails with stable `code` + JSON `pointer`; never raw input, CLR type names or member names as typed. Client maps to typed exceptions. |

## Chosen approach

**Own query model + JSON wire format.** `Abstractions` defines a closed AST and its JSON contract; the client
translates LINQ → AST with an `ExpressionVisitor`; the server validates the AST against the model and builds
`Expression`s over entities via the DTO mapping.

- *Not serialized .NET expression trees (Serialize.Linq style):* the wire format would be the whole expression API;
  validating it safely means rebuilding the same allow-list, and it locks out non-.NET clients.
- *Not URL syntax / OData-like strings:* dropped in favour of QUERY + JSON only (one format, no text parser).

## Design

### Packages

```
LinqToRest.Abstractions   IDto, model builder + configuration, query AST + JSON contract,
                          batch contract, cursor format. BCL only (System.Text.Json source-gen).
LinqToRest.Client         RestDataContext, DtoSet<T> : IQueryable<T>, RestQueryProvider,
                          change tracker, async operators.  → Abstractions
LinqToRest.Server         DTO→entity mapping, AST → Expression, batch executor, policies,
                          ASP.NET Core endpoints.  → Abstractions, EF Core
```

A contracts assembly in the user's solution holds DTOs + their configuration and is referenced by both ends.

### Shared model

```csharp
public sealed class ProductDto : IDto            // marker interface, no members
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public byte[]? Version { get; set; }
    public int CategoryId { get; set; }
    public CategoryDto? Category { get; set; }
    public List<OrderLineDto> Lines { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string CategoryName { get; set; } = "";
}

public sealed class ProductConfiguration : IDtoTypeConfiguration<ProductDto>
{
    public void Configure(DtoTypeBuilder<ProductDto> b)
    {
        b.ToSet("products");
        b.HasKey(d => d.Id);
        b.HasConcurrencyToken(d => d.Version);
        b.HasOne(d => d.Category).WithForeignKey(d => d.CategoryId);
        b.HasMany(d => d.Lines);
        b.Property(d => d.CategoryName).IsReadOnly();
        b.Property(d => d.Price).IsSortable();
        b.HasPagination(p => p
            .Modes(PaginationModes.Offset | PaginationModes.Cursor)   // default: both
            .DefaultPageSize(50)
            .MaxPageSize(200)
            .MaxSkip(10_000));
    }
}

DtoModel model = new DtoModelBuilder()
    .ApplyConfigurationsFromAssembly(typeof(ProductDto).Assembly)
    .Build();                                       // immutable, validated, built once
```

Access defaults (the DTO is already the explicit contract):
- **Selectable / filterable** — on by default; `IsNotFilterable()` opts out (e.g. large text).
- **Sortable** — opt-in via `IsSortable()`; keys are always sortable.
- **Writable** — everything except key, concurrency token and `IsReadOnly()` members.
- **Navigations** — `HasOne` / `HasMany` to other configured DTOs only; path depth limited (default 3).

`Build()` fails fast on: DTO without key, duplicate set names, unconfigured navigation targets, unsupported member
types.

### Pagination

- **Offset:** `Skip` / `Take`; `MaxSkip` limit. Without `OrderBy` the server orders by key for stable pages.
- **Cursor (keyset):** server always appends the key as final sort member. Cursor =
  base64url(`{v:1, sort:<hash of sort spec>, values:[<last row's sort values>]}`). Next page becomes a keyset
  predicate (`price < 19.99 OR (price == 19.99 AND id > 1042)`). Mismatched hash / wrong value types or count →
  `400 invalid_cursor`. Not signed — tampering only moves the comparison point on visible, sortable members.
  Cursor mode allows only non-nullable sort members (`cursor_nullable_sort`).
- Every query is paged: no `take` → `DefaultPageSize`. `count: true` adds one `COUNT` with the same filter in either
  mode.

### Server mapping and hosting

```csharp
public sealed class ProductMapping : IDtoMapping<ProductDto, Product>
{
    public void Configure(DtoMappingBuilder<ProductDto, Product> m)
    {
        m.FromSource<ShopDbContext>(db => db.Products);
        m.Project(p => new ProductDto                 // default projection, scalars only
        {
            Id = p.Id, Name = p.Name, Price = p.Price, Version = p.RowVersion, CategoryId = p.CategoryId,
            Tags = p.Tags.Select(t => t.Value).ToList(),
            CategoryName = p.Category.Name,           // computed → IsReadOnly() in shared model
        });
        m.HasOne(d => d.Category, p => p.Category);   // reuses CategoryDto's mapping
        m.HasMany(d => d.Lines, p => p.Lines);
    }
}

builder.Services.AddLinqToRest(model, o => o
    .AddMappingsFromAssembly(typeof(ProductMapping).Assembly)
    .UseDbContext<ShopDbContext>()
    .Limits(l => l.MaxNodes(500).MaxDepth(16).MaxBatchOperations(100)));

app.MapLinqToRest("/api")              // QUERY /api/{set}, POST /api/$batch
   .RequireAuthorization();            // RouteGroupBuilder → standard ASP.NET Core auth
```

Startup validation (fail fast):
- every DTO member is assigned in `Project` or mapped as a navigation;
- writable members are bound to a plain entity member (`Name = p.Name`); anything else must be `IsReadOnly()`.
  This reverse binding is the only path writes take;
- key and concurrency token map to plain entity members.

Translation substitutes DTO member accesses with their entity expressions (`d.CategoryName` → `p.Category.Name`),
applies filter / sort / page to `IQueryable<TEntity>`, and projects last — EF sees one flat query.

Navigations are **not loaded by default** (null / empty) — only via `include` or a select that uses them.

Per-set policy (optional, resolved from DI):

```csharp
public sealed class ProductPolicy : DtoSetPolicy<ProductDto, Product>
{
    public override IQueryable<Product> Filter(IQueryable<Product> q, PolicyContext c)
        => q.Where(p => p.TenantId == c.User.TenantId());          // reads and writes
    public override ValueTask<bool> CanAsync(DtoOperation op, PolicyContext c) => ...;  // read/insert/update/delete
    public override void OnInsert(Product e, PolicyContext c) => e.TenantId = c.User.TenantId();
}
```

Update/delete of a row hidden by `Filter` → 404 (not 403), no existence probing.

### Query tree (wire format)

`QUERY /api/products`, `Content-Type: application/json`:

```json
{
  "v": 1,
  "filter": {"and": [
    {"gt": [{"member": "price"}, 10]},
    {"any": {"source": {"member": "tags"}, "var": "t",
             "body": {"eq": [{"var": "t"}, "sale"]}}},
    {"in": [{"member": "category/name"}, ["Books", "Games"]]}
  ]},
  "orderBy": [{"member": "price", "desc": true}, {"member": "name"}],
  "select": {
    "id":    {"member": "id"},
    "total": {"sum": {"source": {"member": "lines"}, "var": "l",
              "body": {"mul": [{"member": "l/qty"}, {"member": "l/price"}]}}},
    "label": {"concat": [{"member": "name"}, " (", {"member": "category/name"}, ")"]},
    "lines": {"map": {"source": {"member": "lines"}, "var": "l",
              "where": {"gt": [{"member": "l/qty"}, 0]},
              "body": {"sku": {"member": "l/sku"}}}}
  },
  "include": ["category"],
  "page": {"mode": "cursor", "after": "eyJ2Ijox...", "take": 20},
  "count": true
}
```

- Node = JSON literal or an object with exactly one key `{"op": args}`. Member paths are `/`-separated; inside a
  lambda the first segment may be the lambda variable (`l/qty`).
- Closed node list:

| Group | Nodes |
|---|---|
| Logic | `and` `or` `not` |
| Compare | `eq` `ne` `gt` `ge` `lt` `le` `in` (null via `eq`/`ne`) |
| Arithmetic | `add` `sub` `mul` `div` `mod` `neg` |
| String | `contains` `startsWith` `endsWith` `toLower` `toUpper` `length` `trim` `concat` |
| Date/time | `year` `month` `day` `hour` `minute` `date` |
| Conditional | `if` [cond, then, else], `coalesce` |
| Collections (lambda) | `any` `all` `count` `sum` `min` `max` `avg`; `map` (+ optional `where`) in select only |

- Typing: the whole tree is type-checked against the model before any `Expression` is built; literals take the type
  of the other operand (`"2026-10-18"` vs `DateOnly`, `10` vs `decimal`). Mismatch → `type_mismatch` + pointer.
- `orderBy`: member paths only (index-friendly, cursor-compatible), each `IsSortable()`.
- `select`: object, nested up to limits; absent → full default DTO (+ includes).
- `include`: navigation paths only, path-depth limited.
- Unknown keys / ops / wrong arity → 400. Nothing is ignored.
- Response `200`: `{"items": [...], "nextCursor": "...", "totalCount": 1342}` (`nextCursor` in cursor mode,
  `totalCount` when requested).

### Client queries

```csharp
public sealed class ShopContext(RestDataContextOptions<ShopContext> options) : RestDataContext(options)
{
    public DtoSet<ProductDto> Products => Set<ProductDto>();
    public DtoSet<CategoryDto> Categories => Set<CategoryDto>();
}

services.AddRestDataContext<ShopContext>(model, http => http.BaseAddress = new("https://shop.example/api/"));
```

`DtoSet<T>` is our own `IQueryable<T>` over `RestQueryProvider : IQueryProvider`; the HttpClient is typed via
HttpClientFactory (auth / resilience handlers are standard).

Supported operator order: `Where*` → `Include*` → `OrderBy/ThenBy*` → `After`/`Skip` → `Take` → `Select`.

| Operator | Maps to |
|---|---|
| `Where` (multiple → `and`) | `filter` |
| `Include(d => d.Category)` | `include` |
| `OrderBy/ThenBy(Descending)` | `orderBy` |
| `Skip` / `Take` / `After(cursor)` | `page` |
| `Select` (anonymous types, records, classes; property names → wire keys, camelCase) | `select` |
| `AsNoTracking()` | client only |

Translated inside lambdas: comparison / logical / arithmetic operators, `?:`, `??`,
`string.Contains/StartsWith/EndsWith/ToLower/ToUpper/Trim/Length/Concat` and `+`, date parts,
`Enumerable.Any/All/Count/Sum/Min/Max/Average/Select/Where`, `list.Contains(d.X)` → `in`. Anything else or
out-of-order → local `NotSupportedException` naming the operator/method, before any HTTP call.

Captured variables: parameter-free subtrees are evaluated client-side into constants.

Terminal operations (async only):

| Call | Request |
|---|---|
| `ToListAsync` / `ToArrayAsync` | page as given; **throws locally without `Take`** |
| `ToCursorPageAsync` | cursor page → `CursorPage<T>` (`Items`, `NextCursor`, `TotalCount?`) |
| `AsAsyncEnumerable()` | follows cursor pages to the end (set must allow cursor mode) |
| `FirstAsync` / `FirstOrDefaultAsync` | `take: 1` |
| `SingleAsync` / `SingleOrDefaultAsync` | `take: 2`, throws on 2 |
| `CountAsync` / `LongCountAsync` | `count: true, take: 0` |
| `AnyAsync` | `take: 1`, `select: {}` |

Synchronous enumeration throws `NotSupportedException("Use ToListAsync")`. Materialization via System.Text.Json
(anonymous types through their constructor). Full-DTO results are tracked; projections never; included
navigations are wired through the identity map.

### Unit of work and `$batch`

Client tracker: identity map per (set, key); snapshot of writable members at materialization; changes detected
by comparison at `SaveChangesAsync` (primitive collections by sequence) — DTOs stay POCOs. API: `Add`, `Remove`,
`Attach`, `Update`, `ctx.Entry(dto).State`, `.Property(...).IsModified`.

Relationships change via FK members (`WithForeignKey`) or by assigning a navigation to a tracked DTO (sets the FK).
FK to an *Added* DTO → `refs` to its operation id. Changing `HasMany` membership without a child FK → local throw.

`POST /api/$batch`:

```json
{"v": 1, "operations": [
  {"id": "0", "op": "insert", "set": "categories", "values": {"name": "Games"}},
  {"id": "1", "op": "insert", "set": "products",   "values": {"name": "Chess", "price": 20},
                                                   "refs": {"categoryId": "0"}},
  {"id": "2", "op": "update", "set": "products",   "key": {"id": 7}, "values": {"price": 12}, "token": "AAAAB9E="},
  {"id": "3", "op": "delete", "set": "products",   "key": {"id": 9}, "token": "AAAAB+A="}
]}
```

`key` is an object (composite keys); updates send modified members only.

Server, in one transaction:
1. Validate every operation against the model (only writable members in `values`); policy `CanAsync` + `Filter`
   (hidden → 404).
2. Insert: new entity, values via reverse mapping, `OnInsert`, `refs` resolved by pointing the navigation at the
   inserted entity (EF orders inserts). Update/delete: load by key through `Filter`, set the token as the
   concurrency token's original value, apply values / remove.
3. One `SaveChanges`; then re-read inserted/updated rows through the default projection.
4. Any failure rolls back everything.

```json
200 {"results": [
  {"id": "0", "value": {"id": 15, "name": "Games"}},
  {"id": "1", "value": {"id": 42, "name": "Chess", "price": 20, "categoryName": "Games", "version": "..."}},
  {"id": "2", "value": {...}}, {"id": "3"}
]}
```

Client on success: copy returned values into tracked instances, re-snapshot, detach deleted. On failure:
`RestSaveException` / `RestConcurrencyException` with the affected entries; tracker state untouched.

## Public API changes

All new (first release):
- **Abstractions:** `IDto`, `IDtoTypeConfiguration<T>`, `DtoTypeBuilder<T>` (+ property / navigation / pagination
  builders), `DtoModelBuilder`, `DtoModel`, `PaginationModes`, AST node types, wire contract types (query, response,
  batch, cursor), error code constants.
- **Client:** `RestDataContext`, `RestDataContextOptions<T>`, `DtoSet<T>`, `RestQueryProvider`, queryable
  extensions (`Include`, `After`, `AsNoTracking`, async terminals, `ToCursorPageAsync`, `AsAsyncEnumerable`),
  `CursorPage<T>`, `EntityEntry<T>`, `EntityState`, `RestQueryException`, `RestSaveException`,
  `RestConcurrencyException`, `AddRestDataContext<T>`.
- **Server:** `IDtoMapping<TDto, TEntity>`, `DtoMappingBuilder<TDto, TEntity>`, `DtoSetPolicy<TDto, TEntity>`,
  `PolicyContext`, `DtoOperation`, `AddLinqToRest`, `MapLinqToRest`, limits options.

Wire contract versioned with `"v": 1`; unknown versions → `unsupported_version`.

## Security notes

- Default limits (server options, enforced during JSON reading): body 64 KB · depth 16 · nodes 500 · lambda
  nesting 3 · `in` 500 · path depth 3 · page default 50 / max 200 (per-set) · skip 10 000 · batch ops 100.
- Allow-listing: DTO is the exposure boundary; entities never serialized. Sorting opt-in. Writes only via direct
  member bindings of the default projection; read-only / key / token / unknown members in `values` → 400.
- Policies: authorization per operation, row filter on reads *and* writes, server-owned values via `OnInsert`.
  Hidden rows → 404.
- Errors: constant titles, stable codes, JSON pointers; no raw values, type names or member names as typed.
  Logs record code / pointer / set, never values.
- Cursor tokens unsigned by design (no access beyond what filters allow); validated for version, sort hash, types.
- QUERY-only: document required proxy / WAF configuration and CORS preflight in the README.

## Testing

| Project | Covers |
|---|---|
| `LinqToRest.Abstractions.UnitTests` | model builder validation, JSON contract round-trips, every limit at and past its boundary |
| `LinqToRest.Client.UnitTests` | LINQ → tree for every supported operator, local `NotSupportedException` cases, change detection, batch building, materialization (stub `HttpMessageHandler`) |
| `LinqToRest.Server.UnitTests` | type checker, error codes + pointers, mapping startup validation |
| `LinqToRest.Server.IntegrationTests` | every node / operator on EF Core SQLite: results equal LINQ-to-Objects on the same data and no client evaluation; offset + cursor paging; batch transactions, refs, concurrency; policies |
| `LinqToRest.EndToEnd.Tests` | `WebApplicationFactory` server + real client: query → modify → save → re-query; error mapping |

## Implementation notes

Where the implementation differs from the sections above, this list wins.

1. **Shapes use an explicit `{"new": {...}}` node.** A plain object in a select position is ambiguous: a field
   named `count` would read as the `count` operator. `"select"` can also be a single expression (`{"member":"name"}`).
2. **Policy filters are predicates.** `Filter` returns `Expression<Func<TEntity, bool>>?` instead of transforming an
   `IQueryable`, so it composes into navigations (hidden reference targets read as `null`), collection lambdas,
   includes, counts, cursors and the rows updates and deletes load. All hooks (`Filter`, `CanAsync`, `OnInsert`,
   `OnUpdate`) are `protected`: consumers override them, only the server calls them.
3. **Filters don't reach inside projections.** A computed member such as `CategoryName = p.Category.Name` reads the
   navigation directly, so the category policy doesn't hide it. Mapping authors keep computed members within what
   the set may show.
4. **More error codes.** Besides the list above: `invalid_query`, `invalid_batch`, `not_found` (404), `forbidden` (403),
   `conflict` (409, a constraint violation on save) and `unsupported_media_type` (415). Problem `type` is
   `urn:linqtorest:error:<code>`. An error on an operator points at its node (`/filter`); its arguments are
   `/filter/gt/0` and so on.
5. **Reading and binding.** The body size is checked before parsing; `JsonDocument` bounds the JSON depth and the
   reader enforces nodes, depth, lambda nesting, `in` lists, path depth and cursor length while building the tree.
   Type checking and expression building are one pass over the tree, still finished before anything executes.
6. **Collection lambdas** all accept an optional `where` (the client folds `Where(...)` chains into it); `min`, `max`
   and `avg` of an empty collection are `null`.
7. **Batches.** Concurrency tokens are compared before saving (so a no-op update or a delete still fails on a stale
   token) and again by EF Core as original values. A foreign key written by value must point at a row the caller
   can see (404 otherwise). The concurrency token must be configured as one in the EF Core model.
8. **Client.** Paging (`After`/`Skip`/`Take`) may come before or after `Select`; filtering, includes and sorting come
   before it. Beyond the listed members, the client translates the `Count` property, `string.Equals` (ignore-case
   compares lowercased), `string.CompareOrdinal`, `char` arguments and the C# 14 span overload of `array.Contains`.
   `RestQueryProvider` is internal. Exceptions expose `Location` (analyzer rule CA1720 rules out `Pointer`) and
   `Entities`.
9. **`POST /{set}/$query` alias (added after v1 review).** ASP.NET Core 10's OpenAPI generator (Microsoft.OpenApi 2.x,
   OpenAPI 3.1) can't describe QUERY, and Microsoft.OpenApi 3.x (which supports OpenAPI 3.2's `query` operation) is
   incompatible with it. So the server also accepts the same body, limits and policies on `POST /{set}/$query`,
   described in OpenAPI together with `$batch`; QUERY is excluded from descriptions. The client keeps using QUERY.
   Requiring `application/json` keeps the POST alias out of reach of cross-site form posts.
10. **Testing.** End-to-end tests host the server with the ASP.NET Core test server directly instead of
   `WebApplicationFactory`, and share the integration tests' shop through linked source files. Integration tests
   capture the executed SQL to prove projections run in the database.
11. **Entity sets (2026-10-20).** `docs/plans/2026-10-20-entity-sets-design.md` replaces the shared DTO model:
   `IDto`, `IDtoTypeConfiguration<T>`, `DtoModelBuilder` and `IDtoMapping<TDto, TEntity>` give way to entity sets
   configured on the server (`IEntitySetConfiguration<TEntity>`, explicit opt-in per member), an exported model
   document and generated clients. Sets are read-only until `AllowWrites(...)`, and `$batch` is mapped only when one
   is writable. The query, `$batch` and cursor wire contracts are unchanged.

## Open questions

- ~~License~~ — MIT.

- ~~PostgreSQL in CI (Testcontainers)~~ — done, see `2026-10-18-postgresql-design.md`.
- `POST /{set}/$query` fallback for environments that block QUERY — demand-driven, post-v1.
- OpenAPI description of QUERY endpoints (tooling support is thin).
- Split `LinqToRest.Server` into EF Core and ASP.NET Core packages, or keep one?
- Client source generation (DTO JSON context, AOT-friendly materialization) — v2?
- Null-aware keyset cursors; owned/complex DTO types; `HasMany` membership changes without FK.
- Package IDs and NuGet ownership / naming check.

## Implementation steps

1. Update `CLAUDE.md`, `security` and `dotnet-conventions` skills to this design (both ends, QUERY + JSON, server-only
   no-`Compile` rule, new layout).
2. Scaffold projects (`src/LinqToRest.{Abstractions,Client,Server}`, test projects, `samples/` placeholders), add to
   `LinqToRest.slnx`, add EF Core / SQLite package versions.
3. Abstractions: `IDto`, `DtoTypeBuilder<T>`, `IDtoTypeConfiguration<T>`, `DtoModelBuilder` (+ assembly scan),
   `DtoModel` with `Build()` validation; tests.
4. Abstractions: AST node types + limit-enforcing JSON reader / writer, error codes; tests.
5. Abstractions: response, batch and cursor contracts; tests.
6. Server: `IDtoMapping` / `DtoMappingBuilder`, reverse-binding analysis, startup validation; tests.
7. Server: type checker → typed AST with pointers; tests.
8. Server: AST → `Expression` for filter / orderBy / offset paging with member substitution; SQLite integration
   tests per node.
9. Server: select (incl. `map`), include, count; integration tests.
10. Server: cursor pagination; integration tests.
11. Server: `AddLinqToRest` / `MapLinqToRest`, QUERY endpoint, ProblemDetails mapping, limits options.
12. Server: policies (`CanAsync`, `Filter`, `OnInsert`); integration tests.
13. Server: `$batch` executor (transaction, refs, concurrency, re-read); integration tests.
14. Client: `RestDataContext`, `DtoSet<T>`, `RestQueryProvider`, LINQ → AST for where / orderBy / paging + captured
    variable evaluation; unit tests.
15. Client: select / include translation, materialization, terminal operators, `ToCursorPageAsync`,
    `AsAsyncEnumerable`, error → exception mapping; unit tests.
16. Client: change tracker, identity map, `SaveChangesAsync`, refs; unit tests.
17. End-to-end tests (`WebApplicationFactory` + real client).
18. Samples (`Shop.Contracts`, `Shop.Api`, `Shop.Console`) and README walkthrough (incl. QUERY proxy / CORS notes).
