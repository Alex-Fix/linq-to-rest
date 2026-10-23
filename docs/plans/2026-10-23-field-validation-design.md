# Field-level validation — design
Status: approved, implemented (see Implementation notes) · Date: 2026-10-23

Milestone 2 of [`2026-10-20-roadmap.md`](2026-10-20-roadmap.md): "Member-level pointers for batch values" and
"Problem → field mapping helpers".

## Problem

A form can't show which field a save failed on:

- Rejected batch values point at `/operations/N/values`, not the member, so a UI can name the row but not the field
  (found building the catalog editor).
- Generated clients only send typed, writable members, so the errors that exist (`type_mismatch`, `not_writable`,
  `unknown_member`) rarely reach a real form. What a form does hit (a required name, a price range, a duplicate SKU)
  can't be expressed at all: the server has no validation hook, and a duplicate comes back as a 409 `conflict` with no
  pointer.
- Errors arrive one at a time, so fixing a form takes a round trip per field.

## Goals / Non-goals

**Goals**
- Existing batch errors point at the member (`/operations/N/values/<member>`) wherever the member is one the model
  knows.
- API authors declare rules on the set: built-ins (`IsRequired`, `HasMinLength`/`HasMaxLength`, `HasRange`,
  `HasPattern`), custom `Must` rules and async `IEntityValidator<TEntity>` classes from DI.
- Built-in rules are exported in the model document; both clients check them on save (without sending) and offer
  `Validate` and rule metadata for forms.
- Every validation error of a batch comes back at once: one 422 problem with errors per operation and member, and one
  client exception mapping them to tracked entities and members.

**Non-goals**
- Display messages or localization: everything is a stable code; apps map codes to text.
- Rules on delete, on navigations, or on computed members.
- Exporting custom rules (`Must`, validators) to clients.
- DataAnnotations support.
- Mapping provider constraint errors (unique indexes, foreign keys) to members; a validator covers the cases that
  matter, and other refusals stay 409 `conflict`.

## Constraints check

| Constraint | This feature |
|---|---|
| **Translatable** | Unaffected: rules run on entity values in memory after values are applied, not in SQL. A validator's own queries are the author's code. |
| **Untrusted input** | Values are untrusted: patterns (author-defined) run with a 100 ms match timeout and are at most 500 characters; the number of reported errors is capped (100). Pointers and `member` only carry names from the model; an unknown member keeps the `/values` pointer. |
| **No dynamic code** | Rules are built-ins or author code compiled into the app (`Must`, validators); nothing is built from the request. |
| **Wire contract** | Additive. The model document gains an optional `rules` object per writable property (stays `"v": 1`; documents without rules are unchanged). New 422 problem `validation_failed` with `errors`. Existing batch errors get deeper pointers, same codes and statuses. Batch requests are unchanged, so the golden request bodies stay. |
| **Public API** | Additive. Server: property-builder rule methods, `Must`, `ValidateWith<T>`, `IEntityValidator<T>`, `EntityValidationContext<T>`, a limit. Client: `RestValidationException`, `RestValidationError`, `RestSaveException.Member`, `Validate`, `Rules`, `PropertyRules`. TypeScript: the same. |
| **Dependencies** | BCL only (`System.Text.RegularExpressions`). Abstractions owns the rule model, codes and the problem contract; the client stays trim/AOT-safe (interpreted `Regex`). |
| **Errors** | Stable codes only: `required`, `too_short`, `too_long`, `out_of_range`, `invalid_format`, `too_many_errors`, and custom `snake_case` codes. Values are never echoed in problems or logs. |

## Chosen approach

**Collect, then reject once.** The batch is applied, then built-ins, `Must` rules and validators run for every inserted
or updated row; all errors are returned in one 422 problem before `SaveChanges`, so nothing is written. Clients raise
one exception whether the errors were found locally or by the server.

- *Not fail-fast* (first error as today's 400 with a member pointer): simpler, but a form fixes one field per round
  trip.
- *Not pointers only:* generated clients rarely trigger the existing errors, so forms would gain little.
- *Not provider constraint mapping:* provider-specific error parsing, and still no business rules.
- *Not DataAnnotations:* reflection-based, tied to entity classes, text messages instead of codes.

## Design

### Authoring rules (server)

```csharp
public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.AllowWrites(WriteOperations.All);

        // Built-ins: exported to the model document, checked by clients too.
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Sku).IsRequired().HasPattern("^[A-Z0-9-]{3,20}$");
        builder.Property(p => p.Price).HasRange(0.01m, 100_000m);
        builder.Property(p => p.Tags).HasMaxLength(10);          // collections: item count

        // Custom rules: server-only, synchronous, named by a stable code.
        builder.Must(p => p.Price, price => price % 0.01m == 0, "too_precise");
        builder.Must(p => p.SaleEndsOn is null || p.SaleEndsOn > p.ReleasedOn, "sale_ends_before_release", p => p.SaleEndsOn);

        // Async rules with services (uniqueness, cross-row checks).
        builder.ValidateWith<ProductValidator>();
    }
}

public sealed class ProductValidator(CatalogDbContext db) : IEntityValidator<Product>
{
    public async ValueTask ValidateAsync(EntityValidationContext<Product> context, CancellationToken cancellationToken)
    {
        if (context.IsModified(p => p.Sku)
            && await db.Products.AnyAsync(p => p.Sku == context.Entity.Sku && p.Id != context.Entity.Id, cancellationToken))
        {
            context.AddError(p => p.Sku, "duplicate");
        }
    }
}
```

**Built-ins** (on `EntityPropertyBuilder`, fixed codes):

| Method | Applies to | Code | Meaning |
|---|---|---|---|
| `IsRequired()` | any | `required` | not null; strings and collections also not empty (whitespace-only strings count as empty) |
| `HasMinLength(n)` / `HasMaxLength(n)` | strings, primitive collections | `too_short` / `too_long` | string length / item count |
| `HasRange<T>(T? min, T? max)` | numbers, dates, times | `out_of_range` | inclusive bounds; either may be null |
| `HasPattern(regex)` | strings | `invalid_format` | whole-value match is the author's job (`^…$`) |

Startup refuses: a rule that doesn't fit the property type (`HasRange` on a string, a `T` other than the property's
type), a rule on a read-only property (it could never be triggered), min greater than max, and a pattern longer than
500 characters or that doesn't compile with `RegexOptions.ECMAScript`.

**`Must`** has two overloads: member + value predicate + code, and entity predicate + code + optional member to blame
(without one, the error is about the row). **`IEntityValidator<TEntity>`** is created per request from DI
(`ActivatorUtilities`, so it can take the `DbContext`); its `EntityValidationContext<TEntity>` has `Entity`,
`Operation` (insert or update), `IsModified(member)`, `AddError(member, code)` and `AddError(code)`.

**What runs when:** on insert, every rule. On update, built-ins and member `Must` rules only for members the batch
sends values for (an edit isn't blocked by older data that a newer rule would reject); entity-level `Must` rules and
validators always. Rules run after every operation is applied (so `refs` are resolved and validators see related
rows), in operation order, sequentially (one `DbContext`). Deletes aren't validated.

**Codes:** custom codes match `^[a-z][a-z0-9_]{0,63}$` and can't reuse a built-in code. `Must` codes are checked at
startup; a validator's code or member is checked when it's added, and a bad one is a server bug (500, logged), not a
client error.

### Wire contract

**Model document:** writable properties may carry `rules`; only built-ins are exported.

```json
{"name": "name",  "type": "string",  "writable": true, "rules": {"required": true, "maxLength": 200}},
{"name": "sku",   "type": "string",  "writable": true, "rules": {"required": true, "pattern": "^[A-Z0-9-]{3,20}$"}},
{"name": "price", "type": "decimal", "writable": true, "sortable": true, "rules": {"min": 0.01, "max": 100000}},
{"name": "tags",  "type": "string",  "collection": true, "writable": true, "rules": {"maxLength": 10}}
```

`min`/`max` are wire values of the property's type (dates as ISO strings). The document stays `"v": 1`: `rules` is
optional, and a model without rules is byte-for-byte unchanged. The parser is strict, so older generators and clients
reject a document with rules ("unknown key 'rules'"), which is right: they'd ignore rules the server enforces.

**422 problem**, every error of every operation, capped at 100:

```json
{
  "type": "urn:linqtorest:error:validation_failed", "title": "Invalid values.", "status": 422,
  "code": "validation_failed", "pointer": "/operations",
  "errors": [
    {"operation": "o0", "pointer": "/operations/0/values/price", "member": "price", "code": "out_of_range"},
    {"operation": "o0", "pointer": "/operations/0/values/sku",   "member": "sku",   "code": "duplicate"},
    {"operation": "o1", "pointer": "/operations/1",              "code": "sale_ends_before_release"}
  ]
}
```

`member` is the model's wire name (so clients needn't parse pointers); an error without one is about the row. A
required member missing from an insert points where its value would be. At the cap, collection stops and the last
entry is `{"code": "too_many_errors"}`. Validation happens before `SaveChanges`, so a 422 means nothing was written;
provider refusals (409 `conflict`) and concurrency conflicts (412) keep their shapes, and a uniqueness validator
turns the usual duplicate-key 409 into a field error.

**Existing batch errors:** `not_writable`, `type_mismatch` and a foreign key whose target doesn't exist (`not_found`)
point at `/operations/N/values/<member>`; `unknown_member` keeps `/operations/N/values` (the name came from the client
and isn't echoed). Codes and statuses are unchanged.

### Clients

Both clients behave the same; names follow each language.

```csharp
try
{
    await catalog.SaveChangesAsync();
}
catch (RestValidationException exception)          // derives from RestSaveException
{
    foreach (var error in exception.Errors)       // RestValidationError(Entity, Member, Code)
    {
        // error.Entity is the tracked row; error.Member is the CLR name ("Price"), or null for the whole row.
    }

    var fields = exception.ErrorsFor(product);    // { "Price": ["out_of_range"], "Sku": ["duplicate"] }
    if (exception.IsLocal) { /* caught before sending */ }
}

IReadOnlyList<RestValidationError> errors = catalog.Validate(product);   // every built-in rule, nothing sent
PropertyRules rules = catalog.Products.Rules(p => p.Name);               // Required, MinLength, MaxLength, Min, Max, Pattern
```

```ts
try { await catalog.saveChanges(); }
catch (error) {
  if (error instanceof RestValidationError) {
    error.errorsFor(product);        // { price: ["out_of_range"], sku: ["duplicate"] }
  }
}
catalog.validate(product);           // RestValidationIssue[]
catalog.products.rules.name;         // { required: true, maxLength: 200 }, typed from the generated model
```

- `SaveChangesAsync` first checks the exported built-ins on changed rows (inserts: all rules; updates: modified
  members, like the server) and throws without sending (`IsLocal = true`); tracked state is unchanged.
- A 422 produces the same exception (`IsLocal = false`), with operation ids mapped to tracked entities and wire names
  to CLR members.
- `RestSaveException.Member` (TypeScript: `member`) is filled for other save errors whose pointer names a member.
- No display text: apps map codes to messages, using the rule values ("At most {maxLength} characters").
- Patterns: C# uses `Regex` with `RegexOptions.ECMAScript`, a 100 ms timeout and no `Compiled` (AOT-safe);
  TypeScript `new RegExp(pattern)` without flags. The server refuses non-ECMAScript patterns at startup, so all three
  agree. `IsRequired` means the same everywhere.

## Public API changes

- **Abstractions (added):** `RestPropertyRules` on `RestPropertyModel` (parse/write/limits); `ErrorCodes.ValidationFailed`,
  `Required`, `TooShort`, `TooLong`, `OutOfRange`, `InvalidFormat`, `TooManyErrors`; the validation problem contract.
- **Server (added):** `EntityPropertyBuilder.IsRequired`, `HasMinLength`, `HasMaxLength`, `HasRange<T>`, `HasPattern`;
  `EntitySetBuilder<T>.Must` (two overloads), `ValidateWith<TValidator>()`; `IEntityValidator<TEntity>`,
  `EntityValidationContext<TEntity>`; `Limits.MaxValidationErrors` (default 100).
- **Client (added):** `RestValidationException`, `RestValidationError`, `RestSaveException.Member`,
  `RestDataContext.Validate(object)`, `RestSet<T>.Rules(...)`, `PropertyRules`.
- **TypeScript (added):** `RestValidationError`, `RestValidationIssue`, `RestSaveError.member`, `validate`, set `rules`.
- **Generated code:** rules travel in the embedded model; the TypeScript generator emits typed `rules`.
- **Behaviour:** batch error pointers for known members are one segment deeper.

## Security notes

- Error count capped (`MaxValidationErrors`, 100); patterns limited to 500 characters and a 100 ms match timeout (a
  timeout is `invalid_format`, logged with set and member only).
- Pointers and `member` carry only model names; unknown members aren't echoed; values never appear in problems or
  logs.
- Validators run in the batch transaction after policies are applied; they're author code, and the README warns
  against leaking hidden rows through codes (for example `duplicate` against a row the caller can't see).
- Custom codes are constrained to `snake_case`; a validator emitting a bad code or member is a 500 bug, not echoed.

## Testing

| Project | Tests |
|---|---|
| Abstractions.UnitTests | `rules` parse/write round trip; bad rule keys, types and limits rejected; problem `errors` contract |
| Server.UnitTests | startup checks (rule vs type, ECMAScript patterns, read-only members, min > max, codes); member pointers for existing errors; unknown member keeps `/values` |
| Server.IntegrationTests (SQLite + PostgreSQL) | each built-in at and just past its boundary; `Must` member- and entity-level; validator with a DB query; updates check only sent members; errors collected across operations; nothing written on 422; the cap; uniqueness validator instead of 409 |
| Client.UnitTests | local check on save (insert, update, nothing sent); 422 mapped to entities and CLR members; `Validate`, `Rules`, `ErrorsFor`; `Member` on other save errors |
| EndToEnd.Tests | the same rule fails locally, and on the server when the batch is sent raw, with identical errors |
| TypeScript (Vitest, golden) | as Client.UnitTests, plus a 422 golden response fixture; e2e against `Catalog.Api` |
| CodeGen.UnitTests | C# and TypeScript snapshots with rules |
| AotSmoke | a local validation failure under Native AOT |
| Benchmarks | `BatchExecution` before/after (with and without rules); `ChangeDetection` with rules |

## Samples and docs

- `Catalog.Api`: `Product` gets built-ins (name, SKU pattern, price range, tag count), one `Must` rule and a SKU
  uniqueness validator, replacing the 409 "duplicate SKU" message.
- Both editors show errors next to their fields, take required/maxlength from `Rules`, and validate as the user types.
- README: a "Validation" section, the new codes, the 422 shape. Roadmap: both Milestone 2 items done.

## Implementation notes

Where the implementation differs from the sections above, this list wins.

1. **Portable patterns.** .NET's `RegexOptions.ECMAScript` accepts constructs JavaScript lacks or reads differently
   (`\p{…}`, inline options, `\A`/`\z`, comments), and its `$` also matches before a final newline, so
   `"ABC\n"` would pass `^[A-Z]+$` on the server and fail in the browser. A small scanner in Abstractions refuses
   non-portable constructs (also atomic groups, class subtraction, `[]`) and rewrites the two that differ for .NET:
   `$` → `\z`, `\s`/`\S` → JavaScript's whitespace set. `RestPropertyRules.Check` is the one definition of the
   rules, shared by the server and the .NET client.
2. **Ranges:** `HasRange<T>(T min, T max)` plus `HasMin<T>`/`HasMax<T>` instead of nullable bounds: C# can't infer
   `T` for `T?` parameters from `0.01m`, so the designed `HasRange(0.01m, 100_000m)` wouldn't compile.
3. **Foreign keys set through `refs`** aren't checked by built-in rules, on the server or the clients: the value
   exists only once the referenced insert is saved (a required foreign key would otherwise always fail). Found
   writing the .NET client.
4. **Order and cap:** per operation, built-ins, then `Must` rules, then validators (created once per request with
   `ActivatorUtilities`). A member that broke a rule isn't checked by its other rules (one error per member).
   `QueryLimits.MaxValidationErrors` (default 100) caps the list, followed by one `too_many_errors` entry. `Must`
   rules or validators on a set without inserts or updates fail at startup; so does a member `Must` rule on a member
   that isn't writable (it could never be sent).
5. **API names:** the issue contract is `ValidationIssue` with `Location` (CA1720; `pointer` on the wire), read and
   written by `BatchJson.ReadIssues`/`WriteIssues`. The .NET client returns the shared `RestPropertyRules` from
   `RestSet<T>.Rules(...)` instead of a separate `PropertyRules`; `RestValidationException.IsTruncated` reports
   `too_many_errors`; `ErrorsFor` lists row errors under `""`. `EntityValidationContext.Request` is the
   `PolicyContext`.
6. **TypeScript:** `validate` and `rules` live on the set (`catalog.products.validate(p)`), not the context: a plain
   object doesn't know its set. The 422 contract is a shared fixture, `tests/golden/response-validation-failed.json`,
   read by both clients' tests.
7. **Encoding:** model documents and generated files keep .NET's default JSON escaping, so `+` in a pattern appears
   as `\u002B` (same value).
8. **Test models:** the golden and integration shops got lenient rules (name length, non-negative price), so the
   generator snapshots, end-to-end tests and the Native AOT smoke app (now with a validation scenario) cover rules
   without changing the golden request bodies. Server integration tests use their own product set with every kind
   of rule.
9. **Performance:** not measured yet; the `BatchExecution` and `ChangeDetection` before/after runs were stopped
   before finishing. Without rules, a batch's extra work is one pass over its operations; with rules, one check per
   ruled member (a cached `Regex` for patterns).

## Open questions

- Should `Must` rules or validators run on delete (e.g. "can't delete a category with products")? Proposed: not now;
  those stay 409 `conflict` from the database.

## Implementation steps

1. Abstractions: `RestPropertyRules` in the model document (parse, write, limits), validation error codes and problem
   contract; tests.
2. Server: member pointers for existing batch errors; tests.
3. Server: built-in rule builders, startup checks, model export; tests.
4. Server: `Must` rules; tests.
5. Server: `IEntityValidator<T>`, `ValidateWith<T>()`, collecting errors across operations, 422 response, cap;
   integration tests; `BatchExecution` benchmark.
6. Client: rules from the model, `Rules`, `Validate`, local check on save, `RestValidationException`,
   `RestSaveException.Member`; tests; `ChangeDetection` benchmark.
7. TypeScript client and generator: the same; golden fixtures.
8. Regenerate golden models and every generated client; end-to-end tests; AotSmoke scenario.
9. Samples: catalog rules, validator, both editors.
10. Docs (README, conventions), roadmap.
