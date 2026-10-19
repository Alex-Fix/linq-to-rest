# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

LinqToRest covers both ends of a .NET REST API. Design: `docs/plans/2026-10-18-linqtorest-v1-design.md` — read it
before changing behaviour or the wire contract.

- **Client:** a `RestDataContext` of `DtoSet<T>` (our own `IQueryable<T>` / `IQueryProvider` — never EF's)
  translates LINQ into a JSON expression tree sent with HTTP **QUERY**; full unit of work (change tracking,
  identity map) saved as one atomic `POST /$batch`.
- **Server:** validates the tree against the shared DTO model and translates it onto EF Core entities, each DTO
  being the default projection of an entity; applies batches in one transaction.
- **Shared:** DTOs (`IDto`) configured EF-style (`IDtoTypeConfiguration<T>`), used by both ends.

Every query body and batch reaching the server is untrusted — see the `security` skill.

## Layout (target — create projects here, don't invent new top-level dirs)

```
LinqToRest.slnx                 solution (+ global.json, Directory.Build.props, Directory.Packages.props)
src/
  LinqToRest.Abstractions/      IDto, model builder, query AST + JSON contract, batch/cursor contracts (BCL only)
  LinqToRest.Client/            RestDataContext, DtoSet<T>, RestQueryProvider, change tracker (BCL only)
  LinqToRest.Server/            DTO→entity mapping, AST → Expression, batch executor, policies, endpoints (EF Core)
clients/typescript/              npm package "linqtorest": TypeScript client (same wire protocol, golden-tested)
samples/                        Shop.Contracts, Shop.Api (EF Core SQLite, API key), Shop.Console (client) — not packable
tests/                          xUnit v3, one project per package + Server.IntegrationTests (SQLite) + EndToEnd.Tests
                                (EndToEnd links the integration tests' Shop/*.cs)
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
- TypeScript client (`cd clients/typescript`): `npm ci`, then `npm run check` (Prettier, ESLint strict type-checked,
  `tsc`, Vitest incl. type tests, build) and `npm run test:e2e` (starts `samples/Shop.Api` on SQLite). Request bodies
  must match `tests/golden/*.json`, which the .NET client tests also assert (`LINQTOREST_UPDATE_GOLDEN=1` regenerates
  them from the .NET client). Conventions: `typescript-client` skill.
- Generated TypeScript models (never edit by hand): `dotnet run --project src/LinqToRest.TypeScript -- --assembly
  <Contracts.dll> --name Shop [--import ../src/index.js] --out <file>`; CI runs it with `--check` for
  `clients/typescript/test`, `clients/typescript/test-e2e` and `samples/shop-ts`.

## Rules

1. **Translatable expressions only.** Server-built expressions must translate on EF Core; anything that would fall
   back to client evaluation is a bug. Prove it with SQLite integration tests, not LINQ-to-Objects alone.
2. **Closed AST, no dynamic code on the server.** The server only accepts the documented node list and builds
   `Expression` nodes itself — never deserialized .NET expressions, `System.Linq.Dynamic.Core`, Roslyn scripting or
   `Expression.Compile()` on the request path. (The client may compile/evaluate captured variables: that's the
   developer's own code.)
3. **DTO is the boundary.** Entities are never serialized. Sorting is opt-in per member; writes only go through
   members bound directly in the default projection. Every size is limited (body, depth, nodes, page, batch…).
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
