import { describe, expect, it } from "vitest";
import { createContext, type RestSet, RestHttpError, RestQueryError } from "../src/index.js";
import type { CategoryDto, CategoryProxy } from "./shop.generated.js";
import { shop } from "./stub.js";

describe("query translation", () => {
  it("translates every operator family", async () => {
    const { shop: s, http } = shop();

    await s.products
      .where((p) =>
        p.price
          .add(1)
          .sub(2)
          .mul(3)
          .div(4)
          .mod(5)
          .neg()
          .lt(0)
          .or(p.name.toUpper().trim().endsWith("X"), p.name.contains("o").not(), p.categoryId.in([1, 2]))
          .and(p.createdAt.date().ge("2026-01-01T00:00:00Z"), p.releasedOn.month().le(6)),
      )
      .take(1)
      .toList();

    expect(http.last.body).toMatchObject({
      filter: {
        and: [
          {
            or: [
              {
                lt: [
                  { neg: [{ mod: [{ div: [{ mul: [{ sub: [{ add: [{ member: "price" }, 1] }, 2] }, 3] }, 4] }, 5] }] },
                  0,
                ],
              },
              { endsWith: [{ trim: [{ toUpper: [{ member: "name" }] }] }, "X"] },
              { not: [{ contains: [{ member: "name" }, "o"] }] },
              { in: [{ member: "categoryId" }, [1, 2]] },
            ],
          },
          { ge: [{ date: [{ member: "createdAt" }] }, "2026-01-01T00:00:00Z"] },
          { le: [{ month: [{ member: "releasedOn" }] }, 6] },
        ],
      },
    });
  });

  it("names nested lambda variables like the .NET client", async () => {
    const { shop: s, http } = shop();

    await s.products
      .where((p) => p.lines.any((l) => l.quantity.gt(p.lines.count())))
      .take(1)
      .toList();

    expect(http.last.body).toMatchObject({
      filter: {
        any: {
          source: { member: "lines" },
          var: "v0",
          body: { gt: [{ member: "v0/quantity" }, { count: { source: { member: "lines" } } }] },
        },
      },
    });
  });

  it("uses min, max and avg over collections", async () => {
    const { shop: s, http } = shop();

    await s.products
      .select((p) => ({
        lowest: p.lines.min((l) => l.unitPrice),
        highest: p.lines.max((l) => l.quantity),
        average: p.lines.avg((l) => l.quantity),
      }))
      .take(1)
      .toList();

    expect(http.last.body).toMatchObject({
      select: {
        new: {
          lowest: { min: { source: { member: "lines" }, var: "v0", body: { member: "v0/unitPrice" } } },
          highest: { max: { source: { member: "lines" }, var: "v1", body: { member: "v1/quantity" } } },
          average: { avg: { source: { member: "lines" }, var: "v2", body: { member: "v2/quantity" } } },
        },
      },
    });
  });

  it("treats select(p => p) as the full DTO", async () => {
    const { shop: s, http } = shop();

    await s.products
      .select((p) => p)
      .take(1)
      .toList();

    expect(http.last.body).not.toHaveProperty("select");
  });

  it.each([
    [
      "where after select",
      () =>
        shop()
          .shop.products.select((p) => p.id)
          .where((p) => p.id.gt(1)),
    ],
    ["skip after take", () => shop().shop.products.take(1).skip(1)],
    [
      "orderBy twice",
      () =>
        shop()
          .shop.products.orderBy((p) => p.price)
          .orderBy((p) => p.id),
    ],
    ["thenBy first", () => shop().shop.products.thenBy((p) => p.id)],
    [
      "select twice",
      () =>
        shop()
          .shop.products.select((p) => p.id)
          .select((p) => p.id),
    ],
  ])("rejects %s while building", (_, build) => {
    expect(build).toThrow(/isn't supported at this point/);
  });

  it.each([
    ["toList without take", () => shop().shop.products.toList(), /Add take/],
    [
      "sorting by an unsortable member",
      () =>
        shop()
          .shop.products.orderBy((p) => p.name as never)
          .take(1)
          .toList(),
      /isn't sortable/,
    ],
    [
      "include of a scalar",
      () =>
        shop()
          .shop.products.include((p) => p.name)
          .take(1)
          .toList(),
      /needs a navigation/,
    ],
    [
      "include with select",
      () =>
        shop()
          .shop.products.include((p) => p.category)
          .select((p) => p.id)
          .take(1)
          .toList(),
      /can't be combined/,
    ],
    ["skip on a cursor-only set", () => shop().shop.categories.skip(1).take(1).toList(), /only allows cursor/],
    ["cursor on an offset-only set", () => shop().shop.orderLines.take(1).toCursorPage(), /doesn't allow cursor/],
    ["count after a cursor", () => shop().shop.products.after("c").count(), /Counting after a cursor/],
    [
      "a Date literal",
      () =>
        shop()
          .shop.products.where((p) => p.createdAt.gt(new Date() as never))
          .take(1)
          .toList(),
      /ISO 8601/,
    ],
  ])("rejects %s before any request", async (_, run, message) => {
    await expect(run()).rejects.toThrow(message);
  });

  it("checks filterable members locally", async () => {
    const { StubFetch } = await import("./stub.js");
    const { shopModel } = await import("./shop.generated.js");
    const http = new StubFetch();
    const categories = shopModel.sets.categories;
    const model = {
      sets: {
        categories: {
          ...categories,
          members: categories.members.map((m) => (m.name === "name" ? { ...m, filterable: false } : m)),
        },
      },
    };
    const context = createContext(model, { baseUrl: "https://shop.test/api/", fetch: http.fetch });
    const set = context.categories as RestSet<CategoryDto, CategoryProxy>;

    await expect(
      set
        .where((c) => c.name.eq("x"))
        .take(1)
        .toList(),
    ).rejects.toThrow(/can't be filtered/);
    await set
      .select((c) => c.name)
      .take(1)
      .toList();

    expect(http.requests).toHaveLength(1);
  });
});

describe("paging and terminals", () => {
  it("pages with cursors and streams every row", async () => {
    const { shop: s, http } = shop();
    http
      .respond({
        items: [
          { id: 1, name: "A" },
          { id: 2, name: "B" },
        ],
        nextCursor: "c1",
        totalCount: 3,
      })
      .respond({ items: [{ id: 3, name: "C" }] });

    const page = await s.categories.orderBy((c) => c.id).toCursorPage({ includeTotalCount: true });
    const rest: string[] = [];
    for await (const category of s.categories
      .orderBy((c) => c.id)
      .after(page.nextCursor ?? "")
      .stream()) {
      rest.push(category.name);
    }

    expect(page).toMatchObject({ nextCursor: "c1", totalCount: 3 });
    expect(page.items.map((c) => c.name)).toEqual(["A", "B"]);
    expect(rest).toEqual(["C"]);
    expect(http.requests[0]?.body).toMatchObject({ page: { mode: "cursor", take: 2 }, count: true });
    expect(http.last.body).toMatchObject({ page: { mode: "cursor", after: "c1", take: 2 } });
  });

  it("asks only for what first, single, count and any need", async () => {
    const { shop: s, http } = shop();
    http
      .respond({ items: [{ id: 1, name: "A" }] })
      .respond({
        items: [
          { id: 1, name: "A" },
          { id: 2, name: "B" },
        ],
      })
      .respond({ items: [], totalCount: 10 })
      .respond({ items: [{}] });

    expect((await s.categories.first()).name).toBe("A");
    expect(http.last.body).toMatchObject({ page: { take: 1 } });
    await expect(s.categories.single()).rejects.toThrow(/more than one/);
    expect(http.last.body).toMatchObject({ page: { take: 2 } });
    expect(await s.products.skip(8).take(5).count()).toBe(2);
    expect(http.last.body).toEqual({ v: 1, select: { new: {} }, page: { mode: "offset", take: 0 }, count: true });
    expect(await s.products.any((p) => p.id.gt(0))).toBe(true);
    expect(await s.products.firstOrDefault()).toBeUndefined();
  });

  it("sends HTTP QUERY by default and POST $query when asked", async () => {
    const query = shop();
    const post = shop({ transport: "post" });

    await query.shop.products.take(1).toList();
    await post.shop.products.take(1).toList();

    expect([query.http.last.method, query.http.last.path]).toEqual(["QUERY", "/api/products"]);
    expect([post.http.last.method, post.http.last.path]).toEqual(["POST", "/api/products/$query"]);
    expect(query.http.last.headers.get("content-type")).toBe("application/json");
  });

  it("adds per-request headers", async () => {
    const { StubFetch } = await import("./stub.js");
    const { createShop } = await import("./shop.generated.js");
    const http = new StubFetch();
    let calls = 0;
    const s = createShop({
      baseUrl: "https://shop.test/api/",
      fetch: http.fetch,
      headers: () => ({ authorization: `Bearer ${String(++calls)}` }),
    });

    await s.products.take(1).toList();
    await s.products.take(1).toList();

    expect(http.last.headers.get("authorization")).toBe("Bearer 2");
  });

  it("maps problem responses to typed errors", async () => {
    const { shop: s, http } = shop();
    http
      .problem(400, { status: 400, code: "limit_exceeded", pointer: "/page/take", limit: "pageSize" })
      .respond("oops", 500);

    const rejected = await s.products
      .take(500)
      .toList()
      .catch((error: unknown) => error);
    const failed = await s.products
      .take(1)
      .toList()
      .catch((error: unknown) => error);

    expect(rejected).toBeInstanceOf(RestQueryError);
    expect(rejected).toMatchObject({ status: 400, code: "limit_exceeded", location: "/page/take", limit: "pageSize" });
    expect(failed).toBeInstanceOf(RestHttpError);
    expect(failed).toMatchObject({ status: 500 });
  });

  it("fills navigations that weren't included", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [{ id: 1, name: "Chess" }] });

    const product = await s.products.first();

    expect(product.category).toBeNull();
    expect(product.lines).toEqual([]);
  });
});
