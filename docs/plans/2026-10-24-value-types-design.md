# Value types (owned / complex types) — design
Status: approved, in progress (see Implementation notes) · Date: 2026-10-24

## Problem

Real domains keep value objects inside entities: an address on a customer, dimensions on a product, money. EF Core
maps them as complex types (`ComplexProperty`) or owned types (`OwnsOne`, optionally `ToJson()`), but a set can only
expose scalars, primitive collections, computed values and navigations to other sets. Today an API author has to
flatten them by hand into `Computed` members, which are read-only and lose the structure in both generated clients.
Listed as a v1 non-goal and a roadmap backlog item.

## Goals / Non-goals

Goals:
- A set exposes a **single** value object (EF complex type or `OwnsOne`, including `ToJson`) as a structured member,
  with its exposed members opted in **once per value type** and reused wherever it appears.
- Queries filter, sort, group and select through value paths (`shippingAddress/city`), also through navigations and
  in lambdas; rows carry nested objects.
- Batches **replace** a value object as a whole; built-in rules, `Must` and validators apply, with nested pointers.
- Both generated clients get value types (.NET immutable records, TypeScript readonly interfaces) with LINQ / proxy
  path support, change tracking and local validation. The .NET client stays trim/AOT-safe.

Non-goals:
- Collections of value objects (`OwnsMany`, complex collections): provider-specific JSON querying; later.
- Comparing whole values (`address == other`), sorting or grouping by a whole value.
- Partial (per-member) updates of a value object.
- Navigations or computed members inside value types; `Must` rules at the value-type level.
- Per-use member subsets (a second CLR type is the answer).

## Constraints check

| Constraint | This feature |
|---|---|
| **Translatable** | `c.Address.City` is a column access for complex types, `OwnsOne` with table splitting and `OwnsOne(...).ToJson()` on SQLite and PostgreSQL. Whole-value selects project the member columns into the row, guarded by a null test for nullable uses; the CLR object is never materialized. The null test on optional complex types and `OwnsOne` is proven first (step 1); a mapping where it doesn't translate has its nullable uses refused at startup. Whole-value comparison is refused (partial EF support, none for owned). |
| **Untrusted input** | Only configured value-type members exist on the wire (`unknown_member` otherwise). Value nesting ≤ 3 and no cycles (startup); node depth and body limits still apply; value segments add no joins, so they don't count as navigations. Batch values are closed, typed objects: unknown members rejected, every member required. A writable use must expose every EF-mapped member, so a replacement never silently resets hidden data. |
| **No dynamic code** | Paths bind to `MemberExpression`s built at startup. Values are constructed with a constructor / setter plan resolved at startup (configured members only); nothing is compiled on the request path. |
| **Wire contract** | Query nodes unchanged (`a/b` paths already exist). Rows and batch `values` gain nested objects. The model document gains `valueTypes`, additively at `"v": 1`: documents without value types are byte-identical; older readers reject documents that use them (unknown key). Nothing is published yet, and earlier additions (`rules`, `groupable`) were made the same way. New golden bodies. |
| **Public API** | Additive: `IValueTypeConfiguration<T>`, `ValueTypeBuilder<T>`, value member builders, `EntitySetBuilder.Value(...)`, `AddValueType<T>`; `RestValueTypeModel`, `RestModel.ValueTypes`; `ValueBinding<T>`; TS value types. |
| **Dependencies** | Abstractions/Client stay BCL-only and trim/AOT-safe (delegate bindings, AotSmoke). EF-specific checks live in Server. |
| **Errors** | Existing codes only: `unknown_member`, `not_filterable`, `not_sortable`, `not_groupable`, `type_mismatch`, `not_writable`, `invalid_batch`, 422 `required` and rule codes. Pointers extend to `/operations/N/values/shippingAddress/city`; never echo client-supplied names or values. |

## Chosen approach

**Value types in the model document** (`valueTypes`), nested objects on the wire, generated value types in both
clients, whole-value writes.
- *Flattening on the server* (`Property(c => c.Address.City).HasName("addressCity")`): no client changes, but no
  structure, no nullable values, per-field writes; contradicts whole-value replacement.
- *Model `"v": 2` for documents with value types*: clearer failure for old readers, but nothing is published and
  earlier additions stayed at v1.
- *Inline per-use configuration*: different subsets per use would need one generated type per use.

## Design

### Server configuration

```csharp
public sealed class AddressType : IValueTypeConfiguration<Address>
{
    public void Configure(ValueTypeBuilder<Address> b)
    {
        b.HasTypeName("Address");                       // defaults to the CLR name
        b.Property(a => a.Street).HasMaxLength(200);
        b.Property(a => a.City).IsSortable().IsGroupable();
        b.Property(a => a.PostalCode).HasPattern("^[0-9A-Z -]{3,10}$");
        b.Value(a => a.Geo);                            // nested value type, configured elsewhere
    }
}

public sealed class CustomerSet : IEntitySetConfiguration<Customer>
{
    public void Configure(EntitySetBuilder<Customer> b)
    {
        b.ToSet("customers").FromSource<ShopDbContext>(db => db.Customers);
        b.Property(c => c.Name);
        b.Value(c => c.ShippingAddress);                // writable in a writable set; nullability from EF
        b.Value(c => c.BillingAddress).IsReadOnly();
        b.AllowWrites(WriteOperations.Insert | WriteOperations.Update);
    }
}
```

- `AddSetsFromAssembly` also registers `IValueTypeConfiguration<T>` classes; `AddValueType<T>(...)` registers one
  explicitly. `Value(...)` (on sets and value types) on an unconfigured type fails at startup.
- Value member builders offer `HasName`, `IsSortable`, `IsNotFilterable`, `IsGroupable`, `IsAggregatable` and the
  validation rules — no `IsReadOnly`: writability belongs to the use (`Value(...).IsReadOnly()`, `HasName`,
  `IsNotFilterable`).
- **Startup validation:** the member is an EF complex property or `OwnsOne` (incl. `ToJson`) of that CLR type; value
  members are EF-mapped scalars (or enums / nested value types) of supported types; nesting ≤ 3 with no cycles; type
  names unique across sets, value types and enums. For a **writable** use: the value type exposes every EF-mapped
  member, and is constructible — a constructor whose parameters match the members by name (records), or a
  parameterless constructor with settable properties; the plan is resolved once.

### Model document

```json
"valueTypes": [
  {"name": "Address", "properties": [
    {"name": "street", "type": "string", "rules": {"maxLength": 200}},
    {"name": "city", "type": "string", "sortable": true, "groupable": true},
    {"name": "geo", "type": "GeoPoint", "nullable": true}]}],
"sets": [{"name": "customers", "properties": [
    {"name": "shippingAddress", "type": "Address", "nullable": true, "writable": true},
    {"name": "billingAddress", "type": "Address"}]}]
```

- A property `type` may name a value type. On a value-typed property, only `nullable`, `writable` and `filterable`
  are allowed (`RestModel` rejects `sortable`, `groupable`, `aggregatable`, `rules`, `collection`). Value-type
  properties never carry `writable` (it's the use's).
- Effective permissions of a path such as `shippingAddress/city`: filterable if every step is; sortable, groupable,
  aggregatable as the leaf member says; cursor sorting also needs every step non-nullable.
- `valueTypes` is written only when non-empty, in a stable order; `RestModel` validates references, cycles and depth.

### Queries and rows

```json
{"v":1,
 "filter":{"and":[{"eq":[{"member":"shippingAddress/city"},"Lviv"]},
                  {"ne":[{"member":"billingAddress"},null]}]},
 "orderBy":[{"member":"shippingAddress/city"}],
 "select":{"new":{"name":{"member":"name"},"ship":{"member":"shippingAddress"},
                  "lat":{"member":"shippingAddress/geo/lat"}}}}
```

- Value paths work wherever member paths do: `filter`, `orderBy`, `groupBy`, `select`, through navigations
  (`customer/shippingAddress/city`) and inside lambdas.
- A whole value may only be compared with `null` (`eq` / `ne`, nullable uses); compared with an object, used in `in`,
  `orderBy` or `groupBy` → `type_mismatch` / `not_sortable` / `not_groupable`. Unknown segment → `unknown_member`.
- Rows nest the object; a null value is `null`:
  `{"id":4,"name":"Ada","shippingAddress":{"street":"Main 1","city":"Lviv","geo":null},"billingAddress":null}`.
- Translation: `shippingAddress/city` → `c.ShippingAddress.City`. Whole values project
  `c.ShippingAddress == null ? null : new object?[] { … }` (no null test for required uses), written by a nested
  `Shape` as a JSON object.

### Batch writes and validation

```json
{"id":"o1","op":"update","set":"customers","key":{"id":4},"token":"…",
 "values":{"shippingAddress":{"street":"Main 1","city":"Lviv","geo":{"lat":49.84,"lng":24.03}}}}
```

- The object replaces the whole value. On update, an absent property keeps the value; `null` clears it (nullable
  uses only, else `type_mismatch`).
- Every exposed member must be present (nested too): missing → `invalid_batch` at
  `/operations/N/values/shippingAddress/city`; unknown → `unknown_member` at `/operations/N/values/shippingAddress`
  (never naming the client's member); scalar mismatches → `type_mismatch` at the member pointer; read-only use →
  `not_writable`.
- Insert without a non-nullable value use → 422 `required` at `/operations/N/values/shippingAddress`.
- The server constructs a new instance with the startup plan and assigns it to the entity property (as scalars are
  today). Complex types are compared by value; `OwnsOne` replacement (table splitting or JSON) becomes an update of
  the same row / column — proven per mapping, incl. null ↔ value.
- Built-in rules on value members run on every write of the value; errors point at the nested member. Set-level
  `Must(c => c.ShippingAddress, …)` and `IEntityValidator` see the constructed value; `AddError` accepts a nested
  member path.

### .NET client

```csharp
public sealed partial record Address
{
    public required string Street { get; init; }
    public required string City { get; init; }
    public GeoPoint? Geo { get; init; }
}

var lviv = await ctx.Customers
    .Where(c => c.ShippingAddress != null && c.ShippingAddress.City == "Lviv")
    .OrderBy(c => c.ShippingAddress!.City)
    .Select(c => new { c.Name, c.ShippingAddress })
    .ToListAsync();

customer.ShippingAddress = customer.ShippingAddress! with { City = "Kyiv" };
await ctx.SaveChangesAsync();
```

- Translation: member chains become value paths; `== null` / `!= null` become `eq` / `ne` null. Whole-value
  comparison, sort or group throws `NotSupportedException` locally.
- Binding: generated `ValueBinding<T>` (factory delegate from read member values, getters); `ClientModel.Bind` checks
  it against `valueTypes`. No reflection; AotSmoke covers it.
- Tracking: records are immutable, so the snapshot keeps the reference and a change is `!Equals(snapshot, current)`;
  the whole value is sent.
- Local validation checks value members' rules; `RestValidationException.ErrorsFor` keys nested errors by dotted CLR
  path (`ShippingAddress.City`).

### TypeScript client

```ts
export interface Address { readonly street: string; readonly city: string; readonly geo: GeoPoint | null }

ctx.customers.where(c => c.shippingAddress.city.eq("Lviv")).orderBy(c => c.shippingAddress.city);
customer.shippingAddress = { ...customer.shippingAddress!, city: "Kyiv" };
```

- Proxy lambdas already build paths. Value members get the `Sortable` / `Groupable` / `Aggregatable` markers; a
  whole value isn't sortable (type error).
- Tracking compares value objects structurally (JS can mutate despite `readonly`); the whole value is sent.
- `errorsFor` keys nested errors as `shippingAddress.city`.
- New golden bodies `query-value-filter` and `batch-value`, identical from both clients.

### Sample

- `Catalog.Api`: `Product.Dimensions` (width, height, depth, weight; optional complex type), shown on the product
  page and edited in the product editor; `Brand.Contact` (`OwnsOne`: website, country). Both front ends filter by
  `dimensions/weight`. Model and clients regenerated.

## Public API changes

All additive.
- **Abstractions:** `RestValueTypeModel` (name, properties); `RestModel.ValueTypes`, `FindValueType`, constructor
  parameter; property `Type` may name a value type.
- **Server:** `IValueTypeConfiguration<T>`, `ValueTypeBuilder<T>` (`HasTypeName`, `Property`, `Value`), value
  property builder, `EntitySetBuilder<T>.Value(...)` returning a use builder (`HasName`, `IsReadOnly`,
  `IsNotFilterable`), `LinqToRestServerBuilder.AddValueType<T>(...)`; `AddSetsFromAssembly` scans value types.
  `EntityValidationContext.AddError` accepts a nested member path.
- **Client:** `ValueBinding<T>`; `ClientModel.Bind` takes value bindings; `SetBinding<T>.Value(...)` for value
  members; `ErrorsFor` dotted keys for nested members.
- **TypeScript:** generated value interfaces and model metadata; `errorsFor` dotted keys.
- **Generator:** `cs` emits records + bindings, `ts` emits interfaces.

## Security notes

- Only configured value-type members exist on the wire, in queries, rows and batch values.
- A writable use must expose every EF-mapped member, so replacing a value can't reset unexposed data.
- Batch value objects are closed: unknown → `unknown_member` without echoing the name; missing → `invalid_batch`.
- Nesting ≤ 3, no cycles (startup); value segments don't add joins; node depth and body limits apply.
- Policy filters are unchanged in kind (value members are columns of the same row).
- Security skill: checklist item for value types.

## Testing

Integration shop value types, one per mapping:

| Use | Mapping |
|---|---|
| `Product.Size` (`Dimensions`, nested `Weight`) | `ComplexProperty`, required |
| `Product.Origin` (`Place`) | `ComplexProperty`, optional (EF 10) |
| `Category.Contact` | `OwnsOne`, table splitting, optional |
| `OrderLine.Shipping` (`Place`, reused) | `OwnsOne(...).ToJson()` |

| Project | Covers |
|---|---|
| Abstractions | `valueTypes` round-trip; refused flags on value properties; unknown value type; cycles; nesting > 3; documents without value types byte-identical |
| Server unit | each startup failure (unmapped member, not complex/owned, writable use hiding a member, no usable constructor, nesting/cycle, duplicate name); golden `model.json` |
| Server integration (SQLite + PostgreSQL) | filter / sort / cursor / group / select through value paths and navigations; `eq null` per mapping; whole-value select incl. null; SQL logged (no client evaluation); error codes; batch insert / update / null ↔ value per mapping; missing / unknown member; read-only use; rules with nested pointers |
| Client unit | translation (paths, `== null`, local throws), golden bodies, `ValueBinding` mismatches, record-equality tracking, local rules, `ErrorsFor` dotted keys |
| CodeGen | C# records + bindings, TS interfaces + markers from the golden model |
| TypeScript | proxy paths, structural tracking, type tests, golden bodies |
| End-to-end + AotSmoke | generated client queries and saves value objects on both providers; AOT app reads and saves one |
| Samples.Tests | catalog value members filter and save |

Benchmarks: before/after of the query, materialization and tracking suites (hot paths).

## Implementation notes

Where the implementation differs from the sections above, this list wins.

1. **Spike (step 1): every mapping works on SQLite and PostgreSQL.** Members of required and nested complex types
   filter and sort in SQL; `== null` / `!= null` and the guarded whole-value projection
   (`x == null ? null : new object?[] {…}`) run in SQL for optional complex types (EF 10), table-split `OwnsOne` and
   `OwnsOne(...).ToJson()`; assigning a new instance (incl. null ↔ value) updates the same row or JSON column. No
   mapping's nullable uses need refusing. One CLR type (`Place`) can be a complex type on one entity and an owned JSON
   type on another. Projecting a whole *owned* value by itself needs a non-tracking query; the server never does
   that (it projects member columns). `tests/LinqToRest.Server.IntegrationTests/ValueTypeProviderTests.cs` keeps
   these checks.

2. **Server configuration (step 3).** Value-type members can't be collections. A value type used by several properties
   must map the same members with the same types and nullability everywhere (startup error otherwise), since the
   model has one shape per type. A writable use requires every EF-mapped member (non-shadow, non-key properties,
   complex properties and owned references) to be exposed; nested values are written with the outermost property, so
   only that one is `writable` in the model. The constructor is the public one with the most parameters that all match
   members by name (ignoring case) and type; the other members need a public `set` or `init`. The golden models gain
   value types in steps 4 (integration shop) and 7 (golden client model), when something reads them.

3. **Batch writes (step 5).** An insert that doesn't send a non-nullable value keeps the entity's default, like
   scalars do; it's 422 `required` only when the entity leaves the value null (the design said always `required`,
   which would break every existing insert of a set that gains a value). When a value type's constructor or setter
   throws an `ArgumentException` (its own guards), the client gets 400 `type_mismatch` at the value's pointer. Nested
   validation errors carry the slash path as `member` (`size/weight/unit`), the same form as query paths and
   pointers; `EntityValidationContext.IsModified` / `AddError` accept `e => e.Size.Weight.Unit`.

4. **.NET client (step 6).** `ValueBinding<T>` takes a factory of the member values in binding order
   (`v => new Address { Street = (string)v[0]!, … }`), so immutable records with `required`/`init` members work
   without reflection; `SetBinding<T>.Value(...)` and `ValueBinding<T>.Value(...)` take the nested binding, so
   `ClientModel.Bind` keeps its signature. Hand-written classes bound by reflection get a reflected value binding
   (constructor matching members, then setters). Change tracking snapshots a value's member values (nested too)
   rather than relying on record equality, so a mutable value changed in place is detected as well. A whole value
   that's compared with anything but null, sorted or grouped throws `NotSupportedException` before sending. The
   golden request bodies come with the golden model in step 7, once the generator emits value types.

5. **C# generator (step 7).** Value types become `sealed partial record`s with `required init` members for
   non-nullable ones. A set property holding a non-nullable value starts as `null!` (a record has no empty value);
   local validation reports `required` if it's still null when saving, so code creating such rows must set it. Value
   bindings are fields of a nested `ValueBindings` class, emitted leaves first (static fields initialize in order),
   and name members with literals: a field named like its type would shadow the type inside `nameof`. The golden
   model's products gained an optional `size` (`DimensionsDto` with a nested `WeightDto`); inserts now send
   `"size": null`, so `batch-unit-of-work.json` changed, and `query-value-filter.json` and `batch-value.json` are new.

6. **TypeScript (step 8).** The generated model embeds each value type as a `const` (`ValueTypeModel`, leaves first)
   that members of kind `"value"` reference, so tracking and validation need no model lookups. Value proxies extend
   `ValueProxy<V>` (members, plus `eq(null)` / `ne(null)`; not `Sortable` or `Groupable`), so a whole value in
   `orderBy` is a compile error; at run time a whole value outside a select or null test throws before sending. A
   value member named `eq` or `ne` is a generator error. Saves write values in model member order whatever order the
   object was built in, so request bodies equal the .NET client's.

## Open questions

- Collections of value objects, whole-value equality and partial updates: later, on demand.

## Implementation steps

1. Spike: integration shop value types for every mapping; prove member access, null tests and whole-value
   projection translate on SQLite and PostgreSQL, and that replacing a value updates in place (EF only, no LinqToRest
   changes yet).
2. Abstractions: `RestValueTypeModel`, `valueTypes` read/write and validation; unit tests.
3. Server: `IValueTypeConfiguration<T>`, builders, `Value(...)`, registration, startup validation, model export;
   unit tests and golden models.
4. Server: bind value paths in queries (filter, sort, cursor, group, lambdas, navigations), whole-value rows and null
   comparisons; integration tests.
5. Server: batch writes (construction plan, replacement, nested errors) and validation with nested pointers;
   integration tests.
6. .NET client: `ValueBinding<T>`, translation, materialization, tracking, local validation, `ErrorsFor`; golden
   bodies; unit tests.
7. CodeGen `cs`: records and bindings; regenerate C# clients; end-to-end tests; AotSmoke.
8. TypeScript client and CodeGen `ts`: interfaces, markers, structural tracking, `errorsFor`; golden bodies; e2e.
9. Catalog sample: `Product.Dimensions`, `Brand.Contact`, both front ends; model and clients regenerated;
   sample tests.
10. Benchmarks before/after; docs (README, roadmap, security / conventions / typescript-client skills).
