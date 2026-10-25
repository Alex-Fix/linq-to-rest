import { describe, expect, it } from "vitest";
import { RestValidationError } from "../src/index.js";
import { shop } from "./stub.js";

const chess = {
  id: 1,
  name: "Chess",
  price: 20,
  status: "Active",
  version: "00000001-0000-0000-0000-000000000000",
  createdAt: "2026-01-10T09:30:00Z",
  categoryId: 2,
  size: { width: 40, weight: { amount: 1.2, unit: "kg" } },
  tags: [],
};

describe("value types", () => {
  it("reads values as plain objects", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess] });

    const [product] = await s.products.take(1).toList();

    expect(product?.size).toEqual({ width: 40, weight: { amount: 1.2, unit: "kg" } });
  });

  it("sees a value replaced or changed in place, but not an equal copy", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [chess] });
    const [product] = await s.products.take(1).toList();
    if (product?.size == null) {
      throw new Error("The stub returns a product with a size.");
    }

    product.size = { ...product.size, weight: { ...product.size.weight } };
    expect(s.entry(product).state).toBe("unchanged");
    (product.size.weight as { amount: number }).amount = 1.3;
    expect(s.entry(product).state).toBe("modified");
    // Written whole and in model order, however the object was built.
    product.size = { weight: { unit: "g", amount: 2 }, width: 41 };
    http.respond({ results: [] });
    await s.saveChanges();

    expect(JSON.stringify((http.last.body as { operations: { values: unknown }[] }).operations[0]?.values)).toBe(
      '{"size":{"width":41,"weight":{"amount":2,"unit":"g"}}}',
    );
  });

  it("checks value members' rules locally, by dotted path", async () => {
    const { shop: s, http } = shop();
    const product = s.products.add({
      name: "Cube",
      price: 1,
      status: "Active",
      size: { width: -1, weight: { amount: 1, unit: "grams" + "!" } },
    });

    const error = await s.saveChanges().catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(RestValidationError);
    const validation = error as RestValidationError;
    expect(validation.isLocal).toBe(true);
    expect(validation.errorsFor(product)).toEqual({ "size.width": ["out_of_range"], "size.weight.unit": ["too_long"] });
    expect(http.requests).toHaveLength(0);
  });

  it("maps the server's errors on value members to dotted paths", async () => {
    const { shop: s, http } = shop();
    const product = s.products.add({ name: "Cube", price: 1, status: "Active", size: null });
    http.problem(422, {
      type: "urn:linqtorest:error:validation_failed",
      title: "Validation failed.",
      status: 422,
      code: "validation_failed",
      pointer: "",
      errors: [
        {
          operation: "o0",
          pointer: "/operations/0/values/size/weight/amount",
          member: "size/weight/amount",
          code: "out_of_range",
        },
      ],
    });

    const error = (await s.saveChanges().catch((caught: unknown) => caught)) as RestValidationError;

    expect(error.errorsFor(product)).toEqual({ "size.weight.amount": ["out_of_range"] });
  });

  it("refuses whole values outside selects and null tests before sending", async () => {
    const { shop: s, http } = shop();

    await expect(
      s.products
        .orderBy((p) => p.size as never)
        .take(1)
        .toList(),
    ).rejects.toThrow("is a value such as an address");
    await expect(
      s.products
        .groupBy((p) => p.size as never)
        .select((g) => ({ n: g.count() }))
        .toList(),
    ).rejects.toThrow("is a value such as an address");
    await expect(
      s.products
        .where((p) => p.size.eq({} as never))
        .take(1)
        .toList(),
    ).rejects.toThrow("can only be compared with null");
    expect(http.requests).toHaveLength(0);
  });
});
