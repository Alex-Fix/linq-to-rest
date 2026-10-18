import { describe, expect, it } from "vitest";
import { RestConcurrencyError } from "../src/index.js";
import { shop } from "./stub.js";

const chess = () => ({
  id: 1,
  name: "Chess",
  price: 20,
  status: "Active",
  version: "00000001-0000-0000-0000-000000000000",
  createdAt: "2026-01-10T09:30:00Z",
  categoryId: 2,
  tags: ["sale"],
  categoryName: "Games",
});

describe("unit of work", () => {
  it("tracks queried DTOs by key", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] }).respond({ items: [{ ...chess(), name: "Other" }] });

    const first = await s.products.first();
    const again = await s.products.first();

    expect(again).toBe(first);
    expect(first.name).toBe("Chess");
    expect(s.entry(first).state).toBe("unchanged");
  });

  it("doesn't track projections or asNoTracking results", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] }).respond({ items: [chess()] });

    const first = await s.products.asNoTracking().first();
    const second = await s.products.asNoTracking().first();

    expect(second).not.toBe(first);
    expect(s.entry(first).state).toBe("detached");
  });

  it("wires included navigations to tracked instances", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [{ id: 2, name: "Games" }] }).respond({
      items: [
        {
          ...chess(),
          category: { id: 2, name: "Games" },
          lines: [{ id: 11, sku: "C1", quantity: 2, unitPrice: 20 }],
        },
      ],
    });

    const games = await s.categories.first();
    const product = await s.products
      .include((p) => p.category)
      .include((p) => p.lines)
      .first();

    expect(product.category).toBe(games);
    expect(product.lines.map((line) => s.entry(line).state)).toEqual(["unchanged"]);
  });

  it("saves changed members as an update and refreshes from the result", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] });
    const product = await s.products.first();
    product.price = 21;
    http.respond({
      results: [{ id: "o0", value: { ...chess(), price: 21, version: "00000002-0000-0000-0000-000000000000" } }],
    });

    expect(s.entry(product).state).toBe("modified");
    expect(s.entry(product).isModified("price")).toBe(true);
    expect(await s.saveChanges()).toBe(1);

    expect([http.last.method, http.last.path]).toEqual(["POST", "/api/$batch"]);
    expect(http.last.body).toEqual({
      v: 1,
      operations: [
        {
          id: "o0",
          op: "update",
          set: "products",
          key: { id: 1 },
          values: { price: 21 },
          token: "00000001-0000-0000-0000-000000000000",
        },
      ],
    });
    expect(product.version).toBe("00000002-0000-0000-0000-000000000000");
    expect(s.entry(product).state).toBe("unchanged");
    expect(await s.saveChanges()).toBe(0);
  });

  it("inserts in reference order and fills generated keys", async () => {
    const { shop: s, http } = shop();
    const product = s.products.add({ name: "Cube", price: 9.5 });
    const category = s.categories.add({ name: "Puzzles" });
    product.category = category;
    http.respond({
      results: [
        { id: "o0", value: { id: 7, name: "Puzzles" } },
        { id: "o1", value: { ...chess(), id: 42, name: "Cube", price: 9.5, categoryId: 7 } },
      ],
    });

    await s.saveChanges();

    const body = http.last.body as { operations: { set: string; values: unknown; refs?: unknown }[] };
    expect(body.operations.map((op) => op.set)).toEqual(["categories", "products"]);
    expect(body.operations[1]).toMatchObject({ values: { name: "Cube", price: 9.5 }, refs: { categoryId: "o0" } });
    expect([product.id, product.categoryId, category.id]).toEqual([42, 7, 7]);
    expect(s.entry(product).state).toBe("unchanged");
  });

  it("sets the foreign key when a navigation points at a tracked DTO", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] }).respond({ items: [{ id: 3, name: "Tools" }] });
    const product = await s.products.first();
    product.category = await s.categories.first();
    http.respond({ results: [{ id: "o0", value: chess() }] });

    await s.saveChanges();

    expect(http.last.body).toMatchObject({ operations: [{ values: { categoryId: 3 } }] });
  });

  it("deletes removed DTOs and forgets removed added ones", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] });
    const product = await s.products.first();
    s.products.remove(product);
    s.categories.remove(s.categories.add({ name: "Temp" }));
    http.respond({ results: [{ id: "o0" }] });

    expect(await s.saveChanges()).toBe(1);
    expect(http.last.body).toEqual({
      v: 1,
      operations: [
        { id: "o0", op: "delete", set: "products", key: { id: 1 }, token: "00000001-0000-0000-0000-000000000000" },
      ],
    });
    expect(s.entry(product).state).toBe("detached");
  });

  it("update sends every writable member; setModified(false) accepts a change", async () => {
    const { shop: s, http } = shop();
    const games = { id: 2, name: "Games" };
    s.categories.update(games);
    http.respond({ results: [{ id: "o0", value: games }] });
    await s.saveChanges();
    expect(http.last.body).toMatchObject({ operations: [{ op: "update", key: { id: 2 }, values: { name: "Games" } }] });

    games.name = "Board games";
    s.entry(games).setModified("name", false);

    expect(s.entry(games).state).toBe("unchanged");
  });

  it("keeps the tracked state when a save is rejected", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] });
    const product = await s.products.first();
    product.price = 1;
    http.problem(412, { status: 412, code: "concurrency_conflict", pointer: "", operationId: "o0" });

    const error = await s.saveChanges().catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(RestConcurrencyError);
    expect(error).toMatchObject({ code: "concurrency_conflict", status: 412, entities: [product] });
    expect(s.entry(product).state).toBe("modified");
  });

  it("rejects collection membership changes and untracked navigation targets", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] });
    const product = await s.products.first();
    product.lines.push({ id: 0, sku: "X", quantity: 1, unitPrice: 1 });
    const added = s.products.add({ name: "Cube", category: { id: 5, name: "Untracked" } });

    expect(() => s.entry(product).state).toThrow(/Set the foreign key/);
    expect(() => s.entry(added).state).toThrow(/Add or attach it first/);
  });

  it("refuses a second instance with the same key", () => {
    const { shop: s } = shop();
    s.categories.attach({ id: 1, name: "A" });

    expect(() => {
      s.categories.attach({ id: 1, name: "B" });
    }).toThrow(/already tracked/);
  });
});
