---
name: typescript-client
description: Conventions for the LinqToRest TypeScript client (clients/typescript, npm package linqtorest) and its generator (src/LinqToRest.CodeGen, linqtorest-gen ts) — deferred proxy lambdas and .NET-identical JSON, golden files, the unit of work, generated models, tooling (strict TypeScript, ESLint, Prettier, Vitest type tests, e2e against Shop.Api). Use when changing anything under clients/typescript, samples/shop-ts, tests/golden or src/LinqToRest.CodeGen, or when a protocol change must reach both clients.
---

# TypeScript client

Design: `docs/plans/2026-10-18-typescript-client-design.md` (implementation notes at the end), with the model and
generator input changed by `docs/plans/2026-10-20-entity-sets-design.md`.

## Rules

- **Same JSON as the .NET client.** Every query or batch body must equal what `LinqToRest.Client` writes for the
  equivalent C#. Protocol or translation changes go to both clients in the same change, with a golden case in
  `tests/golden/` (add it to `GoldenTests.cs` and `test/golden.test.ts`, regenerate with
  `LINQTOREST_UPDATE_GOLDEN=1 dotnet test --project tests/LinqToRest.Client.UnitTests`, review the diff).
- **Lambdas are deferred.** Builders store user functions; they only run in `Translator`-driven building, so lambda
  variables are allocated in the .NET order: collection variable first, then its `where` filters, then the body.
  `and`/`or`/`concat` chains flatten like C#'s `&&`/`||`/string `+`. Never build nodes eagerly in a builder method.
- **No runtime dependencies, no dynamic code** (`eval`, `new Function`). Plain JSON values only; dates are strings.
- **Results stay plain objects.** Tracking uses snapshots and an identity map, never proxies on results.
- **Types enforce what they can** (operand types, sortable members, readonly keys/tokens/computed members, `add` /
  `remove` only on sets whose `writes` allow them) and
  `test/types.test-d.ts` proves it with `@ts-expect-error`; everything else is checked at runtime before a request.

## Generated models

- `*.generated.ts` files come from `linqtorest-gen ts` reading a model document; never edit them, and they're
  excluded from Prettier and ESLint.
- `clients/typescript/test/shop.generated.ts` is also the generator's snapshot (`tests/LinqToRest.CodeGen.UnitTests`),
  generated from `tests/golden/model.json`; `test-e2e` and `samples/shop-ts` use `samples/Shop.Api/model.json`.
- `samples/Catalog/catalog-web` (React + Vite) uses `src/catalog.generated.ts` from `samples/Catalog/Catalog.Api/model.json`
  and depends on the local client (`file:`), so build `clients/typescript` first. It follows the same tooling rules
  (`npm run check`: Prettier, ESLint strict + React hooks, `tsc`, Vite build).
- After changing a set configuration, the golden model or the emitter, re-export the model and regenerate every
  generated file (commands in CLAUDE.md and `samples/Catalog/README.md`); CI fails on stale ones.

## Tooling

- `npm run check`: Prettier (120 columns), ESLint `strictTypeChecked` + `stylisticTypeChecked`, `tsc --noEmit`,
  Vitest (unit, golden, type tests), build. Fix findings; a suppression needs a `--` reason on the same comment.
- `npm run test:e2e`: starts `samples/Shop.Api` (SQLite, throwaway database, port 5091) and runs `test-e2e/`.
- Tests use `StubFetch` (`test/stub.ts`) for requests; no network in unit tests.
- `npm run bench`: Vitest benchmarks in `bench/` (translate, proxy, materialize, saveChanges) against a canned
  `fetch`; results in `artifacts/typescript-bench.json`. Single runs vary by ±10%, so compare a change with several
  alternating before/after runs (medians), not one run each. Profile with `node --cpu-prof` on the compiled output.
