---
name: security
description: LinqToRest security guidance — threat model for a server that executes untrusted JSON query trees and $batch change sets against EF Core, rules for the exposed-set boundary (entity sets, read-only by default), limits, type checking, write binding, policies (authorization, row filter), cursors, error messages, the client's trusted-code exceptions, and dependency auditing, plus a review checklist. Use when reviewing changes for security, adding AST nodes/operators, touching the JSON reader, type checker, member resolution, entity set configuration/reverse binding, the model document, batch executor, policies, limits or endpoints, before releases, or when the user asks "is this secure?".
---

# Security

The server takes a JSON tree or a change set from an untrusted client and turns it into EF Core queries and writes.
It must make it impossible to read or write what the configured sets and policies don't expose, and hard to make
the database suffer. Design: `docs/plans/2026-10-18-linqtorest-v1-design.md` and
`docs/plans/2026-10-20-entity-sets-design.md` → "Security notes".

## Threat model

| Asset | Why it matters |
|---|---|
| Entity members a set doesn't configure | Only configured members exist on the wire; a filter on an unexposed column would be an oracle. New columns stay hidden until configured. |
| Rows hidden by a policy `Filter` (other tenants) | Must not be readable, writable, countable or probe-able (404, never 403). |
| Server-owned columns (tenant, owner, audit) | Mass assignment via `values` in `$batch`. |
| Sets meant to be read-only | Writes through `$batch` to a set that never opted in. |
| Schema | The model document lists sets, members and types; `GET /$model` is opt-in. |
| Database and app CPU/memory | Deep trees, huge `in` lists, unindexed sorts, deep skips, large pages, big batches, nested lambdas. |
| App internals | Type/member names, stack traces, raw values in errors and logs. |
| Package consumers | A weak default ships to every app using the library. |

| Adversary | Can | Mitigation |
|---|---|---|
| **Anonymous / authenticated client** | Send arbitrary QUERY bodies and batches | Closed AST, type checker, exposed-set boundary, read-only sets by default, policies, limits, constant errors |
| **Enumerating client** | Use filters, counts, 404 vs 403, cursors as oracles | Only configured members are reachable; policy filter applied to reads, counts and writes; hidden → 404 |
| **DoS client** | Expensive queries / batches | Limits enforced while reading JSON; sort opt-in; max skip; batch size cap |
| **Supply chain** | Malicious NuGet update | Central versions, BCL-only Abstractions/Client, `dotnet list package --vulnerable` |

## Rules

### JSON reading (server)
- Enforce body size before parsing (and while streaming the body, without trusting Content-Length); bound the JSON
  depth in `JsonDocumentOptions`; enforce node count, depth, lambda nesting, `in` length and path depth while
  building the tree, so a hostile body never becomes a large object graph.
- Exactly one key per node object; unknown keys, ops, versions or wrong arity → 400. Never ignore input.

### Type checking and member resolution
- Resolve member paths only against the immutable server model built from the set configurations — never
  `Type.GetProperty(clientString)` on entity or arbitrary types. Lambda variables are scoped; an unknown variable is an error.
- Literals are converted to the operand's model type; mismatch → `type_mismatch`, never a fallback to strings.
- `orderBy` only on `IsSortable()` member paths; filter only on filterable members; `include` only on configured
  navigations within the depth limit.

### Expression building (server)
- Build `Expression` nodes directly from the typed AST, values as constants (or closure-captured for
  parameterisation). No `Compile()`, `DynamicInvoke`, dynamic LINQ or scripting on the request path.
- Member accesses are substituted with their configured entity expressions (`Property` or `Computed`); only nodes
  with a translation test exist.
- Always apply the policy `Filter` first, then client filter, sort, page (default page size when absent), projection.
- Navigation policies guard what the client reaches through member paths, includes and lambdas, but not a set's
  own computed members (`b.Computed("categoryName", p => p.Category!.Name)`): review computed members against the
  policies of the sets they read from.

### Writes (`$batch`)
- Sets are read-only by default. An operation the set's `AllowWrites(...)` doesn't include → 403
  `operation_not_allowed`, checked before loading rows or calling policies. `$batch` is mapped only when some set
  allows writes.
- `values` may contain only writable members (bound with `Property(...)`, not `IsReadOnly()`). Key, concurrency
  token, computed, read-only and unknown members → 400. Server-owned values come only from `OnInsert` / policies.
- Updates and deletes load rows through the policy `Filter`; not found or hidden → 404. Concurrency tokens are
  always checked when the set has one.
- One transaction for the whole batch; any failure rolls everything back. `refs` may only point at earlier insert
  operations in the same batch.
- Validation (built-ins, `Must`, `IEntityValidator<T>`) runs after values are applied and before `SaveChanges`; a
  422 means nothing was written. Error pointers and `member` carry only model names (an unknown member keeps the
  `/values` pointer), codes are `snake_case` constants, and the list is capped (`MaxValidationErrors`). Patterns are
  author-defined but run on untrusted strings: keep the match timeout and the portable-subset check. Validators query
  past the policies, so their codes must not reveal hidden rows; a validator bug (bad code or member) is a 500.

### Value types
- Only members configured on a value type (`IValueTypeConfiguration<T>`) exist on the wire, wherever it's used; an
  unknown segment is `unknown_member`. Value segments add no joins, nesting is capped at 3 at startup.
- Batch values replace the whole value: unknown members are `unknown_member` (pointer at the value, never the
  client's name), missing ones `invalid_batch`. A writable use must expose every EF-mapped member, so a replacement
  can't reset data the API doesn't show. Values are built with the constructor/setter plan resolved at startup.
- A whole value is only selected or null-tested; members carry filters, sorts and groups with their own flags, and
  a use's `IsNotFilterable()` covers its members.

### Grouping
- Group keys must be `IsGroupable()` and members inside sum/min/max/avg `IsAggregatable()` (both per column, like
  sorting); `{"group": {}}` only feeds direct aggregates (no nested collection lambdas); a grouped select can't read
  bare members. Policy filters apply before `GroupBy`, so counts never include hidden rows. Bounded by
  `MaxGroupKeys`, `MaxGroups` (without `take`) or the set's page limits, and the node limits.

### Cursors
- Unsigned by design; validate version, sort-spec hash, value count and types. They must never grant access beyond
  what a filter on visible, sortable members could.

### Model document
- Export contains only what the wire exposes: set, type, member and enum names, scalar types, flags. Never entity
  member names, computed expressions, namespaces or assembly names. A set's type name defaults to the entity's class
  name; `HasTypeName` changes it when that name shouldn't be public.
- `GET /$model` is off by default (`WithModelEndpoint()`) and sits under the route group's auth.
- `RestModel.Parse` enforces size and count limits and rejects unknown keys, types and versions, even though its
  input is developer files.

### Errors and logging
- ProblemDetails with constant titles, stable `code`, JSON `pointer`, and `operationId(s)` for batches. Never echo
  values, CLR type names, entity names, or member names as typed; never return stack traces.
- Log codes, pointers, set names and operation ids — not values (filters and batches contain user data).

### Client
- The client may `Compile()` captured variables and use reflection to materialize results — it runs the
  developer's own code. It must still never send anything the model forbids (fail locally first), and must not
  log request/response bodies by default.

### Hosting and sample
- `MapLinqToRest` returns a `RouteGroupBuilder`; samples show `RequireAuthorization()` and a tenant policy — people
  copy samples. No secrets in git; connection strings via user-secrets or environment variables.
- README documents QUERY-specific concerns: proxy/WAF allow-listing of the method and CORS preflight.
- `POST /{set}/$query` is the same handler as QUERY. Keep requiring `application/json` on every endpoint: it forces a
  CORS preflight, so cross-site form posts can't reach the POST endpoints.
- CORS: `AllowLinqToRest()` sets methods, headers and max-age but never origins or credentials. Samples and docs use
  explicit origins and a named policy on the group (`RequireCors`), never origin reflection or
  `SetIsOriginAllowed(_ => true)` with `AllowCredentials()`.

## Review checklist
- [ ] New AST node / operator: in the closed list, type-checked, limited, and covered by a SQLite translation test?
- [ ] Can a client reach an entity member the set doesn't configure, or a member that isn't filterable /
      sortable / writable for that use?
- [ ] Can a client write to a set (or an operation) that didn't opt in with `AllowWrites(...)`?
- [ ] Is the policy `Filter` applied to reads, counts, cursor pages, includes, updates and deletes?
- [ ] Can `values` set anything not bound with `Property(...)` (computed, key, token, read-only)?
- [ ] Value types: can a client reach an unconfigured member, or can a writable use hide an EF-mapped member?
- [ ] CORS: explicit origins, no origin reflection with credentials, scoped to the endpoints that need it?
- [ ] Is every limit enforced before allocation and tested at and past its boundary?
- [ ] Any `Compile()`, reflection on client strings, string-built query or dynamic LINQ on the server? → reject.
- [ ] Error responses and logs free of raw values, type names and member names as typed?
- [ ] New dependency: free/OSS, and not in Abstractions/Client unless BCL? `dotnet list package --vulnerable` clean?
- [ ] Safe defaults: does a consumer who configures nothing get a locked-down setup (nothing exposed, read-only,
      no `$model`)?
