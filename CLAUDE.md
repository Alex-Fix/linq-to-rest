# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

LinqToRest covers both ends of a .NET REST API. Design: `docs/plans/2026-10-18-linqtorest-v1-design.md`, with the
model reworked by `docs/plans/2026-10-20-entity-sets-design.md` (which wins where they differ) — read them before
changing behaviour or the wire contract.

- **Server:** EF Core entities configured as sets (`IEntitySetConfiguration<TEntity>`), exposing only the members,
  computed values and navigations they opt in; sets are read-only until `AllowWrites(...)`. Validates each query tree
  against that model and translates it onto the entities; applies batches in one transaction.
- **Model document:** the server exports its model as versioned JSON (`RestModel`); clients are generated from it.
- **Client:** a generated `RestDataContext` of `RestSet<T>` (our own `IQueryable<T>` / `IQueryProvider` — never EF's)
  translates LINQ into a JSON expression tree sent with HTTP **QUERY**; full unit of work (change tracking,
  identity map) saved as one atomic `POST /$batch`.

Every query body and batch reaching the server is untrusted — see the `security` skill.

## Layout (target — create projects here, don't invent new top-level dirs)

```
LinqToRest.slnx                 solution (+ global.json, Directory.Build.props, Directory.Packages.props)
src/
  LinqToRest.Abstractions/      RestModel (model document), query AST + JSON contract, batch/cursor contracts (BCL only)
  LinqToRest.Client/            RestDataContext, RestSet<T>, ClientModel, RestQueryProvider, change tracker (BCL only)
  LinqToRest.Server/            entity sets, AST → Expression, batch executor, policies, endpoints, model export (EF Core)
  LinqToRest.CodeGen/           linqtorest-gen (dotnet tool): C# and TypeScript clients from a model document
clients/typescript/              npm package "linqtorest": TypeScript client (same wire protocol, golden-tested)
samples/                        Shop.Api (EF Core SQLite, API key, model.json), Shop.Client (generated), Shop.Console,
                                shop-ts: the minimal sample. Catalog/ (Catalog.Api, Catalog.Client, Catalog.Blazor,
                                catalog-web): the realistic one. Not packable
tests/                          xUnit v3, one project per package + Server.IntegrationTests (SQLite) + EndToEnd.Tests
                                (EndToEnd links the integration tests' Shop/*.cs) + Samples.Tests (the catalog sample)
benchmarks/                     BenchmarkDotNet suites for every src/ project (not packable)
docs/plans/                     approved design docs (written by the `brainstorming` skill)
artifacts/                      gitignored: all build output (no per-project bin/obj)
```

## Commands

- Test: `dotnet test --solution LinqToRest.slnx` (Microsoft Testing Platform via `global.json`, so pass
  `--solution`/`--project` explicitly). Integration and end-to-end tests run on SQLite and PostgreSQL; PostgreSQL needs
  Docker (Testcontainers) and is skipped without it, so check the `skipped` count. `LINQTOREST_REQUIRE_POSTGRES=1`
  (set in CI) makes a missing PostgreSQL fail instead.
- Format check: `dotnet format LinqToRest.slnx --verify-no-changes`
- Rider/ReSharper inspections: `dotnet tool restore` once, then
  `dotnet jb inspectcode LinqToRest.slnx --format=Text --output=- --no-build` (~15s+; drop `--no-build` if not
  just built; `--include="src/Foo/**"` to narrow)
- Add a project: `dotnet sln LinqToRest.slnx add --solution-folder src <path>` (or `samples` / `tests`)
- Benchmarks: `dotnet run -c Release --project benchmarks/LinqToRest.Benchmarks -- --filter '*Query*' [--job short]`
  (results in `artifacts/benchmarks/`). Changes to hot paths (reading, binding, execution, writing, client
  translation/materialization/tracking) need a before/after run of the affected suites.
- TypeScript client (`cd clients/typescript`): `npm ci`, then `npm run check` (Prettier, ESLint strict type-checked,
  `tsc`, Vitest incl. type tests, build) and `npm run test:e2e` (starts `samples/Shop.Api` on SQLite); `npm run bench` runs the Vitest
  benchmarks (results in `artifacts/typescript-bench.json`). Request bodies
  must match `tests/golden/*.json`, which the .NET client tests also assert (`LINQTOREST_UPDATE_GOLDEN=1` regenerates
  them from the .NET client). Conventions: `typescript-client` skill.
- Model document: `dotnet run --project samples/Shop.Api -- --Database:Provider=Sqlite --export-linqtorest-model
  model.json` (relative to `samples/Shop.Api`). Test models: `LINQTOREST_UPDATE_GOLDEN=1` rewrites `tests/golden/model.json`
  and `tests/LinqToRest.Server.IntegrationTests/Shop/model.json`.
- Generated clients (never edit by hand): `dotnet run --project src/LinqToRest.CodeGen -- ts --model <model.json>
  --name Shop [--import ../src/index.js] --out <file>` and `... -- cs --model <model.json> --namespace <ns> --context
  <Name>Context --out <file>`; CI re-exports the model and runs both with `--check` for every checked-in generated
  file.

## Rules

1. **Translatable expressions only.** Server-built expressions must translate on EF Core; anything that would fall
   back to client evaluation is a bug. Prove it with SQLite integration tests, not LINQ-to-Objects alone.
2. **Closed AST, no dynamic code on the server.** The server only accepts the documented node list and builds
   `Expression` nodes itself — never deserialized .NET expressions, `System.Linq.Dynamic.Core`, Roslyn scripting or
   `Expression.Compile()` on the request path. (The client may compile/evaluate captured variables: that's the
   developer's own code.)
3. **The exposed set is the boundary.** Only configured entity members exist on the wire; entities are never
   serialized. Sorting is opt-in per member; sets are read-only until `AllowWrites(...)`, and writes only go through
   members bound with `Property(...)`. Every size is limited (body, depth, nodes, page, batch, model…).
4. **Wire contract and public API are versioned.** The JSON contract carries `"v"`; breaking changes to it or to
   packable `src/` APIs need explicit approval and a design-doc update.
5. **Free and open.** Free/open-source dependencies only; Abstractions and Client stay BCL-only.

## Conventions

- Build (`Directory.Build.props`): net10.0, nullable, warnings as errors, `AnalysisLevel=latest-recommended`,
  `EnforceCodeStyleInBuild`, artifacts output. Fix analyzer findings rather than suppressing them.
- Rider inspections count too: before calling C# work done, resolve every ReSharper error, warning **and
  suggestion** in changed files — via `mcp__ide__getDiagnostics` on the file when Rider is connected, otherwise
  `inspectcode` above. Inspection severities live in `.editorconfig` (`resharper_*_highlighting`), not in
  `.DotSettings`; if one is genuinely wrong for this repo, change it there with a comment instead of ignoring it.
- Package versions only in `Directory.Packages.props`; `.csproj` files list names and never set `TargetFramework`.
- Tests: xUnit v3 + NSubstitute; test names use underscores (`Unknown_property_is_rejected`, CA1707 off in tests).
- Details and scaffolding: `dotnet-conventions` skill. Design new features with the `brainstorming` skill first.
