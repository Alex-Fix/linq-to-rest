# Catalog sample

A realistic LinqToRest app: one API and two front ends with the same four screens, one on the TypeScript client
(React) and one on the generated .NET client (Blazor WebAssembly). It shows the library under realistic pressure:
roles, a category tree, real data volume and editing in a browser.
Design: [`docs/plans/2026-10-20-catalog-sample-design.md`](../docs/plans/2026-10-20-catalog-sample-design.md).

| Project | What it is |
|---|---|
| `Catalog.Api` | ASP.NET Core + EF Core (SQLite by default), sets, validation rules and a validator, role policies, dev login, CORS, `model.json`; published ReadyToRun |
| `Catalog.Client` | `CatalogContext.generated.cs`, generated from `model.json` (never edit it) |
| `catalog-web` | React + Vite + [Ant Design](https://ant.design) on `catalog.generated.ts`, generated from the same `model.json` |
| `Catalog.Blazor` | Blazor WebAssembly + [Ant Design Blazor](https://antblazor.com) on `Catalog.Client` |

## Run it

```sh
dotnet run --project samples/Catalog.Api              # http://localhost:5095, SQLite, seeds 200 products

(cd clients/typescript && npm ci && npm run build)             # the React app uses the local client package
(cd samples/catalog-web && npm install && npm run dev) # http://localhost:5173

dotnet run --project samples/Catalog.Blazor           # http://localhost:5174
```

PostgreSQL instead of SQLite: `docker compose -f samples/docker-compose.yml up -d`, then add
`--Database:Provider=Postgres` to the API.

**The sign-in is for development only.** The header switches between Anonymous, Viewer and Editor through
`POST /auth/dev-login`, which issues a one-hour token signed with a key from `appsettings.Development.json`. It isn't
mapped outside Development; a real app uses an identity provider.

## What each screen shows

| Screen | LinqToRest features |
|---|---|
| Browse | One query for the category tree (assembled on the client); filters translated to the server (`contains`, `in` for brands and the category with its subcategories, price range, a computed `inStock`); sorting; cursor pages with a total count; cards project only what they show plus the first image (`select` with a filtered collection). |
| Product page | `include` of a reference and a collection; a second, projected query for related products. |
| Product editor (editors) | One batch for the product and every image change; a new product and its images through `refs`; a change made elsewhere comes back as a concurrency conflict with "reload and reapply". Validation: the model's rules (required, lengths, SKU pattern, price range) mark fields and check them as you type; the server's own rules (whole cents, a SKU no other product has) come back as field errors from one 422. |
| Categories admin (editors) | Small units of work per action (attach, change, save); a self-referencing navigation (`parentId`); deleting a non-empty category shows the 409. |

Switch roles to see the policies at work: viewers and anonymous visitors see only active products (also through
counts, navigations and images), only editors can write, brands can never be deleted (the set doesn't allow it), and
`costPrice` and `internalNotes` exist on the entity but are never exposed.

## Regenerate after changing a set

```sh
dotnet run --project samples/Catalog.Api -- --export-linqtorest-model model.json
dotnet run --project src/LinqToRest.CodeGen -- cs --model samples/Catalog.Api/model.json --namespace Catalog.Client --context CatalogContext --out samples/Catalog.Client/CatalogContext.generated.cs
dotnet run --project src/LinqToRest.CodeGen -- ts --model samples/Catalog.Api/model.json --name Catalog --out samples/catalog-web/src/catalog.generated.ts
```

CI fails when `model.json` or a generated client is out of date. `tests/LinqToRest.Samples.Tests` checks what this
README claims.
