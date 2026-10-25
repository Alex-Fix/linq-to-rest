# Nullable keyset cursors — design
Status: approved, implemented (see Implementation notes) · Date: 2026-10-25

Backlog item of [`2026-10-20-roadmap.md`](2026-10-20-roadmap.md): "Nullable keyset cursors" (a v1 non-goal).

## Problem

Cursor pagination rejects any nullable sort member with `cursor_nullable_sort`: nullable columns (`releasedOn`),
members reached through optional navigations (`category/name`) and members of optional value types
(`origin/country`, the catalog's `dimensions/weightKg`). The reason is twofold: the keyset predicate
`k1 > v1 OR (k1 = v1 AND k2 > v2)` is never true for a `NULL` cursor value in SQL, and providers place `NULL`
differently in `ORDER BY` (SQLite and SQL Server first ascending, PostgreSQL last). The second point also affects offset
pages today: the same query orders rows differently on SQLite and PostgreSQL. The catalog's browse grid pages by
cursor, so it can't offer "sort by weight".

## Goals / Non-goals

**Goals**
- One null ordering for every sorted query, on every provider: `null` is the smallest value, as in LINQ-to-Objects
  (first ascending, last descending). Offset, cursor and grouped queries alike.
- Cursor pages accept nullable sort members; paging through them returns exactly the offset order, with no gaps or
  duplicates across nulls and ties.
- Keep index-ordered plans where the provider already orders nulls smallest (SQLite, SQL Server).
- No wire, golden or client API change; cursors issued before the change stay valid.

**Non-goals**
- Client-chosen null placement (`nulls first/last` per sort): no LINQ syntax for it; a wire addition if ever needed.
- Cursor pages for grouped queries (still offset only).
- SQL Server in the test matrix (its own backlog item; it's listed as native here and covered once that lands).
- Making PostgreSQL use an index for nullable sorts (EF Core has no `NULLS FIRST` API).

## Constraints check

| Constraint | This design |
|---|---|
| **Translatable** | Only `x == null` sort keys and `==` / `!=` / `<` / `>` comparisons with constants or parameters, all of which EF Core translates on SQLite and PostgreSQL (`x IS NULL DESC`, `x IS NOT NULL`). Proven by SQL-asserting integration tests on both providers; no client evaluation. |
| **Untrusted input** | Nothing new is reachable: sorts are still opt-in `IsSortable()` members of the exposed set. A cursor is still unsigned and only moves the comparison point; a `null` for a non-nullable sort (including strings and the key) is `invalid_cursor`. Cursor size, depth and value count are bounded as today. |
| **No dynamic code** | Built from `Expression` nodes only (`Equal`, `NotEqual`, comparisons, `OrderBy`/`ThenBy` calls). |
| **Wire contract** | Unchanged: `"k"` entries may be `null`, which the format already allowed. A previously rejected request now succeeds, so `"v"` stays 1 and golden files don't change. |
| **Public API** | No new members. `ErrorCodes.CursorNullableSort` stays (removal would break), documented as no longer returned. Behaviour change: PostgreSQL offset pages now order nulls first ascending. |
| **Dependencies** | Server only. Abstractions and both clients unchanged. |
| **Errors** | `400 invalid_cursor` at `/page/after` with the constant message; never echoes the cursor. |

## Chosen approach

**Nulls smallest, provider-aware `ORDER BY`.** The keyset predicate handles nulls the same way on every provider;
only providers whose native order differs (PostgreSQL, and any unknown provider) get an extra leading `x == null` sort
key per nullable member. SQLite and SQL Server keep a plain `ORDER BY x`, which an index can serve.

- *Not the same extra key everywhere:* one code path, but SQLite and SQL Server would lose index-ordered plans they
  have today.
- *Not "nulls always last":* differs from LINQ semantics and needs the extra key on every provider.
- *Not cursor-only:* the same query would order differently by paging mode.
- *Not a client-chosen flag:* a wire and client API addition with no LINQ syntax behind it.

## Design

### Behaviour and wire

- Every sorted query treats `null` as smaller than any value: `OrderBy(p => p.ReleasedOn)` lists undated products
  first, `OrderByDescending` last. Same on every provider, in offset, cursor and grouped queries.
- Cursor mode accepts nullable sort members; `cursor_nullable_sort` is no longer returned.
- Cursor format unchanged: base64url of `{"v":1,"s":<sort hash>,"k":[…]}`, with `null` allowed in `k`. The sort-hash
  input (set name, paths, directions) is unchanged, so existing cursors still decode.
- Clients: no change. Neither client checked nullability locally; `After(cursor)` / `ToCursorPageAsync` and the
  TypeScript `toCursorPage` pass cursors through opaquely.

```jsonc
// valid now (was 400 cursor_nullable_sort at /orderBy/0)
{"v":1,"orderBy":[{"member":"releasedOn","descending":true}],"page":{"mode":"cursor","take":20}}
// → {"items":[…],"nextCursor":"eyJ2IjoxLCJzIjoi…","totalCount":…}   // k may be [null, 1042]

// invalid: a cursor whose k has null for a non-nullable member (e.g. name, or the key)
// → 400 invalid_cursor, pointer /page/after
```

### Sort keys

`SortKey` gets `bool IsNullable`:
- Plain queries: from the bound member (`ValueBound.IsNullable`), which already accounts for optional navigations and
  value types. Appended key members are never nullable.
- Grouped queries: from the select field's CLR type (`Nullable<T>` or a reference type), since every field is bound as
  nullable today; `count` / `sum` fields get no null key.

`QueryCompiler.Sorts` drops the `cursor_nullable_sort` rejection. The `CursorTypes` check uses the underlying type for
enums too, so nullable enums are cursor-sortable; `ToComparable` converts a nullable enum to its nullable underlying
type.

### Ordering

`SetMapping.ExecuteAsync` and `ExecuteGroupedAsync` read `db.Database.ProviderName` and ask an internal `NullOrdering`
helper whether nulls already sort smallest:

- Native: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.SqlServer` → sorts applied as today.
- Everything else (PostgreSQL, unknown) → before each nullable sort, a `x == null` key in the opposite direction:
  - ascending: `…OrderByDescending(e => e.X == null).ThenBy(e => e.X)`
  - descending: `…ThenBy(e => e.X == null).ThenByDescending(e => e.X)`

Npgsql translates this to `ORDER BY x IS NULL DESC, x`.

### Keyset predicate

`Keyset` keeps its shape (`after₁ OR (eq₁ AND after₂) OR …`); for a nullable sort the terms depend on the cursor
value `v`:

| Sort | `v` non-null | `v` null |
|---|---|---|
| ascending, after | `x > v` (nulls drop out) | `x != null` |
| descending, after | `x < v \|\| x == null` | none (nothing sorts below null) |
| equality prefix | `x == v` | `x == null` |

Null comparisons use a typed `null` constant, not a parameter, so EF Core emits `IS NULL` / `IS NOT NULL` without
null compensation. Non-nullable sorts produce exactly today's expression.

### Cursor validation

`Cursor.Decode` (public) is unchanged. `QueryCompiler` checks the decoded values afterwards: `null` for a sort that
isn't nullable → `invalid_cursor` at `/page/after`. This covers non-nullable strings, which `JsonValues.ReadScalar`
accepts as `null`.

## Public API changes

None. `ErrorCodes.CursorNullableSort` stays with a doc comment saying it is no longer returned. Documented behaviour
change: PostgreSQL (and other non-native providers) now order nulls smallest in offset and grouped queries too.

## Security notes

- No new reachable members or operators; sorting stays opt-in per member.
- Cursors stay unsigned; a forged `null` can only select a comparison point on a nullable, sortable, visible member.
  `null` for a non-nullable member or the key is rejected rather than producing a malformed predicate.
- Limits unchanged (cursor JSON depth 4, value count = sort count, page size, `MaxSkip`).
- Policy filters still apply before the keyset; the extra sort key doesn't touch filtering.
- `security` skill checklist: "cursor values are checked for nullability against their sort member".

## Testing

- **Server unit tests:** `Keyset` shapes for every row of the table above (ascending/descending × null/non-null ×
  nullable/non-nullable); `NullOrdering` (SQLite and SQL Server native, Npgsql and an unknown name emulated).
- **Server integration (SQLite + PostgreSQL)**, Shop data seeded with several nulls and ties on both sides of a page
  boundary, including a page that ends on a null:
  - for `releasedOn`, `category/name` and `origin/country`, ascending and descending, a cursor walk with small `take`
    concatenates to the offset result, with no duplicates or gaps;
  - offset and cursor agree, with nulls first ascending and last descending on both providers;
  - a cursor issued for a non-nullable sort decodes and its keyset SQL is unchanged;
  - SQL asserted: no `IS NULL` sort key on SQLite, `IS NULL DESC` before the member on PostgreSQL; nullable enum and
    nullable `DateTimeOffset` sorts translate on both; no client evaluation;
  - grouped: ordering by a nullable select field (e.g. `min(releasedOn)` per category) puts nulls first on both;
  - the `cursor_nullable_sort` cases in `PagingTests` and `ValueTypeQueryTests` become success cases; new
    `invalid_cursor` cases for `null` on a non-nullable member, a non-nullable string and the key.
- **End-to-end (.NET) and TypeScript e2e:** a cursor walk sorted by a nullable member round-trips a `null` in the
  cursor through both clients. No golden changes.
- **Samples.Tests:** paging the catalog by weight, both directions.
- **Benchmarks:** `*Query*` suite before/after (execution hot path).

## Open questions

- None blocking. If SQL Server joins the matrix later, its native-null assumption gets a test then.

## Implementation steps

1. Server: `SortKey.IsNullable`, nullable sorts allowed in cursor mode (incl. nullable enums), null-aware `Keyset`,
   the post-decode nullability check; unit tests.
2. Server: `NullOrdering` and the extra sort key in `ExecuteAsync` / `ExecuteGroupedAsync`; unit tests.
3. Integration tests (SQLite + PostgreSQL): cursor walks, offset/cursor agreement, SQL assertions, grouped ordering,
   rejections turned into successes, new `invalid_cursor` cases.
4. End-to-end: .NET end-to-end and TypeScript e2e cursor walks over a nullable sort.
5. Catalog sample: "Lightest" / "Heaviest" sorts in `Browse.tsx` and `Browse.razor`; `Samples.Tests` case.
6. Benchmarks before/after (`*Query*`).
7. Docs: README (paging, null ordering line in value types, error table, behaviour-change note), `ErrorCodes` doc
   comment, v1 design pointer, roadmap, `security` skill checklist.

## Implementation notes

- EF Core lifts the PostgreSQL null key into the subquery as a column (`… IS NULL AS c`, then `ORDER BY p0.c`), so
  the SQL test checks the whole command for `"ReleasedOn" IS NULL` in offset mode (no keyset mentions the member)
  rather than reading the outer `ORDER BY`.
- "A cursor issued before the change still decodes" became `Required_sorts_get_no_null_handling`: the sort hash and
  keyset code for non-nullable members are untouched, and the test proves their SQL gains no null checks.
- The Shop model has no nullable enum or nullable `DateTimeOffset` sort member, and adding one would change the golden
  model and both generated clients. Nullable enums are covered by the `Keyset` unit tests (LINQ-to-Objects); the
  integration tests cover `DateOnly?`, strings through an optional navigation and through an optional value type.
- In memory, `string.Compare(null, "a")` is negative where SQL gives `NULL`, so the `Keyset` unit tests can't catch
  a missing `|| x == null` on descending string sorts; the integration walks over `category/name` and
  `origin/country` do.
- Grouped queries also flag nullable group keys, not just select fields, so a `null` group sorts smallest when the
  keys break ties (the existing grouping test now asserts the exact order instead of sorting it away).
