import { describe, expect, inject, it } from "vitest";
import { RestConcurrencyError, RestHttpError, RestQueryError, RestSaveError } from "../src/index.js";
import { createShop } from "./shop.generated.js";

const baseUrl = inject("baseUrl");
const client = (options: { readonly transport?: "query" | "post"; readonly key?: string | null } = {}) =>
  createShop({
    baseUrl,
    headers: options.key === null ? {} : { "X-Api-Key": options.key ?? "dev-acme-key" },
    ...(options.transport === undefined ? {} : { transport: options.transport }),
  });

describe("TypeScript client against samples/Shop.Api", () => {
  it("filters, sorts and projects on the server", async () => {
    const names = await client()
      .products.where((p) => p.price.ge(12).and(p.tags.any((t) => t.eq("classic")).or(p.category.name.eq("Books"))))
      .orderByDescending((p) => p.price)
      .select((p) => ({ name: p.name, label: p.name.concat(" (", p.categoryName, ")"), tags: p.tags }))
      .take(10)
      .toList();

    expect(names).toEqual([
      { name: "Chess", label: "Chess (Games)", tags: ["classic"] },
      { name: "Dune", label: "Dune (Books)", tags: ["scifi"] },
    ]);
  });

  it("sends the same query over POST $query", async () => {
    const viaQuery = await client()
      .products.orderBy((p) => p.name)
      .select((p) => p.name)
      .take(5)
      .toList();
    const viaPost = await client({ transport: "post" })
      .products.orderBy((p) => p.name)
      .select((p) => p.name)
      .take(5)
      .toList();

    expect(viaPost).toEqual(viaQuery);
  });

  it("pages with cursors and streams every visible row", async () => {
    const shop = client();
    const seen: string[] = [];
    let page = await shop.products
      .orderBy((p) => p.name)
      .take(1)
      .toCursorPage({ includeTotalCount: true });
    seen.push(...page.items.map((p) => p.name));
    while (page.nextCursor !== undefined) {
      page = await shop.products
        .orderBy((p) => p.name)
        .after(page.nextCursor)
        .take(1)
        .toCursorPage();
      seen.push(...page.items.map((p) => p.name));
    }

    const streamed: string[] = [];
    for await (const product of shop.products.orderBy((p) => p.name).stream()) {
      streamed.push(product.name);
    }

    expect(seen.slice(0, 3)).toEqual(["Chess", "Dune", "Go"]);
    expect(streamed).toEqual(seen);
    expect(seen).not.toContain("Hidden");
  });

  it("saves a unit of work and reads it back", async () => {
    const shop = client();
    const go = await shop.products.include((p) => p.category).first((p) => p.name.eq("Go"));
    const oldVersion = go.version;
    go.price = 31;
    const puzzles = shop.categories.add({ name: "Puzzles" });
    const cube = shop.products.add({ name: "Cube", price: 9.5, status: "Active", category: puzzles });

    expect(await shop.saveChanges()).toBe(3);

    expect(go.version).not.toBe(oldVersion);
    expect(cube.id).toBeGreaterThan(0);
    expect(cube.categoryId).toBe(puzzles.id);
    expect(cube.categoryName).toBe("Puzzles");
    const fresh = await client().products.first((p) => p.id.eq(cube.id));
    expect([fresh.name, fresh.categoryName, fresh.createdAt.endsWith("Z")]).toEqual(["Cube", "Puzzles", true]);
  });

  it("reports concurrent edits", async () => {
    const mine = client();
    const theirs = client();
    const dune = await mine.products.first((p) => p.name.eq("Dune"));
    const other = await theirs.products.first((p) => p.name.eq("Dune"));
    other.price = 13;
    await theirs.saveChanges();
    dune.price = 14;

    const error = await mine.saveChanges().catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(RestConcurrencyError);
    expect((error as RestSaveError).entities).toEqual([dune]);
  });

  it("surfaces server rejections as typed errors", async () => {
    const tooMany = await client()
      .products.take(101)
      .toList()
      .catch((caught: unknown) => caught);
    const unauthenticated = await client({ key: null })
      .products.take(1)
      .toList()
      .catch((caught: unknown) => caught);
    const unsortable = await client()
      .products.orderBy((p) => p.categoryId as never)
      .take(1)
      .toList()
      .catch((caught: unknown) => caught);

    expect(tooMany).toBeInstanceOf(RestQueryError);
    expect(tooMany).toMatchObject({ status: 400, code: "limit_exceeded", limit: "pageSize" });
    expect(unauthenticated).toBeInstanceOf(RestHttpError);
    expect(unauthenticated).toMatchObject({ status: 401 });
    expect(unsortable).toBeInstanceOf(Error);
  });
});
