# CORS guidance — design
Status: approved · Date: 2026-10-24

## Problem

Browser front ends on another origin need CORS on the API, and LinqToRest makes every call a preflighted one
(`QUERY` isn't a simple method; `application/json` and `Authorization` force a preflight too). Today the README has a
short note, and `Catalog.Api` applies a hand-written default policy to the whole app. Nothing covers preflight
caching (without `Access-Control-Max-Age` Chrome re-preflights after 5 s, roughly doubling round trips), `GET $model`
with `If-None-Match`/`ETag`, custom headers, cookie credentials, or scoping the policy to the LinqToRest routes. Last
open item of roadmap Milestone 2.

## Goals / Non-goals

Goals:
- A small helper, `CorsPolicyBuilder.AllowLinqToRest()`, that allows exactly what LinqToRest clients send and caches
  preflights; the developer still chooses origins and credentials.
- A README CORS subsection: when it's needed, why every call is preflighted, the recommended setup, custom headers,
  credentials, and what not to do.
- `Catalog.Api` uses a named policy scoped to the LinqToRest group (and the dev login).

Non-goals:
- Choosing origins or credentials for the developer, or any "allow any origin" shortcut.
- A `MapLinqToRest(...).WithCors(origins)` wrapper that hides ASP.NET Core's CORS model.
- Client changes (the TS `fetch` option already covers cookie mode), browser end-to-end tests (Playwright backlog item).

## Constraints check

| Constraint | This feature |
|---|---|
| Translatable | Not affected (hosting only). |
| Untrusted input | The helper never sets origins or credentials. Docs name the trap ASP.NET Core can't catch: `SetIsOriginAllowed(_ => true)` / reflecting the origin together with `AllowCredentials()`. `application/json` stays required on every endpoint so cross-site form posts can't reach POST endpoints. |
| No dynamic code | Not affected. |
| Wire contract | Not affected: no nodes, no `"v"` change. |
| Public API | One new extension class in `LinqToRest.Server.Hosting`; additive. |
| Dependencies | `Microsoft.AspNetCore.Cors` is in the `Microsoft.AspNetCore.App` framework reference the server already has. Abstractions/Client untouched. |
| Errors | Not affected: a disallowed origin simply gets no CORS headers (ASP.NET Core behaviour). |

## Chosen approach

A `CorsPolicyBuilder` extension plus documentation and a sample switch.
- Docs only: leaves every app to rediscover the method, headers, exposed `ETag` and max-age.
- `WithCors(origins)` wrapper: more public API, hides the standard CORS model, and tempts an origin shortcut.

## Design

### Helper

```csharp
namespace LinqToRest.Server.Hosting;

public static class LinqToRestCorsPolicyBuilderExtensions
{
    /// Allows what LinqToRest clients send: methods QUERY, POST and GET; headers Content-Type, Authorization and
    /// If-None-Match; exposes ETag; caches preflights for preflightMaxAge (default 10 minutes). Doesn't choose
    /// origins or credentials: call WithOrigins(...) (and AllowCredentials() for cookies) yourself.
    public static CorsPolicyBuilder AllowLinqToRest(this CorsPolicyBuilder policy, TimeSpan? preflightMaxAge = null);
}
```

- `QUERY`/`POST`: queries, `{set}/$query`, `$batch`. `GET` + `If-None-Match` + exposed `ETag`: `$model` revalidation
  (and the future model-drift check).
- Additive: uses `WithMethods`, `WithHeaders`, `WithExposedHeaders`, `SetPreflightMaxAge`, which append, so chaining
  `.WithHeaders("X-Tenant")` afterwards keeps the standard headers. Overlap with `AllowAnyHeader()` etc. is harmless,
  so there's no check.
- 10-minute default: cuts preflights while a tightened policy reaches browsers within minutes (Chrome caps at 2 h).
- Negative or zero `preflightMaxAge` → `ArgumentOutOfRangeException`.

Recommended setup:

```csharp
builder.Services.AddCors(o => o.AddPolicy("linqtorest", p => p
    .WithOrigins("https://shop.example")
    .AllowLinqToRest()
    .WithHeaders("X-Tenant")));          // only if your app adds headers
app.UseCors();
app.MapLinqToRest("/api").RequireCors("linqtorest");
```

### Sample

`Catalog.Api`: named `"linqtorest"` policy (origins from `Cors:Origins`, `.AllowLinqToRest()`), applied with
`RequireCors` to the `MapLinqToRest("/api")` group and to `MapDevLogin` (the browser apps post to it). `UseCors()`
stays; with no default policy it only acts on endpoints that carry `RequireCors`.

### Docs

README "Deployment notes → CORS" becomes a subsection:
1. When it's needed: only a front end on another origin (scheme, host or port). Same-origin hosting or a dev-server
   proxy (Vite `server.proxy`) needs none.
2. Every call is preflighted: `QUERY` isn't simple, and `application/json`/`Authorization` force it anyway, so
   `POST {set}/$query` doesn't avoid it.
3. The recommended setup above, what `AllowLinqToRest()` sets and why max-age matters.
4. Custom headers (TS `headers` option, .NET `HttpClient` handlers) must be added with `WithHeaders`.
5. Credentials: bearer tokens need no `AllowCredentials()`. Cookies need `AllowCredentials()`, explicit origins, and
   `credentials: "include"` on fetch — in the TS client via
   `fetch: (url, init) => fetch(url, { ...init, credentials: "include" })`. Never `SetIsOriginAllowed(_ => true)` or
   reflect the request origin with credentials: any site could read the user's data.
6. `application/json` stays required on every endpoint; it's what keeps cross-site form posts out.

Also: `clients/typescript/README.md` CORS line links the README section and shows cookie mode; `security` skill gets a
checklist item; roadmap strikes the CORS row (Milestone 2 done); `samples/README.md` mentions the scoped policy.

## Public API changes

- New: `LinqToRest.Server.Hosting.LinqToRestCorsPolicyBuilderExtensions.AllowLinqToRest(CorsPolicyBuilder, TimeSpan?)`.
- Nothing changed or removed.

## Security notes

- Origins and credentials are always the developer's explicit choice; no wildcard helper.
- Docs warn against origin reflection with credentials (the case ASP.NET Core's `AllowAnyOrigin` + `AllowCredentials`
  check doesn't catch).
- Recommend scoping with `RequireCors` on the group rather than a default policy for the whole app.
- Keep requiring `application/json` on every endpoint.

## Testing

- `LinqToRest.Server.UnitTests`: `AllowLinqToRest` builds methods `QUERY`/`POST`/`GET`, headers
  `Content-Type`/`Authorization`/`If-None-Match`, exposed `ETag`, max-age 10 min by default and overridable, no
  origins, no credentials; a following `WithHeaders("X-Tenant")` keeps the standard headers; non-positive max-age
  throws.
- `LinqToRest.Server.IntegrationTests` (`EndpointTests`, group with `RequireCors`): `QUERY` preflight from an allowed
  origin returns allow-origin/methods/headers/max-age; `$batch` preflight allowed; disallowed origin gets no CORS
  headers; cross-origin `GET $model` exposes `ETag`; an endpoint outside the group gets no CORS headers.
- `LinqToRest.Samples.Tests`: existing preflight test also asserts max-age; dev-login preflight allowed; an actual
  cross-origin `QUERY` gets `Access-Control-Allow-Origin` on the 200.

## Open questions

- None blocking. Revisit the max-age default if users report slow policy rollouts.

## Implementation steps

1. `AllowLinqToRest()` in `LinqToRest.Server.Hosting` with XML docs, plus unit tests.
2. Integration tests in `EndpointTests` for preflights, `$model` exposed `ETag` and group scoping.
3. `Catalog.Api`: named policy with `AllowLinqToRest()`, `RequireCors` on the group and dev login; extend the sample
   tests.
4. Docs: README CORS subsection, TS README cookie note, `samples/README.md`, `security` skill checklist, roadmap
   (strike CORS, Milestone 2 done).
