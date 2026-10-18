# PostgreSQL support, provider test matrix and CI — design
Status: approved, implemented (see Implementation notes) · Date: 2026-10-18

## Problem

Every translation in v1 is proven on SQLite only, and nothing checks pull requests. SQLite hides real-provider
differences (no rowversion, decimals as text, lenient types), so PostgreSQL users would be the first to find
translation gaps, and nothing stops a change that breaks a provider from merging. The sample also runs only on
SQLite, which isn't the setup people deploy.

## Goals / Non-goals

**Goals**
- Run every server integration and end-to-end test on SQLite **and** PostgreSQL in a plain `dotnet test`.
- PostgreSQL via Testcontainers. Without Docker the PostgreSQL tests are skipped with a visible reason;
  `RESTTOLINQ_REQUIRE_POSTGRES=1` makes them fail instead.
- A GitHub Actions workflow on every push to `main` and every PR: build (warnings as errors), format check,
  ReSharper inspections, vulnerable-package check, full tests with PostgreSQL required.
- `Shop.Api` on PostgreSQL by default (docker compose), with a SQLite fallback.
- Fix `DateTime` handling so client values never cause a 500 or depend on the server's time zone.

**Non-goals**
- SQL Server (the same test pattern can add it later).
- Migrations (sample and tests keep `EnsureCreated`).
- NuGet publishing (the MIT license was added separately).
- Coercing literals by inspecting column types in the EF model.

## Constraints check

| Constraint | This feature |
|---|---|
| **Translatable** | The purpose: the closed node list runs on PostgreSQL in CI. Known differences: decimals (native `numeric`; the double conversion becomes SQLite-only), identity sequences after explicit-id seeding (reset after seed), string collation (tests compare collation-independent values). Any other gap is fixed in the library, not by weakening tests. |
| **Untrusted input** | Npgsql rejects `DateTime` values whose `Kind` doesn't match the column (`Unspecified` → `timestamptz` throws), which would turn a client literal into a 500. Addressed by the three-layer DateTime design and a 400 safety net. |
| **No dynamic code** | Unchanged. Sequence reset uses table/column names from the EF model, never input. |
| **Wire contract** | Unchanged. `DateTime` literals with an offset now mean their UTC instant (previously converted to server-local time — a bug). |
| **Public API** | No new public API. Behaviour change in `JsonValues.Read` for `DateTime` with offsets. |
| **Dependencies** | Npgsql EF Core provider and Testcontainers only in tests and samples. Abstractions/Client stay BCL-only; Server unchanged. |
| **Errors** | Provider rejection of a parameter value → 400 `type_mismatch` (pointer `""`), logged as a warning; other exceptions propagate. |

## Chosen approach

Abstract test classes with one SQLite and one PostgreSQL subclass each; one PostgreSQL container per test assembly
(xUnit v3 assembly fixture) and one database per test server.

- *Not provider by environment variable and a CI matrix:* a plain `dotnet test` would stay SQLite-only.
- *Not a container per test or class:* too slow; databases inside one container give the same isolation.

## Design

### DateTime values

1. **Library (`JsonValues.Read`)**: a `DateTime` literal with an offset (`Z`, `+02:00`) becomes its UTC instant with
   `Kind = Utc`; without an offset it stays `Unspecified`. Never server-local time. Applies to query literals, `in`
   lists, cursors and batch values (all go through `JsonValues`).
2. **Recommended PostgreSQL setup** (tests, sample, README): `DateTime` columns are `timestamp with time zone`, hold
   UTC values, and use a value converter that marks values as UTC (`Unspecified` → `SpecifyKind(Utc)`, others →
   `ToUniversalTime()`) on the way in and `SpecifyKind(Utc)` on the way out. EF Core applies the column's converter
   to parameters compared with it, so offset-less literals are treated as UTC instead of throwing.
3. **Server safety net**: an `InvalidCastException` raised by the provider while a query or batch executes becomes
   400 `type_mismatch` with pointer `""`, logged at warning level with the set and code. Other exceptions propagate.

### Test infrastructure

- `TestProvider { Sqlite, Postgres }`.
- `PostgresContainer` assembly fixture in `Server.IntegrationTests` and `EndToEnd.Tests`: starts `postgres:17-alpine`
  once; if Docker isn't available records the reason, or rethrows when `RESTTOLINQ_REQUIRE_POSTGRES=1`.
- `ShopServer.StartAsync(TestProvider provider, …)`: Postgres creates a fresh database `shop_<guid>` in the container
  (or calls `Assert.Skip("Docker isn't available")`); SQLite keeps the in-memory connection.
- `ShopDbContext`: decimal→double conversions only when `Database.IsSqlite()`; the UTC `DateTime` converter on both
  providers; after seeding on PostgreSQL, identity sequences are reset to the highest seeded id (names from the EF
  model).
- Seed data in UTC; expected `DateTime` values in responses end in `Z`.
- Test classes become abstract (`FilterTests`, `PagingTests`, `SelectTests`, `EndpointTests`, `PolicyTests`,
  `BatchTests`, `EndToEndTests`) with `Sqlite…`/`Postgres…` subclasses using `SqliteShopFixture` /
  `PostgresShopFixture`; classes that start their own servers take the provider from an abstract property.
- The SQL-capture test matches case-insensitively.
- Unit tests (Abstractions, Client, Server mapping validation) are unchanged.

### Sample

- `samples/docker-compose.yml`: `postgres:17-alpine`, database/user `shop`, dev password, port 5432, named volume,
  health check.
- `Shop.Api`: `Database:Provider` = `Postgres` (default) | `Sqlite`; connection strings `ShopPostgres` / `ShopSqlite`
  in `appsettings.Development.json` only (dev-only credentials; elsewhere supplied by environment or user-secrets).
- `ShopDbContext`: decimal conversion only on SQLite; UTC converter for `DateTime`; new `Product.CreatedAt` (UTC, set
  on insert) to show the setup.
- `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 in the "Samples" package group; `EnsureCreated` + seed on both.

### CI

`.github/workflows/ci.yml` on push to `main` and pull requests, `ubuntu-latest`, `permissions: contents: read`,
concurrency cancels superseded runs, actions pinned to commit SHAs:

1. checkout; setup-dotnet from `global.json`; `dotnet tool restore`; `dotnet restore`
2. `dotnet build RestToLinq.slnx -c Release --no-restore`
3. `dotnet format RestToLinq.slnx --verify-no-changes --no-restore`
4. `dotnet jb inspectcode` (SARIF) — fail and print findings if any
5. `dotnet list package --vulnerable --include-transitive` — fail if any
6. `dotnet test --solution RestToLinq.slnx -c Release --no-build` with `RESTTOLINQ_REQUIRE_POSTGRES=1`
7. upload SARIF and test results as artifacts

### Docs

- README: "Databases" (tested providers, recommended Npgsql `DateTime` setup, safety net), running provider tests,
  sample providers, CI badge.
- CLAUDE.md: provider test commands and the environment variable.
- `dotnet-conventions` skill: the subclass-per-provider pattern for new integration tests.
- v1 design: "PostgreSQL in CI" open question resolved by this design.

## Public API changes

None. Behaviour change: `DateTime` values with an offset read as UTC instants (`Kind = Utc`) instead of server-local
time.

## Security notes

- Provider type rejections no longer surface as 500s; the 400 carries no value and the warning log carries no value.
- Dev credentials (API key, PostgreSQL password) only in `appsettings.Development.json` and the compose file, marked
  dev-only; production configuration comes from the environment.
- CI: least-privilege token, SHA-pinned actions, vulnerable-package gate.

## Testing

- `JsonValues` unit tests: offsets → UTC instants, offset-less → `Unspecified`, round trip of UTC values.
- Every integration and end-to-end test on both providers; skipped (not passed) without Docker.
- Safety net: a PostgreSQL test where an unconverted `timestamptz` comparison with an offset-less literal returns 400
  `type_mismatch`, not 500.
- Sample run manually on both providers; CI run on the PR.

## Implementation notes

1. **Npgsql throws `ArgumentException`**, not `InvalidCastException` (checked against a real container): directly
   for query parameters (source `Npgsql.EntityFrameworkCore.PostgreSQL`) and inside `DbUpdateException` on save
   (source `Npgsql`). The guard treats `ArgumentException` or `InvalidCastException` as a provider rejection when it
   was thrown outside RestToLinq, EF Core, `System.Linq.Expressions` and CoreLib (judged by the exception's
   `Source`). Tested with a command interceptor standing in for the provider, on both providers.
2. **PostgreSQL found one library gap:** collection elements (includes, selected collections, `map` over DTO
   collections) had no defined order. They are now ordered by the element key on every provider.
3. **Comparisons on computed `DateTime`s** (`date(createdAt)`) with offset-less literals also work on PostgreSQL with
   the UTC converter, verified in the sample.
4. **CI doesn't upload test results.** The xUnit runner's report options weren't worth an extra package; failing
   tests are printed in the log. Only the inspection SARIF is uploaded.
5. **Untested: the "no Docker" skip.** It can't be simulated while Docker Desktop is running, because
   `/var/run/docker.sock` always links to it. The skip path is the documented `DockerUnavailableException`.

## Open questions

- SQL Server next (same pattern), or on demand?
- Should the safety net also cover other provider exception types if new ones show up?

## Implementation steps

1. `JsonValues`: `DateTime` offsets → UTC instants; unit tests.
2. Server safety net: provider `InvalidCastException` during query/batch execution → 400 `type_mismatch`, logged.
3. Test infrastructure: packages, `TestProvider`, `PostgresContainer` assembly fixture, provider-aware `ShopServer`,
   `ShopDbContext` (conversions, UTC converter, sequence reset), UTC seed.
4. Split integration test classes into SQLite/PostgreSQL subclasses; fix any PostgreSQL gaps found in the library.
5. Split end-to-end tests the same way.
6. Sample: docker compose, provider switch, UTC converter, `CreatedAt`; run on both providers.
7. CI workflow.
8. Docs: README, CLAUDE.md, `dotnet-conventions` skill, v1 open question.
