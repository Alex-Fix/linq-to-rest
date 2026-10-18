# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

RestToLinq: a .NET library that turns REST query parameters (filter, sort, paging, field selection) into LINQ
expression trees applied to `IQueryable<T>`, so they run in the database (EF Core) rather than in memory. Also
ships a sample ASP.NET Core API that demonstrates it. Every query string comes from an untrusted client — see
the `security` skill.

## Layout (target — create projects here, don't invent new top-level dirs)

```
RestToLinq.slnx                 solution (+ global.json, Directory.Build.props, Directory.Packages.props)
src/                            packable libraries (core parser/translator; ASP.NET Core integration)
samples/                        sample API (not packable)
tests/                          xUnit v3, shared settings in tests/Directory.Build.props
docs/plans/                     approved design docs (written by the `brainstorming` skill)
artifacts/                      gitignored: all build output (no per-project bin/obj)
```

## Commands

- Test: `dotnet test --solution RestToLinq.slnx` (Microsoft Testing Platform via `global.json`, so pass
  `--solution`/`--project` explicitly)
- Format check: `dotnet format RestToLinq.slnx --verify-no-changes`
- Rider/ReSharper inspections: `dotnet tool restore` once, then
  `dotnet jb inspectcode RestToLinq.slnx --format=Text --output=- --no-build` (~15s+; drop `--no-build` if not
  just built; `--include="src/Foo/**"` to narrow)
- Add a project: `dotnet sln RestToLinq.slnx add --solution-folder src <path>` (or `samples` / `tests`)

## Rules

1. **Translatable expressions only.** Generated expressions must be translatable by EF Core (no
   `Invoke`/compiled delegates, no client-only methods). Anything that would silently fall back to in-memory
   evaluation is a bug. Verify translation with integration tests against a real provider, not LINQ-to-Objects only.
2. **No dynamic code.** Never build queries with string concatenation, `System.Linq.Dynamic.Core`, Roslyn
   scripting or `Expression.Compile()` on the request path — build `Expression` trees directly.
3. **Allow-list, bounded.** Only explicitly exposed properties are filterable/sortable/selectable; query length,
   nesting depth, node count and page size have limits with safe defaults.
4. **Public API is a contract.** Packable `src/` projects are versioned NuGet packages — breaking changes need
   explicit approval.
5. **Free and open.** Free/open-source dependencies only; core library keeps dependencies minimal.

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
