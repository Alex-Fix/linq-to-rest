# linqtorest

TypeScript client for [LinqToRest](https://github.com/Alex-Fix/linq-to-rest) APIs: typed queries that run on the
server (filters, sorting, projections, offset and cursor pages) and a unit of work that saves every change in one
atomic batch. No runtime dependencies; ESM; Node ≥ 20 and modern browsers.

## Generate the model

The row types and the client model come from the model document your LinqToRest server exports
(`ExportLinqToRestModelAsync`), so they always match the server:

```sh
dotnet tool install LinqToRest.CodeGen   # provides the linqtorest-gen command
dotnet linqtorest-gen ts --model ../Shop.Api/model.json --name Shop --out src/shop.generated.ts   # your API's exported model
```

Each set only offers the writes the server allows: `add` needs inserts, `update` updates and `remove` deletes; the
others are compile errors (and throw at run time if called anyway). Add `--check` in CI to fail when the file is
stale. The tool warns about `decimal` and `long` members: JavaScript
numbers keep about 15 significant digits and are exact only up to 2^53.

## Query

```ts
import { cond } from "linqtorest";
import { createShop } from "./shop.generated.js";

const shop = createShop({
  baseUrl: "https://shop.example/api/",
  headers: async () => ({ Authorization: `Bearer ${await token()}` }),
});

const games = await shop.products
  .where((p) =>
    p.price.lt(25).and(
      p.category.name.eq("Games"),
      p.tags.any((t) => t.eq("sale")),
    ),
  )
  .orderByDescending((p) => p.price)
  .select((p) => ({
    name: p.name,
    label: p.name.concat(" (", p.categoryName, ")"),
    expensive: cond(p.price.gt(18), "yes", "no"),
  }))
  .take(20)
  .toList(); // { name: string; label: string; expensive: string }[]
```

Lambdas receive typed proxies: members have methods instead of operators (`eq`, `gt`, `contains`, `startsWith`,
`year`, `add`, `mul`, `and`, `or`, `not`…), collections have `any`, `all`, `count`, `sum`, `min`, `max`, `avg`,
`where` and `select`. Comparing a number with a string, ordering a boolean or sorting by a member the server doesn't
allow doesn't compile.

Operators: `where`, `include`, `orderBy`/`thenBy` (and `…Descending`), `select`, `skip`, `after`, `take`,
`asNoTracking`. Terminals: `toList` (requires `take`), `toCursorPage`, `stream` (follows cursor pages), `first`,
`firstOrDefault`, `single`, `singleOrDefault`, `count`, `any`; each takes `{ signal }`.

## Save

```ts
const chess = await shop.products.first((p) => p.name.eq("Chess"));
chess.price = 21;
const puzzles = shop.categories.add({ name: "Puzzles" });
shop.products.add({ name: "Cube", price: 9.5, status: "Active", category: puzzles });
await shop.saveChanges(); // one atomic $batch; generated keys, tokens and computed members are filled in
```

Rejections throw `RestQueryError`, `RestSaveError` or `RestConcurrencyError` (with `status`, `code`, `location` and,
for saves, the affected `entities`); other failures throw `RestHttpError`.

## Notes

- Dates, times, `Guid` and binary values are ISO 8601 / canonical strings, never `Date` objects.
- `transport: "post"` sends queries to `POST {set}/$query` instead of HTTP `QUERY`, for infrastructure that blocks
  unknown methods. Browser apps on another origin need CORS on the API for the method, `Content-Type` and auth headers.
- One context per unit of work (a form, a page): tracked rows are plain objects in an identity map.

MIT licensed.
