---
name: dotnet-conventions
description: Conventions and scaffolding for the RestToLinq .NET solution — build props, central package management, analyzers, library packaging and public API rules, xUnit v3 test layout (unit vs EF Core translation tests), sample API, and how to add a project. Use when creating or modifying anything under src/, samples/ or tests/, or any .csproj/.props file.
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
- Free/open-source only. Core library: as few dependencies as possible (ideally none beyond the BCL);
  ASP.NET Core and EF Core dependencies belong in the integration package, sample, or tests.

## Library projects (`src/`)
- Packable: set `IsPackable`, `PackageId`, `Description`, `PackageReadmeFile`, `PackageLicenseExpression` in the
  csproj; `GenerateDocumentationFile=true` so every public member has XML docs (CS1591 is an error under
  warnings-as-errors — that's intended).
- Keep the public surface small: `internal` by default, `sealed` classes unless designed for inheritance,
  `InternalsVisibleTo` for test projects instead of making things public.
- Expression building: `System.Linq.Expressions` nodes only. Resolve members via configured allow-lists, never
  `Type.GetProperty(clientString)` on arbitrary types. No `Expression.Compile()` on the request path.
- Options: immutable options objects with safe defaults (max depth, max nodes, max page size); validate at
  configuration time.
- Errors: a dedicated exception type (or result type) carrying a constant message and error code — never the
  raw query text. The ASP.NET Core package maps it to ProblemDetails (400).
- Time: inject `TimeProvider` if anything needs "now"; tests use `FakeTimeProvider`.

## Sample (`samples/`)
- Minimal ASP.NET Core API + EF Core (SQLite) showing the library end to end. Not packable. Keep it simple — it's
  documentation, not a second product.

## Tests (`tests/`)
- `tests/Directory.Build.props` adds xUnit v3, `OutputType=Exe`, `IsPackable=false`, global `using Xunit`.
  Mocks: NSubstitute. Names use underscores (CA1707 off in tests).
- Unit tests: parser/tokenizer and expression shape (valid and invalid inputs, every limit at and past its boundary).
- Translation tests: run generated queries against a real EF Core provider (SQLite at minimum) and assert both the
  results and that the query translated (no client evaluation). LINQ-to-Objects alone isn't proof.
- Sample API functional tests: `WebApplicationFactory` hitting real endpoints, asserting status codes and
  ProblemDetails bodies.

## Commands
- Test: `dotnet test --solution RestToLinq.slnx` (Microsoft Testing Platform via `global.json`); one project:
  `dotnet test --project tests/<Project>`
- Format: `dotnet format RestToLinq.slnx --verify-no-changes` (drop the flag to fix)
- Pack: `dotnet pack RestToLinq.slnx -c Release` → `artifacts/package/`
- Rider/ReSharper inspections (tool pinned in `dotnet-tools.json`; `dotnet tool restore` first):
  `dotnet jb inspectcode RestToLinq.slnx --format=Text --output=- --no-build [--include="src/X/**"]`.
  Default minimum severity is SUGGESTION — fix those too, not just warnings. Text output has no severity column;
  use `--format=Sarif --output=artifacts/inspect.sarif` when severity matters. When Rider is connected,
  `mcp__ide__getDiagnostics` with the file URI gives the live editor highlights faster.
- Severities: `.editorconfig` `resharper_<inspection_id>_highlighting = none|hint|suggestion|warning|error`.
  `src/**` already turns off the "never used" inspections for public API (consumers use it, the solution doesn't).

## Adding a project
1. Create the `.csproj` under `src/`, `samples/` or `tests/` (no `TargetFramework`/versions — inherited).
2. `dotnet sln RestToLinq.slnx add --solution-folder src <path>` (or `samples` / `tests`).
3. Test projects reference production projects as `..\..\src\<Project>\<Project>.csproj` and inherit xUnit from
   `tests/Directory.Build.props`.
4. Add any new package versions to `Directory.Packages.props`.
