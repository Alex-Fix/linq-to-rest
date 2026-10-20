# Catalog sample — design
Status: approved · Date: 2026-10-20

Milestone 1 of [`2026-10-20-roadmap.md`](2026-10-20-roadmap.md).

## Problem

The only sample, `samples/Shop.*`, has two sets, API-key tenants, a console client and a Node script. It's a good
getting-started path, but it doesn't show LinqToRest under realistic pressure: no browser, no roles, no real
editing flow, no navigation trees, no data volume worth paging. Gaps that a real web app would hit (CORS for
`QUERY`, trimming in Blazor WebAssembly, error display, unit-of-work editing in a UI) stay invisible.

## Goals / Non-goals

**Goals**
- A realistic catalog app in `samples/Catalog/`: one API, a React front end on the TypeScript client and a Blazor
  WebAssembly front end on the generated C# client, with the same four screens.
- Anonymous browsing; a dev-only login with two roles (viewer, editor); editors change the catalog and each edit
  is saved as one batch.
- Exercise and document what a browser client needs: CORS (including the `QUERY` preflight), bearer tokens, error
  display, concurrency handling.
- Tests that pin the sample's claims, and CI that keeps its model and generated clients current.
- Keep `samples/Shop.*` unchanged as the minimal getting-started sample.

**Non-goals**
- Orders, customers, reviews, back office (only the catalog).
- A real identity provider, user registration or ASP.NET Core Identity.
- Browser end-to-end tests (Playwright) for now.
- A UI component library or design system; plain CSS and Razor.
- Fixing library gaps inside the sample: they become roadmap items (Milestone 2).

## Constraints check

| Constraint | This sample |
|---|---|
| **Translatable** | Self-referencing navigation (category parent/children), computed counts and flags, `contains` search, `in` filters, projections with `map`. SQLite by default, PostgreSQL optional; self-references get library integration tests on both providers. |
| **Untrusted input** | Browsers are untrusted by definition. Reads are public; writes need the `editor` role (`CanAsync`); drafts are hidden from non-editors by a role-keyed policy filter (hidden → 404, including navigations and counts). Unconfigured columns (`costPrice`, `internalNotes`) stay unreachable. Brands are never deletable (`AllowWrites` without `Delete`). |
| **No dynamic code** | No library change. |
| **Wire contract** | No change. A feature the protocol lacks becomes a roadmap item, not a sample workaround. |
| **Public API** | No library API change planned. |
| **Dependencies** | Sample-only, free/OSS: `Microsoft.AspNetCore.Authentication.JwtBearer`, Blazor WebAssembly packages, React, react-router, Vite. Abstractions/Client stay BCL-only. |
| **Errors** | The UIs show problem responses by `code`, mapping `pointer` to the form field; nothing echoes input. |

## Chosen approach

**A separate realistic sample next to the minimal one.** `samples/Catalog/` holds `Catalog.Api`, `Catalog.Client`
(generated C#), `Catalog.Blazor` and `catalog-web` (React).

- *Not growing Shop into the catalog app:* the README quick start, the TypeScript e2e tests and `shop-ts` rely on
  Shop staying small, and every protocol test would depend on a UI-sized sample.
- *Not several small focused samples:* the point is the realistic combination (roles, trees, paging, editing).
- *Not one front end:* the user asked for both, as a side-by-side comparison of the two clients.

## Design

### Layout

```
samples/Catalog/
  Catalog.Api/        ASP.NET Core + EF Core (SQLite default, --Database:Provider=Postgres), sets, policies,
                      dev login, CORS, model.json (exported, checked in)
  Catalog.Client/     CatalogContext.generated.cs (linqtorest-gen cs), never edited by hand
  Catalog.Blazor/     standalone Blazor WebAssembly, references Catalog.Client
  catalog-web/        React + Vite, catalog.generated.ts (linqtorest-gen ts), linqtorest via file: dependency
  README.md           how to run; which screen shows which feature
```

All .NET projects are in the solution (`samples` folder) and non-packable through `samples/Directory.Build.props`.

### Domain and sets

| Set | Exposed | Computed | Navigations | Writes (editors only) |
|---|---|---|---|---|
| `categories` | id, name (sortable), parentId | productCount | parent (self), children, products | insert, update, delete |
| `brands` | id, name (sortable) | productCount | products | insert, update |
| `products` | id, sku, name (sortable), description (not filterable), price (sortable), status, stock (sortable), categoryId, brandId, createdAt (sortable, read-only), version (token) | inStock, tags, imageCount | category, brand, images | insert, update, delete |
| `productImages` | id, productId, url, sortOrder | — | — | insert, update, delete |

- `Product.CostPrice` and `Product.InternalNotes` exist on the entity and are never configured.
- Tags are seeded and read-only (`Computed` list); images are a writable set, so adding a product with images uses
  batch `refs`.
- Deleting a category that still has products fails with a database constraint → 409 `conflict`.
- Seed: about 200 products, about 15 categories over 3 levels, 8 brands.

### Permissions

- One `SetPolicy<T>` per entity type: reads allowed for everyone; insert/update/delete only for `editor`.
- Product filter: anonymous users and viewers see only `Active` products; editors also see `Draft` and
  `Discontinued`.
- `OnInsert` sets `createdAt`; the concurrency token rotates on save (SQLite has no rowversion).
- The LinqToRest route group doesn't call `RequireAuthorization()`; policies decide per operation.

### Dev login and CORS

- `POST /auth/dev-login {"role":"viewer"|"editor"}` → a short-lived JWT signed with a dev key from configuration,
  mapped only in Development. No user store.
- CORS policy for the two dev origins (Vite and Blazor dev server, from configuration): methods `QUERY`, `POST`,
  headers `Content-Type`, `Authorization`. Both apps call the API directly (no dev-server proxy), so the
  preflight path is real.

### Screens (both front ends)

1. **Browse:** category tree (one query, `include` children, `productCount`); product grid with name search
   (`contains`), brands (`in`), price range, in-stock toggle, category with subcategories (`any`); sort by
   name, price or newest; cursor "Load more" with total count; cards project id, name, price, `inStock` and the
   first image (`select` + `map`).
2. **Product page:** full product with `include` category, brand, images; "more in this category" (projection,
   `take: 4`).
3. **Product editor** (editors): tracked product; edit fields, add, reorder or remove images, save as one batch;
   new product with images via `refs`; 412 → "reload and reapply"; problem `pointer` shown next to its field.
4. **Categories admin** (editors): add, rename, move (`parentId`); deleting a non-empty category shows the 409.

A header role switcher (Anonymous / Viewer / Editor) calls the dev login, keeps the token in memory and re-runs
queries.

- **React:** Vite, React, react-router, strict TypeScript with the repo's ESLint/Prettier rules, plain CSS. Token
  through the client's `headers` option; one context per editor screen (one unit of work per form).
- **Blazor:** `AddCatalogContext(...)` from the generated client, a `DelegatingHandler` for the bearer token, Razor
  components with LINQ. Release publish is trimmed; as a stopgap the project roots `Catalog.Client`
  (`<TrimmerRootAssembly Include="Catalog.Client" />`) until the client is trim-safe (Milestone 2).

## Public API changes

None in `src/`. If the sample needs one, it's designed separately (brainstorming) and listed on the roadmap.

## Security notes

- The dev login exists only in Development and signs with a dev-only key from configuration; the sample README
  says so in bold. Production would use a real identity provider.
- CORS allows only configured origins, never `*` with credentials.
- Writes need the editor role on every set; brands can't be deleted at all; drafts are invisible to non-editors.
- Unexposed columns demonstrate the exposed-set boundary; the sample tests assert they're `unknown_member`.

## Testing

| Where | Covers |
|---|---|
| `tests/LinqToRest.Samples.Tests` (new, SQLite, test server) | role filter hides drafts (lists, counts, navigations); unexposed columns → `unknown_member`; anonymous/viewer batch → 403 `forbidden`; brand delete → 403 `operation_not_allowed`; editor product + images batch with `refs`; non-empty category delete → 409; stale edit → 412; dev login only in Development; CORS preflight for `QUERY`; `model.json` equals a fresh export |
| `LinqToRest.Server.IntegrationTests` | self-referencing navigation (parent/children, includes, filters through the parent) on SQLite and PostgreSQL |
| CI | re-export `Catalog.Api/model.json` and diff; `linqtorest-gen cs`/`ts --check` for both clients; `dotnet publish -c Release` of `Catalog.Blazor` (trimmed); `npm run check` in `catalog-web` |

## Open questions

- Browser end-to-end tests (Playwright) once the screens are stable?
- Should `catalog-web` and `Catalog.Blazor` be served by `Catalog.Api` in a "run everything" mode, or stay
  separate dev servers?
- Image hosting: seeded URLs point at placeholder images; serving files is out of scope.

## Implementation steps

1. Self-referencing navigation integration tests in `LinqToRest.Server.IntegrationTests` (both providers); fix the
   library if they fail.
2. `Catalog.Api`: entities, DbContext (SQLite/PostgreSQL), seed, sets, policies; export `model.json`.
3. `Catalog.Api`: dev login (JWT, Development only) and CORS from configuration.
4. `tests/LinqToRest.Samples.Tests`: the claims in the Testing table.
5. `Catalog.Client`: generated C# context; CI checks (model re-export, `--check`).
6. `catalog-web`: Vite + React skeleton, generated TypeScript client, role switcher, Browse screen.
7. `catalog-web`: product page, product editor (batch with `refs`, 412 handling, field errors), categories admin;
   CI job (`npm run check`).
8. `Catalog.Blazor`: skeleton, token handler, Browse screen.
9. `Catalog.Blazor`: product page, editor, categories admin; CI Release publish (trimmed) with the stopgap root.
10. Docs: `samples/Catalog/README.md`, README link and CORS section, `dotnet-conventions` and `typescript-client`
    skills, CLAUDE.md layout.
