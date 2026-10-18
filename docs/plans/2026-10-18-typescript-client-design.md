# TypeScript client — design
Status: approved, implemented (see Implementation notes) · Date: 2026-10-18

## Problem

RestToLinq's API is consumed mostly by browsers and Node services, but only .NET has a client. TypeScript developers
would have to hand-build JSON query trees and `$batch` bodies, with no types, no paging helpers and no change
tracking.

## Goals / Non-goals

**Goals**
- npm package `resttolinq` (`clients/typescript`) with the .NET client's shape: a typed query builder (filter, sort,
  include, select, offset and cursor paging, count/any/first/single, streaming) and a unit of work (tracking,
  `saveChanges()` over `$batch`).
- Queries written with typed proxy lambdas that record the closed node list; byte-identical JSON to the .NET client.
- DTO types and the runtime model generated from the .NET contracts by a dotnet tool, `resttolinq-ts`.
- No runtime dependencies; ESM; Node ≥ 20 and modern browsers.
- Tests: unit, type-level, golden files shared with .NET, generator snapshots, end-to-end against `Shop.Api`; CI job.

**Non-goals**
- Publishing to npm or NuGet.
- Exact `decimal`/`long` handling (strings or `bigint`) — would change the wire contract.
- `Date` objects for date/time members.
- React/Vue bindings, a browser sample, server-side TypeScript.
- Parsing lambda source or build-time transforms.

## Constraints check

| Constraint | TypeScript client |
|---|---|
| **Translatable / Untrusted input** | Server unchanged; the TS client is another untrusted client and can only emit closed-list nodes. |
| **No dynamic code** | Proxies record the tree by running each lambda once; no `eval`/`new Function` (CSP-safe). |
| **Wire contract** | Unchanged (`"v": 1`); golden files prove both clients write identical bodies. |
| **Public API** | New npm package and dotnet tool (`RestToLinq.TypeScript`); the generator only reads the public `DtoModel`. |
| **Dependencies** | No runtime dependencies (`fetch`, `JSON`). Dev: TypeScript, Vitest, ESLint (typescript-eslint), Prettier. |
| **Errors** | Same problem parsing: `RestQueryError`, `RestSaveError`, `RestConcurrencyError` with `status`, `code`, `location`, `limit`, `entities`. |
| **Number precision** | `decimal`/`long` → `number`: values beyond ~15 significant digits or 2^53 lose precision in JavaScript. Documented; the generator warns per member. |
| **Dates** | `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `byte[]` → ISO / canonical strings (base64 for binary), never `Date`. |
| **Layout** | New top-level `clients/` (CLAUDE.md updated). |

## Chosen approach

A typed proxy builder in TypeScript, and an explicitly run dotnet tool that generates DTO interfaces, proxy types and
the runtime model from the contracts assembly; CI fails when the generated file is stale.

- *Not parsing `Function.toString()`*: breaks under minification, can't see captured values, fails only at runtime.
- *Not a build-time transformer*: every consumer must wire a plugin into their bundler.
- *Not a server `$model` endpoint*: exposes the schema at runtime; the contracts assembly already has everything.
- *Not generating on every MSBuild build*: couples frontend output to backend builds.

## Design

### Query API

```ts
const shop = createShop({ baseUrl: "https://shop.example/api/" });

const cheap = await shop.products
  .where(p => p.price.lt(minPrice).and(p.tags.any(t => t.eq("sale"))))
  .orderByDescending(p => p.price)
  .thenBy(p => p.id)
  .select(p => ({
    id: p.id,
    label: p.name.concat(" (", p.categoryName.coalesce(""), ")"),
    total: p.lines.sum(l => l.quantity.mul(l.unitPrice)),
    bulk: p.lines.where(l => l.quantity.gt(1)).select(l => l.sku),
    category: p.category,
  }))
  .take(20)
  .toList();
```

- Expression types: `StringExpr` (`contains`, `startsWith`, `endsWith`, `toLower`, `toUpper`, `trim`, `length`,
  `concat`), `NumberExpr` (`add`, `sub`, `mul`, `div`, `mod`, `neg`), `DateExpr` (`year`, `month`, `day`, `hour`,
  `minute`, `date`), `BoolExpr` (`and`, `or`, `not`), `EnumExpr`; every scalar has `eq`, `ne`, `in`; ordered types add
  `gt`, `ge`, `lt`, `le`; nullable members add `coalesce`. Navigations are nested proxies (`NavigationProxy`);
  collections (`CollectionExpr`, `PrimitiveCollectionExpr`) have `any`, `all`, `count`, `sum`, `min`, `max`, `avg`,
  `where`, and `select` in projections. `cond(test, a, b)` maps to `if`.
- Arguments are plain JS values or expressions, type-checked against the member.
- Operators: `where`, `include`, `orderBy`/`orderByDescending`/`thenBy`/`thenByDescending`, `select`, `skip`, `after`,
  `take`, `asNoTracking` — immutable, ordering rules as in .NET (checked when the query is built).
- Terminals: `toList` (requires `take`), `toCursorPage({ includeTotalCount })`, `stream()` (`AsyncIterable` following
  cursors), `first`, `firstOrDefault`, `single`, `singleOrDefault`, `count`, `any` (+ predicate overloads); each takes
  `{ signal }`.
- Variable names `v0, v1, …` and key order match the .NET client exactly.
- `select` result types are inferred (`NumberExpr` → `number`, navigation → DTO `| null`, collection select → array).
- Filterable/sortable/writable flags are checked locally from the model before any request; sortability is also
  enforced in the types.

### Generator and generated file

`dotnet resttolinq-ts --assembly <Contracts.dll> --name Shop --out <file.ts> [--check]`

- `src/RestToLinq.TypeScript`, packed as a .NET tool. Loads the contracts assembly (dependencies from its
  `.deps.json`), builds the model with `DtoModelBuilder.ApplyConfigurationsFromAssembly`, writes one deterministic
  file with an `<auto-generated>` header (tool and contracts versions).
- `--check` writes nothing and exits 1 when the file differs.
- Errors: DTO class-name collisions across namespaces; a RestToLinq.Abstractions major version different from the
  tool's. Warnings: each `decimal`/`long` member.
- Output: enum string-union types; DTO interfaces (camelCase wire names; key, token and read-only members `readonly`;
  navigations `T | null` / `T[]`); proxy interfaces (`"sortable"` brand on sortable members); the `as const` runtime
  model (sets, key, token, members with kind/nullable/flags, navigations with target and foreign key, pagination);
  the context interface and `create<Name>` factory.

### Unit of work

- Full-DTO results are tracked in an identity map (set + key); projections and `asNoTracking()` aren't. Included
  navigations are wired to tracked instances.
- Snapshot of writable members (arrays copied) at tracking; diffed in `saveChanges()` / `entry().state`. DTOs stay
  plain objects.
- `add(New<T>)` (writable members optional, navigations allowed) returns the tracked object; `attach`, `update`,
  `remove`; `entry(dto).state`, `isModified(member)`, `setModified(member, flag)`.
- Navigation assignment syncs foreign keys; references to added DTOs become `refs`; collection membership changes
  throw.
- `saveChanges()` sends one `$batch` (inserts in reference order, then updates, then deletes; ids `o0…`), applies
  returned keys/tokens/computed members, and on failure throws with the affected entities, leaving the state as it was.

### Transport and package

- `ContextOptions`: `baseUrl`, `headers` (object or async function, per request), `transport: "query" | "post"`
  (default `query`; `post` uses `POST /{set}/$query`), `fetch`.
- Browser apps on another origin need server CORS allowing the method, `Content-Type` and auth headers (README).
- `clients/typescript`: ESM, strict TypeScript, ES2022, `tsc` build to `dist/` with declarations, `sideEffects: false`,
  committed `package-lock.json`.

## Public API changes

- npm `resttolinq`: `createContext`, `ContextOptions`, `DtoSet`, query and expression types, `cond`, `CursorPage`,
  `New<T>`, entry API, error classes.
- dotnet tool `RestToLinq.TypeScript` (`resttolinq-ts`).
- No change to the existing .NET packages.

## Security notes

- The TS client adds no server surface; all limits, allow-lists and policies apply unchanged.
- No dynamic code evaluation in the client.
- The generator runs the contracts' configuration code at build time — the developer's own code.
- `npm audit --audit-level=high` in CI; lockfile committed; CI actions pinned to SHAs.

## Testing

- Unit (Vitest): every node and operator → JSON; local errors; tracking, diffing, batch building, error mapping with a
  mock `fetch`.
- Type tests (`expectTypeOf`, `@ts-expect-error`): misuse doesn't compile.
- Golden files (`tests/golden/*.json`): shared expected JSON for equivalent queries and batches, asserted by both the
  .NET client tests and the TS tests.
- Generator (.NET): snapshot of the output for a test contracts assembly; `--check`.
- End to end: the TS client generated from `Shop.Contracts` against `Shop.Api` on SQLite.

## Implementation notes

1. **Golden comparison** is on parsed JSON with key order (`JSON.stringify(JSON.parse(…))` in TypeScript,
   `JsonNode.ToJsonString()` in .NET), not raw bytes: the .NET writer escapes characters such as `+` and `'` that
   JavaScript doesn't, which the server reads identically.
2. **Generating the golden files found a .NET client bug:** an enum array's `Contains` binds (C# 14) to the span
   overload with a null comparer, which the translator rejected. Fixed, with a regression test.
3. **Inserts:** the .NET client sends every writable member, the TypeScript client those present on the object. The
   golden batch sets every writable member, so both match; `CreatedAt` in the test contracts became read-only
   (server-owned), as in the sample.
4. **API additions:** `StringExpr.equalsIgnoreCase` (the .NET client's `string.Equals(…, OrdinalIgnoreCase)`, which
   analyzers require there); `count()` without a predicate allocates a variable like .NET's `Enumerable.Count()`.
5. **Generated file:** the header has no versions (deterministic across tool releases); generated files are excluded
   from Prettier and ESLint. The hand-written test model was replaced by the generator's output and is now its
   snapshot.
6. **Test contracts** moved to `tests/RestToLinq.TestContracts`; `tests/Directory.Build.props` only makes `*Tests`
   projects xUnit executables.
7. **The sample** runs as TypeScript directly (`node main.ts`, Node ≥ 22.18); the end-to-end tests start
   `Shop.Api` from a Vitest global setup rather than a separate CI script.

## Open questions

- Exact decimals/longs (string or `bigint`) as an opt-in wire format later?
- React/Vue helpers or a browser sample?
- npm/NuGet publishing workflow.

## Implementation steps

1. Golden files: `tests/golden/` with equivalent queries/batches; .NET client tests assert them.
2. TypeScript package scaffold (`clients/typescript`: package.json, tsconfig, ESLint, Prettier, Vitest), CLAUDE.md
   layout and commands.
3. Expression builder and query tree writer; unit tests incl. golden files.
4. Query API, transport, materialization, terminals, errors; unit and type tests.
5. Unit of work: tracking, diffing, `saveChanges()`; unit tests incl. golden batches.
6. Generator tool `RestToLinq.TypeScript` with snapshot tests.
7. Sample `samples/shop-ts` with generated file; end-to-end tests against `Shop.Api`.
8. CI `typescript` job; docs (README, package README, `typescript-client` skill).
