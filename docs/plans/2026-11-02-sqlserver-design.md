# SQL Server in the provider matrix — design
Status: approved, implemented (see Implementation notes) · Date: 2026-11-02

Backlog item of [`2026-10-20-roadmap.md`](2026-10-20-roadmap.md): "SQL Server in the provider matrix", left as a
non-goal by [`2026-10-18-postgresql-design.md`](2026-10-18-postgresql-design.md).

## Problem

Every translation is proven on SQLite and PostgreSQL only. SQL Server is the third common EF Core provider, and it
differs where the other two agree: its default collation is case-insensitive and ignores trailing spaces, it sorts
`uniqueidentifier` differently from .NET, identity columns refuse explicit ids, and a command takes at most 2,100
parameters. `NullOrdering` already treats SQL Server as ordering `NULL` smallest, but nothing tests it. SQL Server
users would be the first to find translation gaps.

## Goals / Non-goals

**Goals**
- Run every server integration and end-to-end test on SQLite, PostgreSQL **and** SQL Server in a plain
  `dotnet test`, with the subclass-per-provider pattern and one container per test assembly.
- String comparisons follow the database collation; SQL Server tests run on the default (case-insensitive)
  collation and assert that behaviour.
- Without Docker the SQL Server tests skip with a reason; one `LINQTOREST_REQUIRE_DATABASES=1` (replacing
  `LINQTOREST_REQUIRE_POSTGRES`) makes a missing PostgreSQL or SQL Server fail instead. CI sets it.
- `Catalog.Api` runs on SQL Server through its provider switch and docker compose.
- Fix any translation gap in the library, never by weakening tests.

**Non-goals**
- Forcing ordinal string semantics (an explicit `COLLATE`) on any provider.
- Azure SQL specifics, SQL Server 2025's `json` and `vector` types, temporal tables.
- `Samples.Tests` or the TypeScript e2e on SQL Server (they stay on SQLite).
- Migrations (tests and samples keep `EnsureCreated`).

## Constraints check

| Constraint | This design |
|---|---|
| **Translatable** | The purpose: the closed node list runs on SQL Server in CI. Expected differences: collation (documented, asserted), `Guid` order (cursor predicates run in SQL, so consistent), identity inserts in the test seed (key sequences), decimal precision. Any other gap is a library bug. |
| **Untrusted input** | SQL Server allows 2,100 parameters per command; `MaxNodes` (500) and `MaxInListLength` (500) keep a query far below it, proven by a test at the limits. A provider value rejection must stay a 400 (`type_mismatch`) and a save failure a 409, never a 500. |
| **No dynamic code** | Unchanged. Sequence restarts in the test context use names from the EF model, never input. |
| **Wire contract** | Unchanged. |
| **Public API** | None. The test environment variable is renamed (contributors only). |
| **Dependencies** | `Microsoft.EntityFrameworkCore.SqlServer` and `Testcontainers.MsSql` (both MIT) in tests and samples only; the `mssql/server` image runs as the free Developer edition. Abstractions, Client and Server unchanged. |
| **Errors** | Existing paths: provider `ArgumentException` / `InvalidCastException` → 400 `type_mismatch`; other `DbUpdateException` → 409 `conflict`. Tests prove SqlClient's errors take them. |

## Chosen approach

The PostgreSQL pattern: abstract test classes with a `SqlServer…` subclass each, one SQL Server container per test
assembly (xUnit v3 assembly fixture) and one database per test server, on the default collation.

- *Not a provider environment variable with a CI matrix:* a plain `dotnet test` would skip SQL Server, which the
  PostgreSQL design already rejected.
- *Not a case-sensitive test collation:* simpler expectations, but untested against the setup most databases use.
- *Not forcing ordinal comparisons:* provider-specific SQL, can defeat indexes, overrides the developer's collation.

## Design

### Test infrastructure

- `TestProvider.SqlServer`; `SqlServerDatabase : ShopDatabase` creates a fresh `shop_<guid>` database (default
  collation) per server and clears its connection pool on dispose, like `PostgresDatabase`.
- `SqlServerContainer` assembly fixture in `Server.IntegrationTests` and `EndToEnd.Tests`:
  `mcr.microsoft.com/mssql/server:2022-latest`, started once; without Docker it records a skip reason, or rethrows
  when `LINQTOREST_REQUIRE_DATABASES=1`. `PostgresContainer` reads the same variable.
- Every abstract test class gets a `SqlServer…Tests` subclass (15 integration classes, one end-to-end), with
  `SqlServerShopFixture` for the shared read-only ones.
- `ShopDbContext` on SQL Server:
  - explicit decimal precisions (no EF warning, no silent truncation);
  - integer keys use sequences (`UseKeySequences()`) so the seed can set ids without `IDENTITY_INSERT`, which only
    one table may have on at a time; `ResetSequences` gains a SQL Server branch restarting each sequence past the
    highest seeded id. The shared tests therefore don't cover `IDENTITY`; the catalog sample does (its seed sets no
    ids).
- Container start: about 10 s on CI, 20–40 s on Apple Silicon (the image is x64-only and runs under Rosetta), once
  per assembly; the two assemblies start in parallel.

### Provider behaviour

- **Null ordering:** `SqlServerNullSortTests` proves the native assumption: plain `ORDER BY` (no `IS NULL` key in
  the SQL), offset and cursor walks over nullable members in both directions.
- **Collation:** shared tests use values that differ by more than case. SQL Server-only `CollationTests` assert the
  default: `eq` and `contains` match case variants, sorting interleaves them, a cursor walk over names differing only
  in case neither skips nor repeats rows (the key breaks ties), trailing spaces are ignored by `=`, and
  `equalsIgnoreCase` gives the same result as on the other providers.
- **`Guid` order:** expectations that sort `Guid`s in .NET (the `Bin` keys) come from the database order instead; a
  cursor walk over `Bin`'s composite `Guid` key proves no skips or repeats.
- **Rejected values:** a decimal literal that overflows `decimal(18,2)` in a query → 400 `type_mismatch` (SqlClient
  throws `ArgumentException`, which `ProviderRejections` already treats as a rejection); an overflow on save → 409
  `conflict` (implemented: 400 `type_mismatch`, see Implementation notes). If SqlClient throws something else, the
  safety net is widened to it, with a test.
- **Parameter limit:** a query at the default node limit with a 500-item `in` list runs on SQL Server.
- Only tests asserting database-inherent differences (collation, trailing spaces) branch on
  `ShopDatabase.Provider`.

### Catalog sample

- `Catalog.Api`: `Database:Provider=SqlServer` with `CatalogSqlServer` (dev-only, in
  `appsettings.Development.json`); `CatalogDbContext` sets decimal precisions on SQL Server and keeps `IDENTITY`
  keys.
- `samples/docker-compose.yml`: `sqlserver` service (`mssql/server:2022-latest`, `ACCEPT_EULA`, Developer edition,
  dev-only `SA_PASSWORD`, port 1433, named volume, health check, `platform: linux/amd64`).
- Run manually on SQL Server; the result goes in the implementation notes.

### CI

- The .NET job sets `LINQTOREST_REQUIRE_DATABASES=1` instead of `LINQTOREST_REQUIRE_POSTGRES`; the test step becomes
  "Test (SQLite, PostgreSQL and SQL Server)". `ubuntu-latest` runs the x64 image natively.
- The vulnerable-package gate covers the new packages. The TypeScript job is unchanged.

### Docs

- README "Databases": SQL Server tested; comparisons and sorting follow the collation (SQL Server's default is
  case-insensitive and ignores trailing spaces; `equalsIgnoreCase` / `string.Equals(…, OrdinalIgnoreCase)` is
  portable); give decimals a precision; null order is native. Running the provider tests (new variable, Rosetta
  note) and the sample on SQL Server.
- `CLAUDE.md`: three providers and the renamed variable.
- `dotnet-conventions` skill: three subclasses per integration test class.
- Roadmap item done; pointers from the PostgreSQL and nullable-cursors designs' open questions.

## Public API changes

None. Contributors: `LINQTOREST_REQUIRE_POSTGRES` becomes `LINQTOREST_REQUIRE_DATABASES`.

## Security notes

- The parameter limit can't be reached through the wire: existing node and `in`-list limits bound a query, and a
  test at those limits runs on SQL Server.
- Provider rejections stay 400 / 409 without echoing values; the warning log carries no value.
- Dev credentials (`SA_PASSWORD`, connection string) only in `appsettings.Development.json` and the compose file,
  marked dev-only; tests use Testcontainers' generated password.

## Testing

- Every integration and end-to-end test on all three providers; SQL Server skipped (not passed) without Docker.
- SQL Server-only: `CollationTests`, `Guid` cursor walk, decimal overflow (query 400, batch 409), parameter-limit
  query.
- `NullSortTests` SQL assertions: no null key on SQL Server.
- Catalog sample run manually on SQL Server; CI run on the PR.

## Implementation notes

- **One library gap, on every provider:** a cursor page after the first on a set with a `Guid` key (Shop's `bins`)
  was a 500. .NET has no `Guid` comparison operators, so the keyset predicate had no term. The keyset now compares
  `Guid`s through `CompareTo`, which EF Core translates to the database's own `>` / `<` (nullable keys get a
  `!= null` guard, as lifted operators would). `Guid` is also a cursor sort type now. `QueryBinder.Comparison` is
  unchanged, so `min` / `max` over `Guid` stay refused (PostgreSQL has no `max(uuid)`). A shared paging test walks
  the seeded bins on all three providers, and the keyset unit tests cover nullable `Guid`s.
- **A decimal that doesn't fit is a 400 in batches too**, not the 409 the design expected: SqlClient refuses the
  parameter with an `ArgumentException` before the database sees it, which the existing provider-rejection check
  turns into `type_mismatch`, as in queries.
- **Key sequences are named explicitly** (`<Table>Sequence`, set with `UseSequence`): with `UseKeySequences()` EF
  Core reports no sequence name for a property, so `ResetSequences` couldn't find them.
- **Two shared tests assumed SQLite/PostgreSQL SQL:** string concatenation is `+` on SQL Server, not `||`, and SQL
  Server prefixes inserts with `SET NOCOUNT ON;`, so the stand-in rejecting interceptor matches `INSERT` anywhere.
  The null-sort SQL assertion now recognises both `"ReleasedOn"` and `[ReleasedOn]`, so SQL Server's native null
  order is really checked.
- **Vulnerable transitive package:** `Microsoft.Data.SqlClient` brings `System.Security.Cryptography.Pkcs` 9.0.11
  (CVE-2026-84008). `Directory.Packages.props` turns on central transitive pinning and pins 10.0.14.
- **Benchmarks** project links the shop files, so it references the SQL Server provider too.
- **Catalog sample, run manually on SQL Server** (compose service, Rosetta on Apple Silicon): case-insensitive search
  (`startsWith "laptops premium"`), cursor pages by price, an editor update and a stale-token 412, and an image link
  to a new image all worked.
- **Timing:** the full solution run (1,534 tests, nothing skipped) takes about a minute locally with both
  containers; SQL Server starts in roughly 10 s under Rosetta, faster than estimated.

## Open questions

- Should the shared tests also cover `IDENTITY` keys on SQL Server (seeding through per-table `IDENTITY_INSERT`)
  rather than leaving that to the sample?

## Implementation steps

1. Packages; `SqlServerContainer`, `SqlServerDatabase`, `TestProvider.SqlServer`, `LINQTOREST_REQUIRE_DATABASES`;
   `ShopDbContext` precisions, key sequences and sequence reset.
2. `SqlServer…` subclasses for every integration test class; fix library gaps found; provider-aware expectations
   only for collation and `Guid` order.
3. SQL Server-only tests: collation, `Guid` cursor walk, rejected values, parameter limit; null-order SQL asserts.
4. End-to-end subclass.
5. Catalog sample: provider switch, compose service, precisions; manual run.
6. CI: environment variable and step name.
7. Docs: README, `CLAUDE.md`, `dotnet-conventions` skill, roadmap, earlier designs' pointers.
