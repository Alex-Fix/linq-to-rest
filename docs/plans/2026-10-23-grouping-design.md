# Grouping and aggregates — design
Status: approved · Date: 2026-10-23

Backlog item of [`2026-10-20-roadmap.md`](2026-10-20-roadmap.md): "`GroupBy` and aggregates" (a v1 non-goal).

## Problem

Clients can filter, sort, project and page rows, and aggregate collections inside a row (`p.Lines.Sum(l => l.Quantity)`),
but they can't ask "how many products per brand", "price range of what's filtered" or "total stock value". Facets and
reports need extra endpoints today, which LinqToRest exists to avoid.

## Goals / Non-goals

**Goals**
- `GroupBy` on one to four keys (members of the set, or reached through references), then `Select` with keys and
  aggregates (`Count`, conditional `Count`, `Sum`, `Min`, `Max`, `Average` over expressions), `Where` on groups
  (HAVING), `OrderBy` keys and aggregates, offset paging and counting groups.
- Whole-query aggregates: `SumAsync`, `MinAsync`, `MaxAsync`, `AverageAsync` (with selectors) over a filtered query.
- API authors opt in per column: `IsGroupable()` for keys, `IsAggregatable()` for members inside aggregates; both in
  the model document, so clients refuse locally.
- Both clients (.NET LINQ and TypeScript), held together by golden request bodies; facets in the catalog sample.

**Non-goals**
- Distinct counts, `GroupBy` with element selectors, `SelectMany` over groups, grouping after `Select`, includes or
  cursor pages on grouped queries, returning grouped rows.
- Nested collection lambdas inside aggregate bodies (provider gaps).
- Joins across sets.

## Constraints check

| Constraint | This feature |
|---|---|
| **Translatable** | `GroupBy(key).Select(g => …g.Count(), g.Sum(x => …)…)` and conditional aggregates translate to `GROUP BY` on SQLite, PostgreSQL and SQL Server. The key type (our own generic class via `MemberInit`) is proven by a spike first. Only direct aggregates over the group are accepted; integration tests assert `GROUP BY` in the SQL and nothing evaluated on the client. |
| **Untrusted input** | Keys and aggregated members are opt-in per column; policy filters apply before grouping (hidden rows never count); `MaxGroupKeys` (4), node limits, and `MaxGroups` (1000) without `Take` or the set's page limits with it. |
| **No dynamic code** | Built from `Expression` nodes; the server closes one of its own `GroupKey<…>` classes, nothing is generated. |
| **Wire contract** | Additive: `groupBy`, `having`, nodes `key` and `group`, all only in grouped queries (where `having`/`orderBy` name `select` fields); still `"v": 1`. Old servers reject `groupBy` as unknown. Model document: optional `groupable`/`aggregatable` flags. |
| **Public API** | Server: `IsGroupable()`, `IsAggregatable()` (property and computed builders), `QueryLimits.MaxGroups`/`MaxGroupKeys`. .NET client: grouped LINQ shapes, `SumAsync`/`MinAsync`/`MaxAsync`/`AverageAsync`. TypeScript: `groupBy`, `sum`/`min`/`max`/`average`, typed proxies. All additive. |
| **Dependencies** | None new; Abstractions and Client stay BCL-only and trim/AOT-safe (the client never builds key types). |
| **Errors** | New codes `not_groupable` and `not_aggregatable` (400, pointer to the member); `limit_exceeded` with `groups` or `groupKeys`; `invalid_query` for misplaced members, nodes or unsupported shapes. |

## Chosen approach

**Aggregates are the existing collection lambdas over the group.** A group is a collection of rows: `{"group": {}}`
becomes a source for `count`, `sum`, `min`, `max` and `avg` (with their optional `where`), so `g.Sum(p => p.Price *
p.Stock)` and `g.Count(p => p.Stock > 0)` map one to one, reusing the reader and type checker. Whole-query aggregates
are grouped queries with no keys.

- *Not aggregates of single members only:* refuses arithmetic and conditional counts (facets like "in stock"), and
  adds a second, weaker syntax next to the lambdas.

## Design

### Client LINQ and wire

```csharp
var facets = await catalog.Products
    .Where(p => p.Status == ProductStatus.Active)
    .GroupBy(p => new { p.BrandId, p.Brand!.Name })
    .Select(g => new { g.Key.BrandId, g.Key.Name, Count = g.Count(), InStock = g.Count(p => p.Stock > 0),
                       From = g.Min(p => p.Price), To = g.Max(p => p.Price) })
    .Where(f => f.Count > 1)                      // after Select = HAVING
    .OrderByDescending(f => f.Count)
    .Take(20)
    .ToListAsync();

decimal total = await catalog.Products.Where(p => p.CategoryId == 4).SumAsync(p => p.Price * p.Stock);
```

```json
{"v": 1,
 "filter": {"eq": [{"member": "status"}, "Active"]},
 "groupBy": ["brandId", "brand/name"],
 "select": {"new": {
   "brandId": {"key": 0}, "name": {"key": 1},
   "count":   {"count": {"source": {"group": {}}, "var": "v1", "body": true}},
   "inStock": {"count": {"source": {"group": {}}, "var": "v2", "where": {"gt": [{"member": "v2/stock"}, 0]}, "body": true}},
   "from":    {"min": {"source": {"group": {}}, "var": "v3", "body": {"member": "v3/price"}}},
   "to":      {"max": {"source": {"group": {}}, "var": "v4", "body": {"member": "v4/price"}}}}},
 "having": {"gt": [{"member": "count"}, 1]},
 "orderBy": [{"member": "count", "desc": true}],
 "page": {"mode": "offset", "take": 20}}
```

- `groupBy`: 0–4 key paths, written like `include` (`[]` = one group of all rows, used by whole-query aggregates).
- Grouped-query nodes: `{"key": n}` (the n-th key) and `{"group": {}}` (only as the source of `count`, `sum`, `min`,
  `max`, `avg`), both only inside `select`. `having` filters groups and `orderBy` sorts them; in a grouped query their
  member paths name the `select`'s fields (`{"member": "count"}`), which the server replaces with the fields'
  expressions. Keys are added as final tie-breakers.
- The client puts a `Where` before `GroupBy` into `filter`, and a `Where`/`OrderBy` after the group `Select` into
  `having`/`orderBy` over the projected fields (exactly as written in LINQ); anything else fails locally with
  `NotSupportedException`.
- Response: the usual `{"items": [...], "totalCount": n}`; items are the projected groups; `count: true` counts groups.
- Whole-query aggregates send `"groupBy": []` and a `select` with one aggregate and read back one value. Over no rows,
  `Sum` is 0; `Min`/`Max`/`Average` throw like LINQ (or return null for nullable results).
- Grouped queries have no `include` and no cursor pages, and must have a `select`.

### Server: type checking and translation

```csharp
source.Where(policy).Where(filter)
      .GroupBy(e => new GroupKey<int, string> { K0 = e.BrandId, K1 = e.Brand.Name })
      .Where(g => g.Count() > 1)
      .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.K0).ThenBy(g => g.Key.K1)
      .Skip(skip).Take(take)
      .Select(g => new object[] { g.Key.K0, g.Key.K1, g.Count(), g.Count(e => e.Stock > 0), g.Min(e => e.Price), g.Max(e => e.Price) })
```

- Keys go into the server's own `GroupKey<T0>` … `GroupKey<T0..T3>` classes via `MemberInit`. Step 1 is a spike proving
  this translates on SQLite and PostgreSQL; if a provider refuses, the fallback is a fixed set of constructor-based key
  classes, whichever translates.
- The projection is the existing flat `object[]` row with a shape, so writing the response doesn't change.
- Whole-query aggregates skip `GroupBy`: `source.Where(policy).Where(filter)` then `Sum(selector)` (or
  `Select(selector).Min()`/`Max()`/`Average()`), executed directly.
- Type checking:
  - Key paths: scalar members marked `IsGroupable()`, through references only, within `MaxPathDepth`; else
    `not_groupable` at the path.
  - `{"group": {}}` only as the source of `count`/`sum`/`min`/`max`/`avg`. In `sum`/`min`/`max`/`avg` bodies every
    member must be `IsAggregatable()` (`not_aggregatable`); in `where` and `count` bodies filterable suffices. Bodies
    may use arithmetic and references, not nested collection lambdas.
  - Grouped `select` uses only keys, aggregates, literals and operators (a bare `member` there is `invalid_query`);
    `having` and `orderBy` use the `select`'s fields by name (an unknown field is `unknown_member`), literals and
    operators.
  - `sum`/`avg` need numbers; `min`/`max` also dates and times; result types follow LINQ (`Average` of `int` is
    `double`, `decimal` stays `decimal`).
- Limits: `MaxGroupKeys` (4); `MaxGroups` (1000): without `Take`, the server reads `MaxGroups + 1` rows and answers
  `limit_exceeded` (`groups`) when there are more; with `Take`, the set's `MaxPageSize` and `MaxSkip` apply.

### Configuration and model document

```csharp
builder.Property(p => p.BrandId).IsGroupable();
builder.Property(p => p.Price).IsSortable().IsAggregatable();
builder.Property(p => p.Stock).IsAggregatable();
builder.Computed("inStock", p => p.Stock > 0).IsGroupable();
// brands: builder.Property(b => b.Name).IsGroupable();   → products can group by brand/name

builder.Services.AddLinqToRest(o => o.Limits(l => { l.MaxGroups = 1000; l.MaxGroupKeys = 4; }));
```

Startup refuses `IsAggregatable()` on non-numeric, non-date/time members and `IsGroupable()` on collections or binary.
The model document writes `"groupable": true` / `"aggregatable": true` only when set (existing documents don't change).

### .NET client

- The translator recognizes `GroupBy(key)` (one member or an anonymous type), the group `Select`, `Where`, `OrderBy`,
  `Skip`, `Take`, and `CountAsync`, `ToListAsync`, `AsAsyncEnumerable` over them. Non-groupable keys and
  non-aggregatable members fail locally with the server's messages; element selectors, `SelectMany` over groups and
  grouping after `Select` are refused locally.
- Grouped results are projections (never tracked), read by the `ProjectionReader`.
- `SumAsync`, `MinAsync`, `MaxAsync`, `AverageAsync` with selector overloads.
- `ToListAsync` on a grouped query works without `Take` (capped by `MaxGroups`); row queries still need `Take`.
  `ToCursorPageAsync` refuses grouped queries.

### TypeScript client

```ts
const facets = await catalog.products
  .where((p) => p.status.eq("Active"))
  .groupBy((p) => ({ brandId: p.brandId, name: p.brand.name }))
  .select((g) => ({ ...g.key, count: g.count(), inStock: g.count((p) => p.stock.gt(0)), from: g.min((p) => p.price) }))
  .where((f) => f.count.gt(1))
  .orderBy((f) => f.count, "desc")
  .toList();
const total = await catalog.products.where((p) => p.categoryId.eq(4)).sum((p) => p.price.mul(p.stock));
```

Generated proxies mark groupable and aggregatable members (`GroupableExpr`, `AggregatableExpr`), so a non-groupable
key or a non-aggregatable sum doesn't compile. Golden files `query-group.json` and `query-aggregate.json` hold both
clients to the same bodies.

## Public API changes

- **Abstractions (added):** `QueryRequest.GroupBy`/`Having`, `KeyNode`, `GroupNode`;
  `RestPropertyModel.IsGroupable`/`IsAggregatable`; `QueryLimits.MaxGroups`/`MaxGroupKeys`; `ErrorCodes.NotGroupable`,
  `NotAggregatable`.
- **Server (added):** `IsGroupable()`, `IsAggregatable()` on property and computed builders.
- **.NET client (added):** grouped query shapes; `SumAsync`, `MinAsync`, `MaxAsync`, `AverageAsync`.
- **TypeScript (added):** `groupBy`, group `select`, `sum`/`min`/`max`/`average`, proxy markers.
- **Behaviour:** `ToListAsync` without `Take` is allowed for grouped queries only.

## Security notes

- Keys and aggregated members are opt-in per column, like sorting; grouping by an unindexed column is the API
  author's choice.
- Policy filters are applied before `GroupBy`, so counts and aggregates never include hidden rows; keys reached
  through references read the target member directly (as computed values do), so only mark members groupable when
  their values may be shown.
- Bounded by `MaxGroupKeys`, node and depth limits, `MaxGroups` or page limits, `MaxSkip`.
- Errors carry codes and pointers only.

## Testing

| Project | Tests |
|---|---|
| Abstractions.UnitTests | new request members and nodes read/written; misplaced nodes rejected with pointers; model flags |
| Server.UnitTests | startup checks for both flags |
| Server.IntegrationTests (SQLite + PostgreSQL) | translation spike; each aggregate and key kind; reference and computed keys; conditional counts; arithmetic bodies; `having`; sorting by aggregates with tie-breakers; offset pages and `count: true`; `MaxGroups` at and past its boundary; policy filters before grouping; whole-query aggregates incl. empty sets; results equal LINQ-to-Objects and the SQL has `GROUP BY` with no client evaluation; every rejection |
| Client.UnitTests | every supported shape to the golden bodies; local refusals with server-identical messages; reading grouped results and whole-query aggregates (empty-set semantics) |
| EndToEnd.Tests | facets and whole-query aggregates through the generated client on both providers |
| TypeScript | golden bodies, type tests (non-groupable keys and non-aggregatable sums don't compile), e2e against `Catalog.Api` |
| AotSmoke | a grouped query and `SumAsync` under Native AOT |
| Benchmarks | `ClientTranslation` and `QueryBinding` with a grouped shape; execution of a grouped query |

## Samples and docs

- `Catalog.Api`: `brandId`, `categoryId`, `status` groupable (and `brands.name`); `price`, `stock` aggregatable; model
  and clients regenerated.
- Browse page (React and Blazor): brand facets with counts for the current filter, plus a summary with the price range
  and total stock.
- README: "Grouping and aggregates" section, nodes, codes, limits. Roadmap: item done.

## Open questions

- Does EF Core translate grouping by a `MemberInit` of our own key class on both providers? (Step 1 decides; fallback
  above.)
- Distinct counts: later, if needed (provider differences).

## Implementation steps

1. Server: translation spike for grouped queries on SQLite and PostgreSQL; pick the key type.
2. Abstractions: wire contract (`groupBy`, `having`, `key`, `group`, limits, codes) and model flags; tests.
3. Server: `IsGroupable()`/`IsAggregatable()`, startup checks, model export; tests.
4. Server: type checking and binding of grouped queries; translation, paging, cap and count; integration tests.
5. Server: whole-query aggregates; tests.
6. .NET client: translation, local checks, aggregate operators, reading results; golden bodies; tests.
7. TypeScript client and generator: proxies, typing, golden bodies; tests.
8. Regenerate models and clients; end-to-end tests; AotSmoke scenario; benchmarks.
9. Samples: catalog flags; facets and summary on both browse pages.
10. Docs and roadmap.
