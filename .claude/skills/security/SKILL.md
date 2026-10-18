---
name: security
description: RestToLinq security guidance — threat model for translating untrusted query strings into LINQ, rules for property allow-listing, limits (length, depth, node count, page size), expression-building safety, error messages, the sample API, and dependency auditing, plus a review checklist. Use when reviewing changes for security, adding operators/syntax, touching the parser, member resolution, limits, error handling or the ASP.NET Core integration, before releases, or when the user asks "is this secure?".
---

# Security

RestToLinq takes a string from an anonymous client and turns it into a database query. Treat every query string as
hostile: the library must make it impossible to read what isn't exposed and hard to make the database suffer.

## Threat model

| Asset | Why it matters |
|---|---|
| Data in unexposed properties / navigations | Filtering leaks it even without selecting it (`?filter=PasswordHash startswith 'a'` is an oracle). |
| Database and app CPU/memory | Deep nesting, huge `in` lists, unindexed sorts, large pages, `contains` on big text columns. |
| App internals | Type names, member names, stack traces and raw input in error messages. |
| Package consumers | A vulnerable default ships to every app that uses the library. |

| Adversary | Can | Mitigation |
|---|---|---|
| **Anonymous client** | Send arbitrary query strings | Allow-listed members, strict grammar, limits, constant error messages |
| **Enumerating client** | Use filters as a boolean oracle on hidden fields | Filterable ≠ readable ≠ sortable: each allow-listed separately; nothing exposed by default |
| **DoS client** | Expensive queries | Limits on length, depth, node count, `in`-list size, page size; opt-in per-member operators |
| **Supply chain** | Malicious NuGet update | Central versions, minimal deps, `dotnet list package --vulnerable` |

## Rules

### Parsing
- Hand-written or well-bounded grammar; no regex with catastrophic backtracking. Reject unknown tokens — never ignore.
- Enforce max query length **before** parsing, and depth / node count **during** parsing (not after building a giant AST).
- Literals are parsed into the target member's type (`int`, `DateTimeOffset`, enum by name from an allow-list…);
  type mismatch is a 400, not a fallback to string comparison.

### Member resolution
- **Default deny.** Only members explicitly configured as filterable/sortable/selectable are reachable.
  Never resolve a client string with `GetProperty`/`GetMember` on the entity type without an allow-list check.
- Navigation paths (`Author.Name`) are allowed only when every segment is allow-listed; cap path depth.
- No access to methods, indexers, fields, static members or collections beyond explicitly supported operators.
- Case-handling is deliberate and consistent (pick one; document it).

### Expression building
- Build `Expression` nodes directly; values go in as `Expression.Constant` (or closure-captured for parameterisation)
  — never string-concatenated into anything.
- No `Expression.Compile()`, `DynamicInvoke`, `System.Linq.Dynamic.Core`, Roslyn scripting on the request path.
- Only operators that translate on EF Core; anything forcing client evaluation is rejected.
- Paging: always apply a max page size, even when the client omits it; `skip` is bounded too.
- Sort: only allow-listed members; consider a stable tiebreaker (key) so paging is deterministic.

### Errors
- Invalid input → a 400 ProblemDetails with a **constant** message and an error code (e.g. `unknown_member`,
  `limit_exceeded`). Never echo the raw query, the member name as typed, CLR type names or stack traces.
- Log error codes and positions, not raw query strings (they can contain PII in filter values).

### Sample API
- Must demonstrate the safe configuration (explicit allow-lists, limits) — people copy samples.
- No secrets in git; connection strings in user-secrets or environment variables.

## Review checklist
- [ ] New syntax/operator: is every member it touches allow-listed and every size it introduces bounded?
- [ ] Can a client reach a member that isn't explicitly exposed (including via navigation or filter-only access)?
- [ ] Is each limit enforced early (before allocation) and tested at and past its boundary?
- [ ] Does the expression translate on EF Core (translation test, not LINQ-to-Objects only)?
- [ ] Any `Compile()`, reflection on client strings, string-built query, or dynamic LINQ? → reject.
- [ ] Error messages constant, no raw input, no type/member names as typed?
- [ ] New dependency: free/OSS, needed in core or only in integration/sample? `dotnet list package --vulnerable` clean?
- [ ] Safe defaults: does a consumer who configures nothing get a locked-down setup?
