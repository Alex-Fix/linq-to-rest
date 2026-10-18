---
name: typescript-client
description: Conventions for the RestToLinq TypeScript client (clients/typescript, npm package resttolinq) and its generator (src/RestToLinq.TypeScript, resttolinq-ts) — deferred proxy lambdas and .NET-identical JSON, golden files, the unit of work, generated models, tooling (strict TypeScript, ESLint, Prettier, Vitest type tests, e2e against Shop.Api). Use when changing anything under clients/typescript, samples/shop-ts, tests/golden or src/RestToLinq.TypeScript, or when a protocol change must reach both clients.
---

# TypeScript client

Design: `docs/plans/2026-10-18-typescript-client-design.md` (implementation notes at the end).

## Rules

- **Same JSON as the .NET client.** Every query or batch body must equal what `RestToLinq.Client` writes for the
  equivalent C#. Protocol or translation changes go to both clients in the same change, with a golden case in
  `tests/golden/` (add it to `GoldenTests.cs` and `test/golden.test.ts`, regenerate with
  `RESTTOLINQ_UPDATE_GOLDEN=1 dotnet test --project tests/RestToLinq.Client.UnitTests`, review the diff).
- **Lambdas are deferred.** Builders store user functions; they only run in `Translator`-driven building, so lambda
  variables are allocated in the .NET order: collection variable first, then its `where` filters, then the body.
  `and`/`or`/`concat` chains flatten like C#'s `&&`/`||`/string `+`. Never build nodes eagerly in a builder method.
- **No runtime dependencies, no dynamic code** (`eval`, `new Function`). Plain JSON values only; dates are strings.
- **DTOs stay plain objects.** Tracking uses snapshots and an identity map, never proxies on results.
- **Types enforce what they can** (operand types, sortable members, readonly keys/tokens/computed members) and
  `test/types.test-d.ts` proves it with `@ts-expect-error`; everything else is checked at runtime before a request.

## Generated models

- `*.generated.ts` files come from `resttolinq-ts`; never edit them, and they're excluded from Prettier and ESLint.
- `clients/typescript/test/shop.generated.ts` is also the generator's snapshot (`tests/RestToLinq.TypeScript.UnitTests`).
- After changing `tests/RestToLinq.TestContracts`, `samples/Shop.Contracts` or the emitter, regenerate all three
  files (commands in CLAUDE.md); CI fails on stale ones.

## Tooling

- `npm run check`: Prettier (120 columns), ESLint `strictTypeChecked` + `stylisticTypeChecked`, `tsc --noEmit`,
  Vitest (unit, golden, type tests), build. Fix findings; a suppression needs a `--` reason on the same comment.
- `npm run test:e2e`: starts `samples/Shop.Api` (SQLite, throwaway database, port 5091) and runs `test-e2e/`.
- Tests use `StubFetch` (`test/stub.ts`) for requests; no network in unit tests.
