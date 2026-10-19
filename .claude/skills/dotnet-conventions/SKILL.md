---
name: dotnet-conventions
description: Conventions and scaffolding for the LinqToRest .NET solution — package boundaries (Abstractions / Client / Server), build props, central package management, analyzers, library packaging and public API rules, JSON contract handling, xUnit v3 test layout (unit, SQLite translation, end-to-end), samples, and how to add a project. Use when creating or modifying anything under src/, samples/ or tests/, or any .csproj/.props file.
---

# .NET conventions

## Build (`Directory.Build.props`, inherited by every project)
- net10.0, nullable, implicit usings, `LangVersion=latest`, **warnings as errors**.
- `AnalysisLevel=latest-recommended` + `EnforceCodeStyleInBuild`: `.editorconfig` style is enforced by the build.
  Fix findings rather than suppressing; if a suppression is unavoidable, scope it (`[SuppressMessage]` with
  `Justification`) and never globally.
- `UseArtifactsOutput` with `ArtifactsPath` pinned to the repo root: output goes to
  `artifacts/{bin,obj,publish,package}/<Project>/<config>`. There are no per-project `bin/obj` folders.
- `.csproj` files never set `TargetFramework`, `Nullable`, versions, or anything already in the props.

## Packages
- Versions only in `Directory.Packages.props` (`<PackageVersion>`); `.csproj` uses `<PackageReference Include="X" />`.
- Free/open-source only. `LinqToRest.Abstractions` and `LinqToRest.Client` reference **BCL only**
  (System.Text.Json, HttpClient; `Microsoft.Extensions.*` abstractions for DI/HttpClientFactory are the one
  exception for Client). EF Core and ASP.NET Core belong in `LinqToRest.Server`, samples and tests.

## Package boundaries
- `Abstractions`: model (`IDto`, builders, `DtoModel`), AST node types, wire contracts (query, response, batch,
  cursor), error codes. No HTTP, no EF, no LINQ translation.
- `Client`: LINQ → AST, HTTP transport (QUERY, `$batch`), materialization, change tracking. Never references Server.
- `Server`: mapping, type checking, AST → `Expression`, batch executor, policies, endpoints. Never references Client.
- Anything both ends need (e.g. a node type, a cursor format, an error code) goes in Abstractions.

## Library projects (`src/`)
- Packable: set `IsPackable`, `PackageId`, `Description`, `PackageReadmeFile`, `PackageLicenseExpression` in the
  csproj; `GenerateDocumentationFile=true` so every public member has XML docs (CS1591 is an error under
  warnings-as-errors — that's intended).
- Keep the public surface small: `internal` by default, `sealed` classes unless designed for inheritance,
  `InternalsVisibleTo` for test projects instead of making things public.
- Server expression building: `System.Linq.Expressions` nodes only, from the type-checked AST. Resolve members via
  `DtoModel` / mappings, never `Type.GetProperty(clientString)`. No `Expression.Compile()` on the request path.
  The client may compile captured-variable subtrees (developer code).
- JSON: System.Text.Json with a strict source-generated `JsonSerializerContext` for batch contracts; the query tree is
  read from a size-capped `JsonDocument` by a hand-written reader that enforces the limits while building nodes.
  Wire values go through `JsonValues` (strict, enums by name). camelCase on the wire.
- Inspections: test, sample and src fixtures/public API have `.Global` inspections turned off in `.editorconfig`;
  anything else gets fixed. Suppress a single deliberate case with `// ReSharper disable once <Id> — <why>`
  (get the id from `--format=Sarif`).
- Async: every I/O API is async with a `CancellationToken`; no sync-over-async. `ConfigureAwait(false)` in `src/`.
- Options: immutable options objects with safe defaults (max depth, max nodes, max page size); validate at
  configuration time.
- Errors: server-side failures carry an error code (constants in Abstractions) + JSON pointer, never raw values;
  endpoints map them to ProblemDetails. Client maps responses to `RestQueryException` / `RestSaveException` /
  `RestConcurrencyException`.
- Time: inject `TimeProvider` if anything needs "now"; tests use `FakeTimeProvider`.

## Samples (`samples/`)
- `Shop.Contracts` (DTOs + configuration), `Shop.Api` (EF Core SQLite, API-key auth, tenant policies,
  `RequireAuthorization`), `Shop.Console` (client: query, cursor paging, save). `samples/Directory.Build.props` makes
  them non-packable. Keep them simple — they're documentation, and people copy them.
- Run: `dotnet run --project samples/Shop.Api --urls http://localhost:5080` (Development, key `dev-acme-key`), then
  `dotnet run --project samples/Shop.Console`.

## Tests (`tests/`)
- `tests/Directory.Build.props` adds xUnit v3, `OutputType=Exe`, `IsPackable=false`, global `using Xunit`.
  Mocks: NSubstitute. Names use underscores (CA1707 off in tests).
- One test project per package: `LinqToRest.<Package>.UnitTests` (Client uses a stub `HttpMessageHandler`).
- `LinqToRest.Server.IntegrationTests`: every AST node / operator executed on EF Core SQLite — assert results equal
  LINQ-to-Objects over the same data **and** that nothing evaluated on the client. LINQ-to-Objects alone isn't proof.
- `LinqToRest.EndToEnd.Tests`: the server on the ASP.NET Core test server (`UseTestServer`) + the real client (query →
  modify → save → re-query, status codes, ProblemDetails, typed client exceptions). It links the integration tests'
  `Shop/*.cs` (entities, DTOs, mappings, policies, seed) instead of copying them.
- To prove an expression runs in SQL, assert on `ShopServer.Sql` (every executed command), not just on results.
- Every limit gets a test at and just past its boundary.
- Provider coverage: integration and end-to-end test classes are `abstract` and take a `ShopFixture` / `ShopDatabase`;
  each gets two one-line subclasses, `Sqlite<Name>` and `Postgres<Name>`, using `SqliteShopFixture` /
  `PostgresShopFixture` or `SqliteDatabase` / `PostgresDatabase(PostgresContainer)`. A new test class needs both
  subclasses. Never start or skip a provider inside a fixture's `InitializeAsync`; reach the server through the lazy
  `Server` property, which skips when PostgreSQL isn't available.
- Results that differ between providers are a library bug (for example, unordered collections) unless the database
  defines them differently (collation, null ordering). Then the test uses values both providers agree on.

## Commands
- Test: `dotnet test --solution LinqToRest.slnx` (Microsoft Testing Platform via `global.json`); one project:
  `dotnet test --project tests/<Project>`
- Format: `dotnet format LinqToRest.slnx --verify-no-changes` (drop the flag to fix)
- Pack: `dotnet pack LinqToRest.slnx -c Release` → `artifacts/package/`
- Rider/ReSharper inspections (tool pinned in `dotnet-tools.json`; `dotnet tool restore` first):
  `dotnet jb inspectcode LinqToRest.slnx --format=Text --output=- --no-build [--include="src/X/**"]`.
  Default minimum severity is SUGGESTION — fix those too, not just warnings. Text output has no severity column;
  use `--format=Sarif --output=artifacts/inspect.sarif` when severity matters. When Rider is connected,
  `mcp__ide__getDiagnostics` with the file URI gives the live editor highlights faster.
- Severities: `.editorconfig` `resharper_<inspection_id>_highlighting = none|hint|suggestion|warning|error`.
  `src/**` already turns off the "never used" inspections for public API (consumers use it, the solution doesn't).

## Adding a project
1. Create the `.csproj` under `src/`, `samples/` or `tests/` (no `TargetFramework`/versions — inherited).
2. `dotnet sln LinqToRest.slnx add --solution-folder src <path>` (or `samples` / `tests`).
3. Test projects reference production projects as `..\..\src\<Project>\<Project>.csproj` and inherit xUnit from
   `tests/Directory.Build.props`.
4. Add any new package versions to `Directory.Packages.props`.
