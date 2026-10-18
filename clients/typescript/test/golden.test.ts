import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { cond } from "../src/index.js";
import { shop } from "./stub.js";

const chess = {
  id: 1,
  name: "Chess",
  price: 20,
  status: "Active",
  version: "00000001-0000-0000-0000-000000000000",
  createdAt: "2026-01-10T09:30:00Z",
  categoryId: 2,
  tags: ["sale"],
};
const dune = {
  id: 2,
  name: "Dune",
  price: 12.5,
  status: "Active",
  version: "00000002-0000-0000-0000-000000000000",
  createdAt: "2026-02-01T14:00:00Z",
  categoryId: 1,
  tags: [],
};

/** The same cases as the .NET client's GoldenTests, written with the TypeScript API. */
const cases: Record<string, (context: ReturnType<typeof shop>) => Promise<unknown>> = {
  "query-filter-sort-page": ({ shop }) =>
    shop.products
      .where((p) => p.price.gt(10).and(p.name.startsWith("C")))
      .orderByDescending((p) => p.price)
      .thenBy((p) => p.id)
      .skip(2)
      .take(5)
      .toList(),
  "query-lambdas": ({ shop }) =>
    shop.products
      .where((p) =>
        p.tags
          .any((t) => t.eq("sale"))
          .and(
            p.lines
              .where((l) => l.quantity.gt(1))
              .sum((l) => l.quantity.mul(l.unitPrice))
              .gt(50),
            p.lines.count().gt(1),
          ),
      )
      .take(10)
      .toList(),
  "query-strings-dates": ({ shop }) =>
    shop.products
      .where((p) =>
        p.name
          .equalsIgnoreCase("go")
          .and(
            p.name.length().gt(1),
            p.createdAt.year().eq(2026),
            p.releasedOn.ne(null),
            p.description.coalesce("").ne("x"),
          ),
      )
      .take(10)
      .toList(),
  "query-in-enum-navigation": ({ shop }) =>
    shop.products
      .where((p) => p.status.in(["Draft"]).and(p.category.name.eq("Games"), p.id.eq(3).not()))
      .take(10)
      .toList(),
  "query-select": ({ shop }) =>
    shop.products
      .select((p) => ({
        id: p.id,
        total: p.lines.sum((l) => l.quantity.mul(l.unitPrice)),
        label: p.name.concat(" (", p.categoryName, ")"),
        skus: p.lines.where((l) => l.quantity.gt(1)).select((l) => l.sku),
        category: p.category,
        expensive: cond(p.price.gt(18), "yes", "no"),
      }))
      .take(20)
      .toList(),
  "query-include-cursor": ({ shop }) =>
    shop.products
      .include((p) => p.category)
      .include((p) => p.lines)
      .orderBy((p) => p.price)
      .after("c1")
      .take(3)
      .toList(),
  "query-count": ({ shop, http }) => {
    http.respond({ items: [], totalCount: 4 });
    return shop.products.count((p) => p.price.gt(1));
  },
  "batch-unit-of-work": async ({ shop, http }) => {
    http.respond({ items: [chess, dune] });
    const [first, second] = await shop.products.take(2).toList();
    if (first === undefined || second === undefined) {
      throw new Error("The stub returns two products.");
    }

    first.price = 21;
    const puzzles = shop.categories.add({ name: "Puzzles" });
    shop.products.add({
      name: "Cube",
      description: null,
      price: 9.5,
      status: "Active",
      releasedOn: null,
      category: puzzles,
    });
    shop.products.remove(second);
    http.respond({ results: [] });
    return shop.saveChanges();
  },
};

const normalize = (json: unknown): string => JSON.stringify(json);

describe("golden request bodies shared with the .NET client", () => {
  for (const [name, run] of Object.entries(cases)) {
    it(name, async () => {
      const context = shop();
      await run(context);

      const golden: unknown = JSON.parse(
        readFileSync(new URL(`../../../tests/golden/${name}.json`, import.meta.url), "utf8"),
      );
      expect(normalize(context.http.last.body)).toBe(normalize(golden));
    });
  }
});
