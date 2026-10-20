# Entity sets and generated clients — design
Status: approved, implemented (see Implementation notes) · Date: 2026-10-20

Supersedes the shared-DTO model of `2026-10-18-linqtorest-v1-design.md` (sections *Shared model*, *Server mapping and
hosting*, the client registration in *Client queries*) and the assembly input of
`2026-10-18-typescript-client-design.md`. The query, `$batch` and cursor wire contracts are unchanged.

## Problem

Exposing a set today takes three pieces kept in step by hand: a DTO class marked `IDto` in a shared contracts
assembly, its `IDtoTypeConfiguration<T>`, and a server-side `IDtoMapping<TDto, TEntity>` whose `Project(...)` must
assign every DTO member. For the common case the DTO mirrors the entity, so most of this is ceremony. Clients also
need a .NET contracts assembly, even the TypeScript generator, which loads it by reflection.

Writes are on for every set: `MapLinqToRest` always maps `POST /$batch`, and the only brake is overriding
`CanAsync` in a policy per set. An API that only wants to be queried has to remember to deny writes everywhere.

## Goals / Non-goals

**Goals**
- The server configures EF Core entities directly; only members, computed values and navigations it opts in are
  exposed.
- Sets are read-only by default; each set opts into insert, update and/or delete. `$batch` exists only when some set
  allows writes.
- The configuration is exported as a versioned, CLR-free **model document**; the .NET and TypeScript clients are
  generated from it.
- Remove `IDto`, `IDtoTypeConfiguration<T>`, the client-side `DtoModelBuilder`, `IDtoMapping<TDto, TEntity>` and
  shared contracts assemblies.
- Query, `$batch` and cursor JSON stay byte-for-byte the same (golden files unchanged).

**Non-goals**
- Keeping the shared-DTO mode alongside (one model, one code path).
- A Roslyn source generator for the .NET client (possible later on top of the CLI generator).
- A runtime drift check between client and server models.
- Custom projections that aren't expressible as `Computed` members on one entity.

## Constraints check

| Constraint | This design |
|---|---|
| **Translatable** | Unchanged in kind: a member access becomes the entity member or the configured `Computed` expression instead of the `Project(...)` binding. Rows are still projected to `object?[]` by EF Core. Every existing SQLite/PostgreSQL integration test carries over. |
| **Untrusted input** | Explicit opt-in keeps the allow-list guarantee: an entity column that isn't configured doesn't exist on the wire, including columns added later. Read-only sets by default shrink the write surface. Writes only reach members bound directly with `Property(...)`; computed members, keys and tokens are never writable. The model document has size and count limits while reading. `GET /$model` reveals the schema, so it's opt-in and behind the route group's auth. |
| **No dynamic code** | Unchanged. `Computed` expressions are server-developer code, substituted as expression trees; no `Compile()` on the request path, no generated runtime types. |
| **Wire contract** | Query/batch/cursor shapes unchanged, so their `"v"` stays 1. New contract: the model document (`{"v": 1, ...}`). New error code `operation_not_allowed`. |
| **Public API** | Breaking, approved: DTO-based types removed or renamed (see *Public API changes*). |
| **Dependencies** | Abstractions and Client stay BCL-only (`RestModel` uses System.Text.Json). Entity configuration lives in Server (EF Core). The generator stays a console app. |
| **Errors** | Writing to a set that doesn't allow the operation → 403 `operation_not_allowed` with pointer `/operations/N`, constant title. The .NET client throws locally before any request; in TypeScript it's a compile error or a rejected `saveChanges` before `fetch`. |

## Chosen approach

**CLI generators from an exported model document.** The server builds its model from
`IEntitySetConfiguration<TEntity>` classes; the app exports it as JSON from inside its real host; one generator tool
(`linqtorest-gen`) emits C# and TypeScript clients from that file, and generated files are checked in and verified in
CI.

- *Not a Roslyn source generator:* auto-regenerates, but adds an analyzer package, harder debugging and a .NET path
  that diverges from TypeScript. Possible later as an addition.
- *Not generating from the server DLL:* loading EF Core, ASP.NET Core and the app's dependencies in a CLI is fragile.
- *Not hand-written client classes:* duplicates the configuration on the client.
- *Not "all scalars exposed, opt out":* a new sensitive column would leak automatically.
- *Not a global `EnableBatch()` switch:* all-or-nothing; per-set operations say exactly what's writable.

## Design

### Server configuration

```csharp
public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> b)
    {
        b.ToSet("products").FromSource<ShopDbContext>(db => db.Products);
        b.HasTypeName("Product");                  // generated class name; defaults to the entity name
        b.HasKey(p => p.Id);                       // defaults to EF's primary key
        b.HasConcurrencyToken(p => p.RowVersion).HasName("version");
        b.Property(p => p.Name);                   // exposed, filterable, writable
        b.Property(p => p.Price).IsSortable();
        b.Property(p => p.Status);
        b.Property(p => p.CategoryId);
        b.Computed("categoryName", p => p.Category!.Name);
        b.Computed("tags", p => p.Tags.Select(t => t.Value).ToList());
        b.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        b.AllowWrites(WriteOperations.Insert | WriteOperations.Update);
        b.HasPagination(p => p.DefaultPageSize(50).MaxPageSize(200));
    }
}

builder.Services.AddLinqToRest(o => o
    .AddSetsFromAssembly(typeof(ProductSet).Assembly)
    .UseDbContext<ShopDbContext>()
    .Limits(l => l.MaxNodes(500)));

app.MapLinqToRest("/api").RequireAuthorization();   // QUERY {set}, POST {set}/$query, POST $batch if any set is writable
```

- **Exposure:** only configured members, computed values and navigations exist on the wire. Wire names are camelCase
  of the member name (computed members name themselves); `HasName` renames.
- **Access defaults** carry over: filterable by default (`IsNotFilterable()`), sortable opt-in (`IsSortable()`, keys
  always), path depth limited.
- **Writability:** a set is read-only until `AllowWrites(...)`. In a writable set, `Property(...)` members are
  writable unless `IsReadOnly()`; keys, tokens and `Computed` members never are. `HasMany` / `HasOne` targets must be
  configured sets.
- **Policies:** `SetPolicy<TEntity>` (was `DtoSetPolicy<TDto, TEntity>`) with the same protected hooks — `Filter`,
  `CanAsync(SetOperation, ...)`, `OnInsert`, `OnUpdate`. A set's `writes` are checked before `CanAsync`.
- **Startup validation (fail fast):** every `Property` is an EF-mapped scalar property of a supported type; key and
  token are plain mapped properties and the token is a concurrency token in the EF model; navigation targets are
  configured sets with a matching EF navigation; `Computed` returns a supported scalar or collection of one; set,
  type and member names are unique and valid; the source is an `IQueryable<TEntity>` of the configured DbContext.

### Model document

```json
{
  "v": 1,
  "enums": {"ProductStatus": ["Draft", "Active", "Retired"]},
  "sets": [{
    "name": "products", "type": "Product",
    "key": ["id"], "concurrencyToken": "version",
    "writes": ["insert", "update"],
    "pagination": {"modes": ["offset", "cursor"], "defaultPageSize": 50, "maxPageSize": 200, "maxSkip": 10000},
    "properties": [
      {"name": "id", "type": "int32", "sortable": true},
      {"name": "name", "type": "string", "writable": true},
      {"name": "price", "type": "decimal", "writable": true, "sortable": true},
      {"name": "status", "type": "ProductStatus", "writable": true},
      {"name": "version", "type": "guid"},
      {"name": "categoryId", "type": "int32", "nullable": true, "writable": true},
      {"name": "categoryName", "type": "string", "nullable": true},
      {"name": "tags", "type": "string", "collection": true}
    ],
    "navigations": [{"name": "category", "target": "categories", "foreignKey": ["categoryId"], "nullable": true}]
  }]
}
```

- Scalar types are the closed list matching `ScalarTypes`: `bool`, `uint8`, `int16`, `int32`, `int64`, `float32`,
  `float64`, `decimal`, `string`, `guid`, `dateTime`, `dateTimeOffset`, `dateOnly`, `timeOnly`, `timeSpan`, `bytes`;
  anything else must name an entry of `enums` (enums travel as names, as today).
- False flags are omitted (`writable`, `sortable`, `nullable`, `collection`); `"filterable": false` is written only
  when opted out. Sets, members and enums are written in a stable order so diffs stay small.
- No entity member names, computed expressions, namespaces or assembly names: only what the wire already exposes,
  plus each set's type name, which defaults to the entity's class name (`HasTypeName` sets another).
- `RestModel` (Abstractions, BCL-only) parses and writes it, rejecting unknown keys, types and versions, and enforcing
  size/count limits. The server produces its document through it; generated clients embed it.

**Export** runs in the real host (same DI, EF model and configuration as production):

```csharp
var app = builder.Build();
if (args is ["--export-linqtorest-model", var path])
{
    await app.ExportLinqToRestModelAsync(path);   // builds and validates the model, writes it, returns
    return;
}
```

`GET {prefix}/$model` is opt-in (`MapLinqToRest(...).WithModelEndpoint()`): same bytes, `ETag`, under the group's
auth.

### Generated clients

`src/LinqToRest.TypeScript` becomes `src/LinqToRest.CodeGen` (`linqtorest-gen`), reading the model document:

```
dotnet run --project src/LinqToRest.CodeGen -- ts --model model.json --name Shop [--import ...] --out shop.generated.ts [--check]
dotnet run --project src/LinqToRest.CodeGen -- cs --model model.json --namespace Shop.Client --context ShopContext --out ShopContext.generated.cs [--check]
```

**C#:** `sealed partial` POCO classes per set (user members in another file are ignored by translation and
tracking), enums, the context and a registration extension:

```csharp
public sealed partial class ShopContext(RestDataContextOptions<ShopContext> options) : RestDataContext(options)
{
    public RestSet<Product> Products => Set<Product>();
    public RestSet<Category> Categories => Set<Category>();

    public static ClientModel Model { get; } = ClientModel.Bind(RestModel.Parse("""{"v":1,...}"""),
        new Dictionary<string, Type> { ["products"] = typeof(Product), ["categories"] = typeof(Category) });
}

public static class ShopContextServiceCollectionExtensions
{
    public static IHttpClientBuilder AddShopContext(this IServiceCollection services, Action<HttpClient> configure)
        => services.AddRestDataContext<ShopContext>(ShopContext.Model, configure);
}
```

`ClientModel.Bind` replaces the client-side `DtoModelBuilder`: it matches wire names to public properties, checks CLR
types against the scalar types, and fails at startup on a mismatch.

**TypeScript:** same output as today, with `DtoSet` renamed to `RestSet` and each set's type carrying its allowed
operations.

**Write permissions on the client** (from the set's `writes`):

| | .NET | TypeScript |
|---|---|---|
| `Add` without `insert` | `InvalidOperationException` at the call | `add` not on the set's type (compile error) |
| `Remove` without `delete` | `InvalidOperationException` at the call | `remove` not on the set's type |
| Modified tracked entity without `update` | `SaveChangesAsync` throws before sending | `saveChanges` rejects before `fetch` |
| Non-writable member | existing local check | `readonly` in the generated type |

### Server internals

The server never materializes DTOs: queries project to `object?[]` rows written by `Shape`s, and `SetMapping` already
maps members to entity lambdas (`Bindings`) and write targets (`EntityProperties`). So:

- `DtoType` / `DtoProperty` / `DtoNavigation` give way to internal `ServerSet` / `ServerMember` / `ServerNavigation`
  built from `EntitySetBuilder<T>`; a member's CLR type is its binding lambda's return type.
- `QueryBinder`, `QueryCompiler`, `Shapes`, cursors and the batch executor keep their behaviour on the new member
  types; generated SQL is unchanged.
- The batch executor rejects an operation the set doesn't allow (403 `operation_not_allowed`, `/operations/N`) before
  loading rows or calling policies. Reverse binding is driven by `Property(...)` instead of analysing `Project(...)`.
- `MapLinqToRest` maps `$batch` only when some set allows writes; otherwise `POST $batch` is a plain 404.

## Public API changes

All breaking, approved here.

- **Abstractions:** removed `IDto`, `IDtoTypeConfiguration<T>`, `DtoTypeBuilder<T>` and its property / reference /
  pagination builders, `DtoModelBuilder`, `DtoModel`, `DtoType`, `DtoProperty`, `DtoNavigation`, `DtoPagination`.
  Added `RestModel` (+ set, property, navigation, pagination descriptions; `Parse`, `Write`), `WriteOperations`.
  `PaginationModes`, protocol and error code types stay (adds `operation_not_allowed`).
- **Client:** `DtoSet<T>` → `RestSet<T>`; added `ClientModel` (`Bind`); `RestDataContextOptions` and
  `AddRestDataContext<T>` take a `ClientModel`; `EntityEntry<T>` and the queryable extensions drop the `IDto`
  constraint.
- **Server:** removed `IDtoMapping<TDto, TEntity>`, `DtoMappingBuilder<TDto, TEntity>`, `AddMapping(s)...`;
  added `IEntitySetConfiguration<TEntity>`, `EntitySetBuilder<TEntity>` (+ property, computed, navigation builders),
  `AddSet` / `AddSetsFromAssembly`, `ExportLinqToRestModelAsync`, `WithModelEndpoint()`.
  `DtoSetPolicy<TDto, TEntity>` → `SetPolicy<TEntity>`, `DtoOperation` → `SetOperation`.
- **Generator:** `linqtorest-ts --assembly` → `linqtorest-gen ts|cs --model`.

## Security notes

- The exposed set is the boundary: unconfigured entity members are unknown (`unknown_member`), exactly like members
  missing from a DTO today. Entities are still never serialized; rows are written from configured members only.
- Read-only by default. A write needs both the set's `writes` and the policy's `CanAsync`; the set check runs first
  and doesn't load rows.
- Writes only reach members bound with `Property(...)`; computed, key and token members are rejected as today.
- `Computed` expressions read whatever they reference, outside policy filters (as implementation note 3 of the v1
  design says for projections); the security skill keeps that warning for set configurations.
- The model document reveals sets, members and types. `GET /$model` is off by default and under the route group's
  auth. The exported file contains no server internals.
- `RestModel.Parse` is fed developer files on the client and generator, but it still enforces limits and rejects
  unknown content.

## Testing

| Project | Covers |
|---|---|
| `LinqToRest.Abstractions.UnitTests` | `RestModel` round-trip, unknown keys / types / versions rejected, every limit at and past its boundary |
| `LinqToRest.Server.UnitTests` | entity configuration validation (each startup failure), model export equals `tests/golden/model.json` |
| `LinqToRest.Server.IntegrationTests` | Shop on entity sets; every existing node / operator / paging / batch / policy test on SQLite and PostgreSQL; unconfigured column → `unknown_member`; operation not in `writes` → 403 with pointer and nothing saved; no writable set → `$batch` 404 |
| `LinqToRest.Client.UnitTests` | `ClientModel.Bind` mismatches; local throws for `Add` / `Remove` / modified entity without the operation; golden request bodies unchanged |
| `LinqToRest.CodeGen.UnitTests` | C# and TypeScript output from the golden model, `--check` behaviour |
| `LinqToRest.EndToEnd.Tests` | generated `ShopContext` against the server: query → modify → save → re-query, write permission errors |
| TypeScript (`npm run check`, `test:e2e`) | generated sets' types (type tests for missing `add` / `remove`), e2e against `Shop.Api` |

`LinqToRest.TestContracts` is removed; client tests use code generated from the golden model. CI re-exports
`samples/Shop.Api/model.json`, regenerates every generated file with `--check`, and fails on any difference.

## Implementation notes

Where the implementation differs from the sections above, this list wins.

1. **Steps 3–5 landed together.** The server can't keep `IDtoMapping` once sets are configured on entities, and
   `DtoSetPolicy<TDto, TEntity>` needs a DTO type the server no longer has, so entity sets, the server internals and
   the policy rename are one change. The client keeps its DTO model until step 7; the wire is the same, so the
   end-to-end tests run unchanged in between.
2. **Validation needs the EF Core model**, which needs a `DbContext`, so `AddLinqToRest` registers the server model as
   a singleton built from DI, and `MapLinqToRest` resolves it: a bad configuration still fails at startup.
3. **Members:** key properties are exposed automatically and come first, in key order; the other members keep the
   order they were configured in. `Property(...)` takes nullability from the EF Core model. `Computed(...)` values of
   reference type are nullable unless marked `IsRequired()` (the expression's nullability can't be inferred);
   collections are never null. Navigation targets must be the only set of their entity type. `WithForeignKey` must
   name an exposed property that is the EF Core foreign key of that navigation.
4. **Scalar type names:** `byte` is `uint8` (it's unsigned), not `int8`.
5. **Two golden models.** `tests/golden/model.json` comes from sets in `tests/LinqToRest.Server.UnitTests/Golden` that
   mirror the client test contracts, so the golden request bodies stay unchanged.
   `tests/LinqToRest.Server.IntegrationTests/Shop/model.json` is the integration shop's model, which the end-to-end
   client is generated from. Both are rewritten with `LINQTOREST_UPDATE_GOLDEN=1`.
6. **No `$batch` without writable sets** means no handler: `POST {prefix}/$batch` then only matches the
   `QUERY {prefix}/{set}` route by path, so ASP.NET Core routing answers 405 rather than 404.
7. **Generator:** `linqtorest-gen` stays a dotnet tool (`LinqToRest.CodeGen`), as `linqtorest-ts` was. The model
   document has no notion of `[Flags]` enums, so a flags enum is generated as the union of its member names, like
   any other enum (the assembly-based generator emitted `string`). The TypeScript client keeps one runtime set class
   with every write method (each checks `writes`); the exported `RestSet<D, P, W>` type only exposes the allowed ones.
8. **Generated C# clients:** the generated context's static `Model` replaces `RestDataContext.Model`, which is
   gone (an instance property of the same name would make `context.Model` ambiguous). Generated files: the client
   tests' `ShopContext` (golden model, also the generator's C# snapshot), the end-to-end tests' `ShopClient` (the
   integration shop's model) and the benchmarks' `BenchmarkShop` (golden model, in its own namespace so it doesn't
   shadow the linked shop entities). CI checks them with `--check`.
9. **Formatting:** the model document is indented by two spaces, with every object or array that fits in 120 columns
   on one line, so each property is one line in diffs.
10. **Performance.** Before/after on the same machine, `main` and this branch alternated twice (DefaultJob): query
   binding, batch execution, client translation, materialization and change detection are all within ±5% (noise
   level) with the same or fewer allocations; query execution and the endpoint were unchanged in a `--job short` run.
   A first run found translation 11% slower and allocating more: the bound model's lists were compiler-generated
   read-only wrappers (collection expressions typed `IReadOnlyList<T>`), which LINQ can't treat as spans. The
   server and client model lists are arrays now.

## Open questions

- Ship `linqtorest-gen` as a `dotnet tool` package?
- Runtime drift check: compare the client's embedded model hash with `GET /$model`'s ETag.
- Roslyn source generator for the .NET client (approach B) as an addition.
- Composite foreign keys in `WithForeignKey` (the document allows a list; v1 only supports one member).

## Implementation steps

1. Update `CLAUDE.md` (layout, "the exposed set is the boundary", generator commands), the `security`,
   `dotnet-conventions` and `typescript-client` skills, and add an implementation note to the v1 and TypeScript
   designs pointing here.
2. Abstractions: `RestModel` + description types, `WriteOperations`, `Parse` / `Write` with limits, error code
   `operation_not_allowed`; unit tests.
3. Server: `IEntitySetConfiguration<T>`, `EntitySetBuilder<T>` (property, computed, navigation, pagination, writes),
   startup validation, model production; unit tests incl. golden `model.json`.
4. Server: switch `ServerModel` / `SetMapping` / binder / compiler / shapes / cursors to `ServerSet` members; port the
   integration tests' Shop to entity sets; all integration tests green on both providers.
5. Server: batch executor on the new model, `writes` check (403), `$batch` mapped only when writable;
   `SetPolicy<TEntity>` / `SetOperation`; integration tests.
6. Server: `ExportLinqToRestModelAsync`, `WithModelEndpoint()` (ETag, auth); tests.
7. Client: `ClientModel.Bind`, `RestSet<T>`, options / registration on `ClientModel`, write-permission checks in the
   tracker; unit tests with golden bodies unchanged.
8. CodeGen: rename the project to `LinqToRest.CodeGen`, read the model document, `ts` target (incl. `RestSet` and
   per-set operations in the TypeScript client); tests.
9. CodeGen: `cs` target (classes, enums, context, registration extension); golden tests; regenerate test clients and
   delete `LinqToRest.TestContracts`.
10. End-to-end tests on the generated `ShopContext`.
11. Remove `IDto`, `IDtoTypeConfiguration<T>`, `DtoModelBuilder` and the DTO model types, `IDtoMapping` /
    `DtoMappingBuilder`; fix everything that referenced them.
12. Samples: `Shop.Api` entity sets + checked-in `model.json` + export switch; `Shop.Contracts` → generated
    `Shop.Client`; `Shop.Console` and `shop-ts` on generated clients; README walkthrough.
13. CI: export the model and run both generators with `--check`; benchmarks updated to the new model and a
    before/after run of the query and batch suites (hot paths touched in steps 4–5).
