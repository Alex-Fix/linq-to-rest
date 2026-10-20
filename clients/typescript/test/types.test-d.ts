import { describe, expectTypeOf, it } from "vitest";
import { cond, type RestSet } from "../src/index.js";
import type { CategoryDto, CategoryProxy, ProductDto } from "./shop.generated.js";
import { shop } from "./stub.js";

const { shop: s } = shop();

describe("types", () => {
  it("infers projection results", () => {
    const query = s.products.select((p) => ({
      id: p.id,
      label: p.name.concat("!"),
      total: p.lines.sum((l) => l.quantity),
      skus: p.lines.select((l) => l.sku),
      category: p.category,
      lines: p.lines,
      tags: p.tags,
      expensive: cond(p.price.gt(18), "yes", "no"),
      nested: { released: p.releasedOn },
    }));

    expectTypeOf(query.toList()).resolves.toEqualTypeOf<
      {
        id: number;
        label: string;
        total: number;
        skus: string[];
        category: CategoryDto | null;
        lines: ProductDto["lines"];
        tags: string[];
        expensive: string;
        nested: { released: string | null };
      }[]
    >();
    expectTypeOf(s.products.take(1).toList()).resolves.toEqualTypeOf<ProductDto[]>();
  });

  it("rejects misuse at compile time", () => {
    // @ts-expect-error — comparing a number with a string.
    s.products.where((p) => p.price.gt("10"));
    // @ts-expect-error — booleans have no order.
    // eslint-disable-next-line @typescript-eslint/no-unsafe-call, @typescript-eslint/no-unsafe-return -- the invalid call is the point.
    s.products.where((p) => p.status.eq("Active").gt(true));
    // @ts-expect-error — name isn't sortable.
    s.products.orderBy((p) => p.name);
    // @ts-expect-error — enum values are a closed union.
    s.products.where((p) => p.status.eq("Gone"));
    // @ts-expect-error — keys are readonly.
    s.products.add({ id: 5 });
    const product = {} as ProductDto;
    // @ts-expect-error — computed members are readonly.
    product.categoryName = "x";
    // @ts-expect-error — null only for nullable members.
    s.products.where((p) => p.name.eq(null));
  });

  it("offers only the writes a set allows", () => {
    type ReadOnly = RestSet<CategoryDto, CategoryProxy>;
    type InsertOnly = RestSet<CategoryDto, CategoryProxy, "insert">;

    expectTypeOf<ReadOnly>().toHaveProperty("where");
    expectTypeOf<ReadOnly>().toHaveProperty("attach");
    expectTypeOf<ReadOnly>().not.toHaveProperty("add");
    expectTypeOf<ReadOnly>().not.toHaveProperty("update");
    expectTypeOf<ReadOnly>().not.toHaveProperty("remove");
    expectTypeOf<InsertOnly>().toHaveProperty("add");
    expectTypeOf<InsertOnly>().not.toHaveProperty("remove");
    expectTypeOf(s.products).toHaveProperty("remove");
  });
});
