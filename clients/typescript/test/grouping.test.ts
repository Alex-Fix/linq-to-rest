import { describe, expect, it } from "vitest";
import { shop } from "./stub.js";

describe("grouped queries", () => {
  it("read groups through the group select and send every group without take", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [{ status: "Active", count: 3, from: 12.5 }] });

    const groups = await s.products
      .groupBy((p) => p.status)
      .select((g) => ({ status: g.key, count: g.count(), from: g.min((p) => p.price) }))
      .toList();

    expect(groups).toEqual([{ status: "Active", count: 3, from: 12.5 }]);
    expect(http.last.body).toMatchObject({ groupBy: ["status"], page: { mode: "offset" } });
    expect((http.last.body as { page: object }).page).not.toHaveProperty("take");
  });

  it("count groups with the select that having names", async () => {
    const { shop: s, http } = shop();
    http.respond({ items: [], totalCount: 2 });

    const count = await s.products
      .groupBy((p) => p.categoryId)
      .select((g) => ({ count: g.count() }))
      .where((f) => f.count.gt(1))
      .count();

    expect(count).toBe(2);
    expect(http.last.body).toMatchObject({
      select: { new: { count: { count: { source: { group: {} } } } } },
      having: { gt: [{ member: "count" }, 1] },
      page: { mode: "offset", take: 0 },
      count: true,
    });
  });

  it("aggregate whole queries with empty-set results", async () => {
    const { shop: s, http } = shop();
    http
      .respond({ items: [41.5] })
      .respond({ items: [] })
      .respond({ items: [] });

    const total = await s.products.sum((p) => p.price);
    const none = await s.products.where((p) => p.price.lt(0)).sum((p) => p.price);
    const noMax = await s.products.max((p) => p.price);

    expect([total, none, noMax]).toEqual([41.5, 0, null]);
    expect(http.requests[0]?.body).toEqual({
      v: 1,
      groupBy: [],
      select: { sum: { source: { group: {} }, var: "v0", body: { member: "v0/price" } } },
      page: { mode: "offset" },
    });
  });

  it.each([
    [
      "non-groupable key",
      (s: ReturnType<typeof shop>["shop"]) =>
        s.products
          .groupBy((p) => p.name as never)
          .select((g) => ({ k: g.key }))
          .toList(),
      "isn't groupable",
    ],
    [
      "non-aggregatable sum",
      (s: ReturnType<typeof shop>["shop"]) =>
        s.products
          .groupBy((p) => p.status)
          .select((g) => ({ s: g.sum((p) => p.id) }))
          .toList(),
      "isn't aggregatable",
    ],
    [
      "nested collection in an aggregate",
      (s: ReturnType<typeof shop>["shop"]) =>
        s.products
          .groupBy((p) => p.status)
          .select((g) => ({ s: g.sum((p) => p.lines.sum((l) => l.quantity)) }))
          .toList(),
      "other collection lambdas",
    ],
    [
      "any over a group",
      (s: ReturnType<typeof shop>["shop"]) =>
        s.products
          .groupBy((p) => p.status)
          .select((g) => ({ a: g.any() }))
          .toList(),
      "count, sum, min, max or average",
    ],
    [
      "cursor page of groups",
      (s: ReturnType<typeof shop>["shop"]) =>
        s.products
          .groupBy((p) => p.status)
          .select((g) => ({ k: g.key }))
          .toCursorPage(),
      "skip and take",
    ],
    ["sum after take", (s: ReturnType<typeof shop>["shop"]) => s.products.take(5).sum((p) => p.price), "where only"],
  ] as const)("refuse %s locally", async (_, run, message) => {
    const { shop: s, http } = shop();

    await expect(Promise.resolve().then((): Promise<unknown> => run(s))).rejects.toThrow(message);
    expect(http.requests).toHaveLength(0);
  });
});
