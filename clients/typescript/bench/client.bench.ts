import { bench, describe } from "vitest";
import { entityProxy } from "../src/expressions.js";
import { findSet } from "../src/model.js";
import { createShop, shopModel, type ProductProxy, type ShopContext } from "../test/shop.generated.js";

/** A fetch answering every request with the same JSON body, so only client CPU is measured. */
function cannedShop(body: unknown): ShopContext {
  const text = JSON.stringify(body);
  return createShop({
    baseUrl: "https://shop.test/api/",
    fetch: () => Promise.resolve(new Response(text, { headers: { "content-type": "application/json" } })),
  });
}

const shop = cannedShop({ items: [] });
const minimum = 10;
const ids = Array.from({ length: 300 }, (_, index) => index + 1);

describe("translate (query → request body)", () => {
  bench("small", () => {
    JSON.stringify(
      shop.products
        .where((p) => p.price.gt(minimum))
        .take(20)
        .toJSON(),
    );
  });

  bench("medium", () => {
    JSON.stringify(
      shop.products
        .where((p) =>
          p.tags
            .any((t) => t.eq("sale"))
            .and(p.lines.sum((l) => l.quantity.mul(l.unitPrice)).gt(minimum), p.category.name.ne("Tools")),
        )
        .orderByDescending((p) => p.price)
        .select((p) => ({
          id: p.id,
          label: p.name.concat(" (", p.categoryName, ")"),
          total: p.lines.sum((l) => l.quantity.mul(l.unitPrice)),
          skus: p.lines.where((l) => l.quantity.gt(1)).select((l) => l.sku),
        }))
        .take(20)
        .toJSON(),
    );
  });

  bench("large", () => {
    JSON.stringify(
      shop.products
        .where((p) =>
          p.id.in(ids).and(
            p.name.contains("o").or(p.createdAt.year().eq(2026)),
            p.lines.any((l) => l.quantity.gt(0)),
          ),
        )
        .include((p) => p.category)
        .include((p) => p.lines)
        .orderBy((p) => p.price)
        .take(20)
        .toJSON(),
    );
  });
});

describe("proxy (what one lambda run builds)", () => {
  const products = findSet(shopModel, "products");

  bench("create root", () => {
    entityProxy(shopModel, products, null);
  });

  bench("create + access members and navigations", () => {
    const p = entityProxy(shopModel, products, null) as unknown as ProductProxy;
    p.price.gt(minimum).and(
      p.category.name.ne("Tools"),
      p.lines.any((l) => l.quantity.gt(0)),
    );
  });
});

const row = (id: number) => ({
  id,
  name: `Product ${String(id)}`,
  description: null,
  price: (id % 90) + 0.5,
  status: "Active",
  version: `00000000-0000-0000-0000-${String(id).padStart(12, "0")}`,
  createdAt: "2026-01-10T09:30:00Z",
  releasedOn: "2026-02-01",
  categoryId: 2,
  tags: ["sale", "new"],
  categoryName: "Games",
});

describe("materialize (response → tracked DTOs)", () => {
  for (const rows of [1, 50, 200]) {
    const body = { items: Array.from({ length: rows }, (_, index) => row(index + 1)) };
    bench(`${String(rows)} rows`, async () => {
      await cannedShop(body).products.take(rows).toList();
    });
  }
});

describe("saveChanges (detect changes + build batch, 10% modified)", () => {
  for (const tracked of [50, 500]) {
    // An empty result keeps the entries modified, so every run builds the same batch.
    const context = cannedShop({ results: [] });
    for (let id = 1; id <= tracked; id++) {
      const product = { ...row(id), category: null, lines: [] } as Parameters<typeof context.products.attach>[0];
      context.products.attach(product);
      if (id % 10 === 0) {
        product.price += 1;
      }
    }

    bench(`${String(tracked)} tracked`, async () => {
      await context.saveChanges();
    });
  }
});
