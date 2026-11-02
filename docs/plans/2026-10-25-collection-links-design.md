# Collection links and composite foreign keys — design
Status: approved, implemented (see Implementation notes) · Date: 2026-10-25

Backlog items of [`2026-10-20-roadmap.md`](2026-10-20-roadmap.md): "`HasMany` membership changes without a child FK"
(a v1 non-goal) and "Composite foreign keys" (from the entity sets design).

## Problem

A collection navigation can be read but not changed from the parent side. The client throws on
`product.Lines.Add(line)` ("set the foreign key of the child entity instead"), which only works when the child
exposes its foreign key and a `HasOne` back to the parent. Shop's `Product.Lines` is `HasMany(...).WithOne()`: an
`OrderLine` has no `Product` navigation, so a line can't even reference a product inserted in the same batch (`refs`
need a reference navigation with `WithForeignKey`). Many-to-many relationships (EF Core skip navigations over a join
table) can't be exposed at all: `ResolveNavigations` only looks up regular navigations.

Separately, `WithForeignKey` takes one member, although the model document already allows a list, so entities whose
foreign key spans several columns can't be written through a navigation.

## Goals / Non-goals

**Goals**
- Parent-side membership changes for one-to-many and many-to-many collections: add and remove existing or newly
  inserted items, from both clients, in one atomic batch.
- Removal follows the EF Core relationship: a required foreign key deletes the orphan, an optional one is nulled.
- Expose pure many-to-many skip navigations for queries (`any`, `count`, `map`, `include`, grouped `count`) and links.
- Composite foreign keys in `WithForeignKey`, by value and through `refs`.
- Read-only by default: membership only changes on navigations that opt in.

**Non-goals**
- Join entities with payload columns as skip navigations: they stay ordinary sets with two `HasOne`s.
- Implicitly adding untracked items (EF's auto-add on `collection.Add`): items are added or attached first.
- Replacing a whole collection in one operation, ordering within a collection.
- Benchmarks in this change: the `ChangeDetection` / `BatchExecution` before/after run is a follow-up (see Open
  questions).

## Constraints check

| Constraint | This design |
|---|---|
| **Translatable** | Many-to-many queries go through EF skip navigations, which translate to joins over the join table on SQLite and PostgreSQL; proven by SQL-asserting integration tests. Writes use EF navigation fixup, no expressions. |
| **Untrusted input** | Membership is read-only until `AllowLinks()`. Parent and item are loaded through their policy filters (hidden → 404); removing a non-member is 404 too, so membership is never confirmed. Policies: parent `Update`; one-to-many child `Update` (link, detach) or `Delete` (required unlink); many-to-many item only visible. Only the affected membership is loaded, never the whole collection. Each link counts toward `MaxBatchOperations`. |
| **No dynamic code** | Navigation fixup on tracked entities (`collection.Add` / `Remove`) and the existing key predicate; nothing compiled or string-built. |
| **Wire contract** | Additive: `link` / `unlink` in the closed `op` list, with `navigation` and `item`; composite `refs` keep their per-member shape. Batch `"v"` stays 1 (old servers reject the new ops with `invalid_batch`). Model document: additive navigation keys `links`, `onUnlink`; `RestModel.Parse` rejects unknown keys, so older generators would refuse a newer document — accepted while nothing is published, model `"v"` stays 1. |
| **Public API** | New: `AllowLinks()` on the collection navigation builder, `WithForeignKey` accepting an anonymous object of members, `BatchOperationKind.Link` / `Unlink`, `BatchOperation.Navigation` / `Item` / parent `Ref`, `BatchItem`, `RestNavigationModel.Links` / `OnUnlink`. No breaking change. |
| **Dependencies** | None new. Abstractions and Client stay BCL-only and AOT-safe (`AotSmoke` gains a link scenario). |
| **Errors** | `400 invalid_batch`, `403 operation_not_allowed` / `forbidden`, `404 not_found`, `412 concurrency_conflict`, `409 conflict`, each with a pointer and the operation id; never echoes values. |

## Chosen approach

**`link` / `unlink` batch operations applied through EF Core navigation fixup.** One mechanism for both relationship
kinds: the server loads parent and item and calls `collection.Add(item)` / `Remove(item)`; EF sets the child's foreign
key, inserts or deletes join rows, and deletes or detaches orphans as the relationship says. The child never needs to
expose its foreign key.

- *Not rewriting one-to-many into child updates on the client:* two mechanisms (many-to-many still needs new ops),
  more client logic, and it fails when the child hides its foreign key.
- *Not "always null the FK" or a per-navigation `OnRemove`:* EF developers expect the relationship's semantics, and
  the model document carries it, so both clients agree without extra API.

## Design

### Configuration

```csharp
builder.HasMany(p => p.Lines).AllowLinks();                              // one-to-many
builder.HasMany(p => p.Labels).AllowLinks();                             // many-to-many (EF skip navigation)
builder.HasOne(p => p.Bin).WithForeignKey(p => new { p.BinAisle, p.BinShelf });   // composite foreign key
```

- `HasMany` accepts an EF skip navigation whose join entity is a pure join table (shared-type or join entity without
  its own exposed set and without payload columns); anything else fails at startup.
- `AllowLinks()` fails at startup unless the parent set allows `Update`; for one-to-many the child set must allow
  `Update`, and `Delete` too when the foreign key is required (unlinking deletes the orphan).
- `WithForeignKey` takes one member or an anonymous object of members; each must be exposed, and together they must be
  the navigation's EF foreign key, in order.

### Model document

```json
{"name":"lines","target":"orderLines","collection":true,"links":"oneToMany","onUnlink":"delete"}
{"name":"labels","target":"labels","collection":true,"links":"manyToMany"}
{"name":"bin","target":"bins","foreignKey":["binAisle","binShelf"],"nullable":true}
```

- `links` (`oneToMany` | `manyToMany`) only with `AllowLinks()`; `onUnlink` (`delete` | `detach`) only for
  one-to-many, from the EF relationship. `RestModel` validation rejects `links` on references and `onUnlink` on
  many-to-many.
- A many-to-many navigation without `AllowLinks()` is listed like any read-only collection.

### Wire format

```json
{"v":1,"operations":[
  {"id":"o0","op":"insert","set":"orderLines","values":{"sku":"C3","quantity":1,"unitPrice":20}},
  {"id":"o1","op":"link","set":"products","key":{"id":7},"navigation":"lines","item":{"ref":"o0"}},
  {"id":"o2","op":"link","set":"products","key":{"id":7},"navigation":"labels","item":{"key":{"id":3}}},
  {"id":"o3","op":"unlink","set":"products","key":{"id":7},"navigation":"lines","item":{"key":{"id":12},"token":"…"}},
  {"id":"o4","op":"insert","set":"products","values":{…},"refs":{"binAisle":"o5","binShelf":"o5"}}]}
```

- Parent: `key`, or `ref` to an earlier `insert` into the same set. Item: `{"key":…}` or `{"ref":…}` against the
  navigation's target set; `item.token` is required for one-to-many when the child set has a concurrency token (the
  child row changes), absent for many-to-many.
- `values`, `refs` and a top-level `token` are invalid on `link` / `unlink`; so are both or neither of `key` / `ref`,
  a missing `item`, or a `ref` to a non-insert or another set → `400 invalid_batch` with a pointer.
- Composite `refs`: every member of one foreign key, all to the same insert of the target set; a partial set →
  `invalid_batch` at `/refs`.

### Server execution

Applied in batch order with the other operations, before validation and the single `SaveChanges`:

1. The navigation must be a linkable collection (otherwise `403 operation_not_allowed` at `/navigation`). Policies:
   parent `CanAsync(Update)`; one-to-many child `Update`, or `Delete` for a required unlink; many-to-many needs no
   operation on the item's set, only visibility (linking products to labels doesn't make labels writable).
2. Parent and item load through their policy filters (or come from earlier inserts by `ref`); hidden → `404
   not_found` at `/key` or `/item/key`. A one-to-many item token is checked like an update's (`412`).
3. Only the affected membership is loaded: the collection query filtered by the item's key.
4. `link` → `collection.Add(item)`; already a member → no-op; a one-to-many child of another parent moves.
   `unlink` → `collection.Remove(item)`, EF deleting the orphan or nulling its foreign key; not a member → `404
   not_found` at `/item`.
5. A foreign key set by `link` is treated like one set through `refs`: its rules are skipped, the database checks it.
6. Results: no `value` for link / unlink. Read-back of inserts and updates is unchanged.

### .NET client

- The tracker snapshots membership of linkable collections by reference; `DetectChanges` diffs it into pending links
  and unlinks per (parent, navigation, item). A new parent's items become links with a parent `ref`.
- Items must be tracked (added or attached): otherwise `InvalidOperationException`, as for references. Collections
  without `links` still throw `NotSupportedException`, naming the navigation as not linkable.
- Batch order: inserts (by `refs`), updates, links and unlinks (in detection order), deletes. Links of deleted parents
  or items are dropped.
- After a successful save: a one-to-many link sets the child's exposed foreign key members to the parent's key, a
  detach nulls them, a delete unlink detaches the child; snapshots are retaken; `SaveChangesAsync` counts links.
- Errors map to the parent's entry by operation id; an item pointer names the item.
- Composite foreign keys: `SyncForeignKeys` copies every key member; a reference to an added entity produces
  per-member `refs`. `ClientNavigation.ForeignKey` becomes a list (internal).

### TypeScript client

- Same rules on plain arrays (`product.lines.push(line)`, `splice`), diffed against snapshots by identity. Changes
  on non-linkable navigations are refused at runtime before any request, with the .NET message. Generated
  `NavigationModel`s gain `links` and `onUnlink`.
- Request bodies equal the .NET client's: golden files `batch-link.json` and `batch-composite-refs.json`, in
  `GoldenTests.cs` and `golden.test.ts`.

### Generators and samples

- Both emitters carry the new navigation keys; every generated file is regenerated (CI `--check`).
- Shop test model: `Label` set and `Product.Labels` (many-to-many over `ProductLabel`, linkable);
  `Product.Lines.AllowLinks()` (required, delete); `Category.Products.AllowLinks()` (optional, detach); `Bin` set with
  key (`Aisle`, `Shelf`) and `Product.Bin` with a composite foreign key.
- Catalog sample: the React and Blazor product editors add and remove images through `product.images` links instead
  of inserting `ProductImage` rows with a `productId` ref.

## Public API changes

- Server: `CollectionNavigationBuilder.AllowLinks()`; `WithForeignKey` accepting `p => new { p.A, p.B }`.
- Abstractions: `BatchOperationKind.Link` / `Unlink`; `BatchOperation.Ref`, `Navigation`, `Item`; `BatchItem` (`Key`,
  `Ref`, `Token`); `RestNavigationModel.Links` (`RestLinkKind?`) and `OnUnlink` (`RestUnlinkBehavior?`).
- Client and TypeScript: no new members; collection changes on linkable navigations now save instead of throwing.

## Security notes

- Membership is read-only until `AllowLinks()`, which requires the writes it implies on both sets at startup.
- Policies on both sides; visibility failures and non-members are 404, never revealing membership.
- Only the affected membership row is loaded; no whole-collection loads a client could amplify.
- No new limits: each link or unlink is a batch operation.
- `security` skill checklist: "link / unlink: navigation opt-in, both policies, 404 for hidden rows and non-members".

## Testing

- **Abstractions unit:** `BatchJson` reads link / unlink and rejects `values` / `refs` / `token`, a missing `item`,
  both or neither of `key` / `ref`; `RestModel` round-trips `links` / `onUnlink` and rejects them where they don't fit.
- **Server unit:** startup failures for `AllowLinks()` without the implied writes, payload join entities, and composite
  foreign keys out of order or not exposed.
- **Integration (SQLite + PostgreSQL), SQL asserted:** one-to-many link by key and by ref, move, required unlink
  (delete), optional unlink (detach); many-to-many link, unlink, repeated link; hidden parent or item → 404, non-member
  → 404, policy → 403, non-linkable → 403, stale item token → 412; composite foreign keys by value and by `refs`,
  partial `refs` → 400; many-to-many queries (`any`, `count`, `map`, `include`, grouped `count`) without client
  evaluation; links counted toward the operation limit.
- **Client unit (.NET and TypeScript):** detection, ordering, new parent with items, untracked item, dropped links of
  deleted rows, post-save fixups, composite `refs`; golden files.
- **End-to-end:** .NET client against Shop; TypeScript e2e for catalog image links; `AotSmoke` link scenario;
  `Samples.Tests` editor flow.

## Open questions

- Benchmarks: `ChangeDetection` and `BatchExecution` before/after are deferred from this change (as requested). Run
  them in a follow-up before the first release and record the numbers here.
- Model document `"v"`: stays 1 while nothing is published; the first release should settle how additive keys are
  versioned (lenient parsing of unknown keys vs a version bump).

## Implementation steps

1. Abstractions: batch contract (`link` / `unlink`, `BatchItem`, parent `ref`) and its reader checks; `RestModel`
   navigation keys and validation; unit tests.
2. Server: composite `WithForeignKey` (builder, EF validation, model export, composite `refs` in the executor); unit
   and integration tests.
3. Server: many-to-many skip navigations in `HasMany` for queries; Shop `Label` model; integration tests.
4. Server: `AllowLinks()`, startup checks, model export; link / unlink execution with policies, tokens and membership
   loading; integration tests.
5. Regenerate golden model and generated clients (Shop, catalog); generator snapshot tests.
6. .NET client: composite foreign keys, membership tracking, ordering, post-save fixups; unit and golden tests;
   `AotSmoke` scenario.
7. TypeScript client: same, with golden and type tests.
8. End-to-end: .NET against Shop, TypeScript e2e.
9. Catalog sample: image links in both editors; `Samples.Tests`.
10. Docs: README (batch, membership, composite keys, model document, errors), roadmap, entity-sets design pointer,
    `security` / `dotnet-conventions` / `typescript-client` skills.

## Implementation notes

- Keys are never writable, so `refs` to a newly inserted composite-key row only work when EF Core generates every key
  member (for example client-generated `Guid`s; SQLite can't auto-increment a composite key). Shop's `Bin` uses a
  `(Zone, Slot)` key of generated `Guid`s; by value, a composite foreign key may be written in part, and the visibility
  check reads the whole key as the row holds it.
- Membership loading uses EF Core's `Collection(...).Query()` filtered by the item's key. For one-to-many it's
  redundant (EF already fixes a tracked child into its parent's collection through the foreign key); for many-to-many
  it's what makes a repeated link a no-op and lets an unlink find the join row (the integration tests fail without it).
- A one-to-many link or detach runs the item set's `OnUpdate` hook, like any other change to that row (not in the
  approved design; it keeps hooks such as "updated by" stamps reliable). A required unlink deletes the item directly.
- The grouped `count` over a many-to-many navigation from the testing list was dropped: grouping keys can't be
  collections, and counts per row are covered by the projection tests.
- TypeScript `add` now starts a new row's collections as empty arrays (queried rows already had them), so items can be
  pushed onto a new parent. Membership changes compare items by reference in any order, in both clients, so
  reordering a collection is no longer mistaken for a membership change.
- Benchmarks were not run (deferred by request); see Open questions.
