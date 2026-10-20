import { describe, expect, inject, it } from "vitest";
import { RestConcurrencyError, RestQueryError, RestSaveError } from "../src/index.js";
import { createCatalog } from "./catalog.generated.js";

const apiRoot = inject("apiRoot");

async function tokenFor(role: "viewer" | "editor"): Promise<string> {
  const response = await fetch(`${apiRoot}auth/dev-login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ role }),
  });
  return ((await response.json()) as { token: string }).token;
}

const editorToken = await tokenFor("editor");

const client = (options: { readonly transport?: "query" | "post"; readonly editor?: boolean } = {}) =>
  createCatalog({
    baseUrl: `${apiRoot}api/`,
    headers: options.editor === true ? { Authorization: `Bearer ${editorToken}` } : {},
    ...(options.transport === undefined ? {} : { transport: options.transport }),
  });

describe("TypeScript client against samples/Catalog.Api", () => {
  it("filters, sorts and projects on the server", async () => {
    const rows = await client()
      .products.where((p) =>
        p.price.ge(50).and(p.tags.any((t) => t.eq("classic")).or(p.category.parent.name.eq("Board games"))),
      )
      .orderByDescending((p) => p.price)
      .select((p) => ({
        name: p.name,
        price: p.price,
        tags: p.tags,
        path: p.category.parent.name.coalesce("").concat(" / ", p.category.name),
        firstImage: p.images.where((i) => i.sortOrder.eq(0)).select((i) => i.url),
      }))
      .take(20)
      .toList();

    expect(rows.length).toBeGreaterThan(0);
    for (const row of rows) {
      expect(row.price).toBeGreaterThanOrEqual(50);
      expect(row.tags.includes("classic") || row.path.startsWith("Board games / ")).toBe(true);
      expect(row.firstImage.length).toBeLessThanOrEqual(1);
    }

    expect(rows.map((row) => row.price)).toEqual([...rows.map((row) => row.price)].sort((left, right) => right - left));
  });

  it("sends the same query over POST $query", async () => {
    const query = (transport?: "query" | "post") =>
      client(transport === undefined ? {} : { transport })
        .products.orderBy((p) => p.name)
        .select((p) => p.name)
        .take(5)
        .toList();

    expect(await query("post")).toEqual(await query());
  });

  it("pages with cursors and streams every visible row", async () => {
    const catalog = client();
    const seen: string[] = [];
    let page = await catalog.products
      .orderBy((p) => p.name)
      .take(50)
      .toCursorPage({ includeTotalCount: true });
    const total = page.totalCount;
    seen.push(...page.items.map((p) => p.sku));
    while (page.nextCursor !== undefined) {
      page = await catalog.products
        .orderBy((p) => p.name)
        .after(page.nextCursor)
        .take(50)
        .toCursorPage();
      seen.push(...page.items.map((p) => p.sku));
    }

    const streamed: string[] = [];
    for await (const product of catalog.products.orderBy((p) => p.name).stream()) {
      streamed.push(product.sku);
      expect(product.status).toBe("Active");
    }

    expect(seen.length).toBe(total);
    expect(streamed).toEqual(seen);
  });

  it("hides drafts from everyone but editors", async () => {
    const visitors = await client().products.count();
    const editors = await client({ editor: true }).products.count();
    const drafts = await client({ editor: true }).products.count((p) => p.status.ne("Active"));

    expect(editors - visitors).toBe(drafts);
    expect(drafts).toBeGreaterThan(0);
  });

  it("saves a unit of work with refs and reads it back", async () => {
    const catalog = client({ editor: true });
    const existing = await catalog.products.include((p) => p.category).first((p) => p.stock.gt(0));
    const oldVersion = existing.version;
    existing.price = existing.price + 1;
    const kites = catalog.categories.add({ name: "Kites", parentId: existing.categoryId });
    const kite = catalog.products.add({
      sku: "SKU-E2E-1",
      name: "Delta kite",
      price: 24.5,
      status: "Active",
      stock: 4,
      brandId: 1,
      category: kites,
    });
    catalog.productImages.add({ url: "https://example.test/kite.png", sortOrder: 0, product: kite });

    expect(await catalog.saveChanges()).toBe(4);

    expect(existing.version).not.toBe(oldVersion);
    expect(kite.categoryId).toBe(kites.id);
    expect(kite.imageCount).toBe(1);
    const fresh = await client()
      .products.include((p) => p.category)
      .first((p) => p.id.eq(kite.id));
    expect([fresh.name, fresh.category?.name, fresh.createdAt.endsWith("Z"), fresh.inStock]).toEqual([
      "Delta kite",
      "Kites",
      true,
      true,
    ]);
  });

  it("reports concurrent edits", async () => {
    const mine = client({ editor: true });
    const theirs = client({ editor: true });
    const product = await mine.products.first((p) => p.sku.eq("SKU-0005"));
    const other = await theirs.products.first((p) => p.sku.eq("SKU-0005"));
    other.price = other.price + 2;
    await theirs.saveChanges();
    product.price = product.price + 3;

    const error = await mine.saveChanges().catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(RestConcurrencyError);
    expect((error as RestSaveError).entities).toEqual([product]);
  });

  it("surfaces server rejections as typed errors", async () => {
    const tooMany = await client()
      .products.take(97)
      .toList()
      .catch((caught: unknown) => caught);
    const anonymous = client();
    anonymous.brands.add({ name: "Nope" });
    const forbidden = await anonymous.saveChanges().catch((caught: unknown) => caught);
    const unsortable = await client()
      .products.orderBy((p) => p.sku as never)
      .take(1)
      .toList()
      .catch((caught: unknown) => caught);

    expect(tooMany).toBeInstanceOf(RestQueryError);
    expect(tooMany).toMatchObject({ status: 400, code: "limit_exceeded", limit: "pageSize" });
    expect(forbidden).toBeInstanceOf(RestSaveError);
    expect(forbidden).toMatchObject({ status: 403, code: "forbidden" });
    expect(unsortable).toBeInstanceOf(Error);
  });
});
