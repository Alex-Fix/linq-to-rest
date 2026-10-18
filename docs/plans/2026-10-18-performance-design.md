# Benchmarks and performance — design
Status: approved · Date: 2026-10-18

## Problem

Nothing measures RestToLinq's cost. Every request on the server parses a JSON tree, binds it, runs EF Core and
writes rows; every client translates queries, materializes results and diffs tracked DTOs. Without numbers there's no
way to know RestToLinq's overhead over hand-written EF Core LINQ, which paths dominate, or whether a change regresses
them.

## Goals / Non-goals

**Goals**
- BenchmarkDotNet suites for every `src/` project (Abstractions, Client, Server, the `resttolinq-ts` generator) and
  Vitest benchmarks for the TypeScript client.
- Make all of them as fast and allocation-light as measurements justify, one benchmarked optimization per commit.
- Keep the JSON contract; public APIs may change (packages aren't published yet).
- Before/after report in `docs/benchmarks/`; benchmarks run in CI in a quick dry mode.

**Non-goals**
- Timing gates in CI (shared runners are too noisy).
- Load testing over real sockets, database tuning, SQL Server.

## Constraints check

| Constraint | This work |
|---|---|
| **Translatable** | Expressions sent to EF Core may change only if the SQL stays equivalent; SQL-capture and integration tests guard it. |
| **Untrusted input** | Parser rewrites keep every limit as early as today (limit tests are the gate). Caches keyed by anything client-controlled are bounded and flood-tested; model-keyed caches are built at startup. |
| **No dynamic code** | No per-request `Compile()` on the server; delegates compiled from the model at startup are allowed. The client may compile captured variables but gets faster paths for common cases. |
| **Wire contract** | Unchanged; golden files and integration tests prove it. |
| **Public API** | May change; every change recorded in the report with old and new shape. |
| **Dependencies** | BenchmarkDotNet only in the benchmark project; TypeScript uses Vitest's built-in `bench`. |

## Chosen approach

Measure first: suites and baselines, then one profiled, benchmarked optimization at a time, kept only on a measured
gain with all tests green.

- *Not redesigning hot paths up front:* risks complexity that doesn't pay off, and gains can't be attributed.

## Design

### .NET suites (`benchmarks/RestToLinq.Benchmarks`)

Console app, BenchmarkDotNet 0.15.8, `[MemoryDiagnoser]`, Release only, not packable; reuses
`RestToLinq.TestContracts` and the integration tests' shop (linked files); internal APIs via `InternalsVisibleTo`.

| Class | Operation | Inputs |
|---|---|---|
| `QueryReading` | `QueryJson.Read` | small, medium, large query |
| `QueryBinding` | `QueryCompiler.Compile` | small, medium, large |
| `QueryExecution` | plan → rows on SQLite in-memory + response writing | small, medium, large; baseline: hand-written EF Core LINQ |
| `QueryEndpoint` | full `QUERY` through the test server | small, medium |
| `Batch` | `BatchJson.Read` + executing 1 and 20 operations | insert/update/delete mix |
| `ClientTranslation` | LINQ → JSON bytes | the three shapes in C# |
| `ClientMaterialization` | response bytes → tracked DTOs | 1, 50, 200 rows |
| `ChangeDetection` | `DetectChanges` + batch building | 50, 500 DTOs, 10% modified |
| `JsonValuesCodec` | `JsonValues.Read`/`Write` | per scalar kind |
| `Generator` | load contracts + build model + emit TypeScript | test and sample contracts |

Setup outside measurements; no network (stub handlers, in-process test server). Raw results in
`artifacts/benchmarks/` (gitignored).

### TypeScript (`clients/typescript/bench`)

Vitest `bench`, `npm run bench`: `translate` (three shapes), `materialize` (1/50/200 rows), `detectChanges` (50/500,
10% modified, batch building), `proxy` (proxy creation per lambda run). Stub `fetch`.

### Optimization loop

1. Pick the costliest hotspot from a profile (`[MemoryDiagnoser]`, `dotnet-trace`; `node --cpu-prof`).
2. Change one thing.
3. Compare against the previous commit; keep only clear gains (≥ 5% time or fewer allocations, no regression
   elsewhere), otherwise revert and record the attempt.
4. All tests, golden files, integration/end-to-end tests, inspections and lints stay green.
5. One commit per optimization with before/after numbers.

Guardrails: bounded caches for client-controlled keys (with flood tests); no per-request server `Compile()`; limits
enforced as early as today; public API changes recorded; safety-relevant code keeps clarity unless the gain is large.

Starting hypotheses: per-literal `JsonDocument` clones (server reader) and allocations (client writer); per-request
reflection in the binder; unpooled request bodies; client double parse and reflection-based snapshots; TypeScript
proxies rebuilt per lambda; generator string building and repeated type lookups.

### Reporting, CI, docs

- `docs/benchmarks/2026-10-18-results.md`: environment, per-suite tables (mean and allocations before → after,
  ratio), change log of kept and rejected optimizations, public API changes. Before = first commit with the
  benchmarks; after = end of branch; same machine.
- CI: `--job dry` run of all .NET benchmarks in `build`; minimal `npm run bench` in `typescript`.
- CLAUDE.md (layout, commands, "hot-path changes need a benchmark run"), `dotnet-conventions` and `typescript-client`
  skills, README "Performance" section.

## Public API changes

To be determined by the measurements; each recorded in the report.

## Security notes

- Limits keep their position in parsing; limit tests are unchanged.
- Client-controlled cache keys are bounded and flood-tested.
- No per-request dynamic compilation on the server.

## Testing

- All existing suites unchanged and green after every optimization.
- New tests for any cache (bounds, eviction) and for any rewritten parser/writer path not already covered.
- Benchmarks run in CI (dry) so they keep working.

## Open questions

- Whether a bounded per-shape plan cache on the server pays for its complexity — decided by measurement.

## Implementation steps

1. Benchmark project and .NET suites; baseline run; CLAUDE.md layout/commands.
2. TypeScript benchmarks; baseline run.
3. Server optimizations (one commit each).
4. Abstractions optimizations.
5. .NET client optimizations.
6. Generator optimizations.
7. TypeScript client optimizations.
8. Final full run, results report, CI dry runs, docs.
