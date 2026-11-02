import { describe, expect, it } from "vitest";
import { createContext, RestSaveError } from "../src/index.js";
import { type ShopContext, shopModel } from "./shop.generated.js";
import { shop, StubFetch } from "./stub.js";

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

const line = { id: 11, sku: "C1", quantity: 2, unitPrice: 20 };

describe("collection links", () => {
  it("sends links after updates and before deletes", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] });
    const product = await s.products.first();
    const eco = { id: 3, name: "Eco" };
    const gone = { id: 41, sku: "H1", quantity: 0, unitPrice: 15 };
    s.labels.attach(eco);
    s.orderLines.attach(gone);
    product.price = 21;
    product.labels.push(eco);
    s.orderLines.remove(gone);
    http.respond({
      results: [
        { id: "o0", value: { id: 1, version: "00000002-0000-0000-0000-000000000000" } },
        { id: "o1" },
        { id: "o2" },
      ],
    });

    expect(await s.saveChanges()).toBe(3);

    expect((http.last.body as { operations: unknown[] }).operations).toEqual([
      {
        id: "o0",
        op: "update",
        set: "products",
        key: { id: 1 },
        values: { price: 21 },
        token: "00000001-0000-0000-0000-000000000000",
      },
      { id: "o1", op: "link", set: "products", key: { id: 1 }, navigation: "labels", item: { key: { id: 3 } } },
      { id: "o2", op: "delete", set: "orderLines", key: { id: 41 } },
    ]);
    expect(s.entry(product).state).toBe("unchanged");
    expect(await s.saveChanges()).toBe(0);
  });

  it("links new parents and items by ref, and the item points back", async () => {
    const { shop: s, http } = shop();
    const puzzles = s.categories.add({ name: "Puzzles" });
    const cube = s.products.add({ name: "Cube", price: 9.5 });
    puzzles.products.push(cube);
    http.respond({
      results: [{ id: "o0", value: { id: 7, name: "Puzzles" } }, { id: "o1", value: { id: 42 } }, { id: "o2" }],
    });

    expect(await s.saveChanges()).toBe(3);

    expect((http.last.body as { operations: unknown[] }).operations[2]).toEqual({
      id: "o2",
      op: "link",
      set: "categories",
      ref: "o0",
      navigation: "products",
      item: { ref: "o1" },
    });
    expect([cube.categoryId, cube.category]).toEqual([7, puzzles]);
    expect(s.entry(cube).state).toBe("unchanged");
    expect(await s.saveChanges()).toBe(0);
  });

  it("detaches a required item after unlinking it", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [{ ...chess(), lines: [line] }] });
    const product = await s.products.include((p) => p.lines).first();
    const removed = product.lines.splice(0, 1)[0];
    http.respond({ results: [{ id: "o0" }] });

    await s.saveChanges();

    expect((http.last.body as { operations: unknown[] }).operations[0]).toEqual({
      id: "o0",
      op: "unlink",
      set: "products",
      key: { id: 1 },
      navigation: "lines",
      item: { key: { id: 11 } },
    });
    expect(removed === undefined ? undefined : s.entry(removed).state).toBe("detached");
  });

  it("sends an optional item's token and clears its foreign key", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [{ id: 2, name: "Games", products: [chess()] }] });
    const games = await s.categories.include((c) => c.products).first();
    const product = games.products[0];
    if (product === undefined) {
      throw new Error("The stub returns a product.");
    }

    product.category = games;
    games.products.splice(0, 1);
    http.respond({ results: [{ id: "o0" }] });

    await s.saveChanges();

    expect((http.last.body as { operations: { item: unknown }[] }).operations[0]?.item).toEqual({
      key: { id: 1 },
      token: "00000001-0000-0000-0000-000000000000",
    });
    expect([product.categoryId, product.category]).toEqual([null, null]);
  });

  it("lets a delete cover removing the item", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [{ ...chess(), lines: [line] }] });
    const product = await s.products.include((p) => p.lines).first();
    const removed = product.lines.splice(0, 1)[0];
    if (removed !== undefined) {
      s.orderLines.remove(removed);
    }
    http.respond({ results: [{ id: "o0" }] });

    expect(await s.saveChanges()).toBe(1);
    expect((http.last.body as { operations: { op: string }[] }).operations.map((operation) => operation.op)).toEqual([
      "delete",
    ]);
  });

  it("refuses changes to collections without links", async () => {
    const http = new StubFetch();
    const model = {
      ...shopModel,
      sets: {
        ...shopModel.sets,
        products: {
          ...shopModel.sets.products,
          navigations: shopModel.sets.products.navigations.map((navigation) =>
            navigation.name === "labels" ? { ...navigation, links: null } : navigation,
          ),
        },
      },
    };
    const s = createContext(model, { baseUrl: "https://shop.test/api/", fetch: http.fetch }) as unknown as ShopContext;
    http.respond({ items: [chess()] });
    const product = await s.products.first();
    const eco = { id: 3, name: "Eco" };
    s.labels.attach(eco);
    product.labels.push(eco);

    await expect(s.saveChanges()).rejects.toThrow(/which doesn't allow links/);
  });

  it("reports a failing link on its parent", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess()] });
    const product = await s.products.first();
    const eco = { id: 3, name: "Eco" };
    s.labels.attach(eco);
    product.labels.push(eco);
    http.problem(404, { status: 404, code: "not_found", pointer: "/operations/0/item/key", operationId: "o0" });

    const error = await s.saveChanges().catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(RestSaveError);
    expect((error as RestSaveError).entities).toEqual([product]);
    expect(product.labels).toContain(eco);
  });

  it("copies composite foreign keys or sends them as refs", async () => {
    const { shop: s, http } = shop();
    const fresh = s.bins.add({ code: "A9" });
    const known = {
      zone: "0a000000-0000-0000-0000-000000000001",
      slot: "0b000000-0000-0000-0000-000000000001",
      code: "A1",
    };
    s.bins.attach(known);
    s.products.add({ name: "Cube", bin: fresh });
    s.products.add({ name: "Crate", bin: known });
    http.respond({ results: [] });

    await s.saveChanges();

    const operations = (http.last.body as { operations: { set: string; values: { name?: string }; refs?: unknown }[] })
      .operations;
    const cube = operations.find((operation) => operation.values.name === "Cube");
    const crate = operations.find((operation) => operation.values.name === "Crate");
    expect(cube?.refs).toEqual({ binZone: "o0", binSlot: "o0" });
    expect(crate?.values).toMatchObject({ binZone: known.zone, binSlot: known.slot });
  });
});
