---
name: security
description: RestToLinq security guidance — threat model for a server that executes untrusted JSON query trees and $batch change sets against EF Core, rules for the DTO boundary, limits, type checking, write binding, policies (authorization, row filter), cursors, error messages, the client's trusted-code exceptions, and dependency auditing, plus a review checklist. Use when reviewing changes for security, adding AST nodes/operators, touching the JSON reader, type checker, member resolution, mapping/reverse binding, batch executor, policies, limits or endpoints, before releases, or when the user asks "is this secure?".
---

# Security

The server takes a JSON tree or a change set from an untrusted client and turns it into EF Core queries and writes.
It must make it impossible to read or write what the DTO model and policies don't expose, and hard to make the
database suffer. Design: `docs/plans/2026-10-18-resttolinq-v1-design.md` → "Security notes".

## Threat model

| Asset | Why it matters |
|---|---|
| Entity data outside the DTO projection | Entities are never exposed; a filter on an unmapped column would be an oracle. |
| Rows hidden by a policy `Filter` (other tenants) | Must not be readable, writable, countable or probe-able (404, never 403). |
| Server-owned columns (tenant, owner, audit) | Mass assignment via `values` in `$batch`. |
| Database and app CPU/memory | Deep trees, huge `in` lists, unindexed sorts, deep skips, large pages, big batches, nested lambdas. |
| App internals | Type/member names, stack traces, raw values in errors and logs. |
| Package consumers | A weak default ships to every app using the library. |

| Adversary | Can | Mitigation |
|---|---|---|
| **Anonymous / authenticated client** | Send arbitrary QUERY bodies and batches | Closed AST, type checker, DTO boundary, policies, limits, constant errors |
| **Enumerating client** | Use filters, counts, 404 vs 403, cursors as oracles | Only DTO members are reachable; policy filter applied to reads, counts and writes; hidden → 404 |
| **DoS client** | Expensive queries / batches | Limits enforced while reading JSON; sort opt-in; max skip; batch size cap |
| **Supply chain** | Malicious NuGet update | Central versions, BCL-only Abstractions/Client, `dotnet list package --vulnerable` |

## Rules

### JSON reading (server)
- Enforce body size before parsing (and while streaming the body, without trusting Content-Length); bound the JSON
  depth in `JsonDocumentOptions`; enforce node count, depth, lambda nesting, `in` length and path depth while
  building the tree, so a hostile body never becomes a large object graph.
- Exactly one key per node object; unknown keys, ops, versions or wrong arity → 400. Never ignore input.

### Type checking and member resolution
- Resolve member paths only against the immutable `DtoModel` — never `Type.GetProperty(clientString)` on entity
  or arbitrary types. Lambda variables are scoped; an unknown variable is an error.
- Literals are converted to the operand's model type; mismatch → `type_mismatch`, never a fallback to strings.
- `orderBy` only on `IsSortable()` member paths; filter only on filterable members; `include` only on configured
  navigations within the depth limit.

### Expression building (server)
- Build `Expression` nodes directly from the typed AST, values as constants (or closure-captured for
  parameterisation). No `Compile()`, `DynamicInvoke`, dynamic LINQ or scripting on the request path.
- DTO member accesses are substituted with their mapped entity expressions; only nodes with a translation test exist.
- Always apply the policy `Filter` first, then client filter, sort, page (default page size when absent), projection.
- Navigation policies guard what the client reaches through member paths, includes and lambdas, but not the
  mapping's own computed members (`CategoryName = p.Category.Name`): review computed members against the policies of
  the sets they read from.

### Writes (`$batch`)
- `values` may contain only writable members (bound directly in the default projection). Key, concurrency token,
  read-only and unknown members → 400. Server-owned values come only from `OnInsert` / policies.
- Updates and deletes load rows through the policy `Filter`; not found or hidden → 404. Concurrency tokens are
  always checked when the DTO has one.
- One transaction for the whole batch; any failure rolls everything back. `refs` may only point at earlier insert
  operations in the same batch.

### Cursors
- Unsigned by design; validate version, sort-spec hash, value count and types. They must never grant access beyond
  what a filter on visible, sortable members could.

### Errors and logging
- ProblemDetails with constant titles, stable `code`, JSON `pointer`, and `operationId(s)` for batches. Never echo
  values, CLR type names, entity names, or member names as typed; never return stack traces.
- Log codes, pointers, set names and operation ids — not values (filters and batches contain user data).

### Client
- The client may `Compile()` captured variables and use reflection to materialize results — it runs the
  developer's own code. It must still never send anything the model forbids (fail locally first), and must not
  log request/response bodies by default.

### Hosting and sample
- `MapRestToLinq` returns a `RouteGroupBuilder`; samples show `RequireAuthorization()` and a tenant policy — people
  copy samples. No secrets in git; connection strings via user-secrets or environment variables.
- README documents QUERY-specific concerns: proxy/WAF allow-listing of the method and CORS preflight.

## Review checklist
- [ ] New AST node / operator: in the closed list, type-checked, limited, and covered by a SQLite translation test?
- [ ] Can a client reach an entity member that isn't in the DTO projection, or a member that isn't filterable /
      sortable / writable for that use?
- [ ] Is the policy `Filter` applied to reads, counts, cursor pages, includes, updates and deletes?
- [ ] Can `values` set anything not bound directly in the default projection?
- [ ] Is every limit enforced before allocation and tested at and past its boundary?
- [ ] Any `Compile()`, reflection on client strings, string-built query or dynamic LINQ on the server? → reject.
- [ ] Error responses and logs free of raw values, type names and member names as typed?
- [ ] New dependency: free/OSS, and not in Abstractions/Client unless BCL? `dotnet list package --vulnerable` clean?
- [ ] Safe defaults: does a consumer who configures nothing get a locked-down setup?
