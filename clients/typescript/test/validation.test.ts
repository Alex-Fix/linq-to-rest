import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  checkRules,
  createContext,
  type Model,
  type RestSet,
  RestSaveError,
  RestValidationError,
} from "../src/index.js";
import type { CategoryDto, CategoryProxy, ProductDto, ProductProxy } from "./shop.generated.js";
import { shopModel } from "./shop.generated.js";
import { StubFetch } from "./stub.js";

// The golden shop with built-in rules on products, as the server would export them.
const rules = {
  name: { required: true, maxLength: 5 },
  price: { min: 0.01, max: 100 },
  releasedOn: { min: "2000-01-01" },
  categoryId: { required: true },
} as const;
const model = {
  sets: {
    ...shopModel.sets,
    products: {
      ...shopModel.sets.products,
      members: shopModel.sets.products.members.map((member) =>
        member.name in rules ? { ...member, rules: rules[member.name as keyof typeof rules] } : member,
      ),
    },
  },
} satisfies Model;

function create() {
  const http = new StubFetch();
  const context = createContext(model, { baseUrl: "https://shop.test/api/", fetch: http.fetch });
  return {
    context,
    http,
    products: context.products as RestSet<ProductDto, ProductProxy, "insert" | "update" | "delete">,
    categories: context.categories as RestSet<CategoryDto, CategoryProxy, "insert" | "update" | "delete">,
  };
}

const saved = (id: string) => ({
  id,
  value: { id: 7, name: "Cube", price: 9.5, status: "Active", version: "00000007-0000-0000-0000-000000000000" },
});

describe("rules", () => {
  it.each([
    [{ required: true }, "string", null, "required"],
    [{ required: true }, "string", " \t", "required"],
    [{ required: true }, "string", [], "required"],
    [{ required: true }, "number", 0, undefined],
    [{ minLength: 2, maxLength: 4 }, "string", "a", "too_short"],
    [{ minLength: 2, maxLength: 4 }, "string", "abcd", undefined],
    [{ minLength: 2, maxLength: 4 }, "string", "abcde", "too_long"],
    [{ maxLength: 2 }, "string", ["a", "b", "c"], "too_long"],
    [{ min: 0.01, max: 100 }, "number", 0, "out_of_range"],
    [{ min: 0.01, max: 100 }, "number", 100, undefined],
    [{ min: "2026-01-01" }, "dateonly", "2025-12-31", "out_of_range"],
    [{ max: "2026-01-01T00:00:00Z" }, "datetime", "2026-01-01T01:00:00+02:00", undefined],
    [{ max: "1.00:00:00" }, "timespan", "23:59:59", undefined],
    [{ max: "1.00:00:00" }, "timespan", "1.00:00:01", "out_of_range"],
    [{ min: "08:00:00" }, "timeonly", "07:59:59.9990000", "out_of_range"],
    [{ pattern: "^[A-Z]+$" }, "string", "AB\n", "invalid_format"],
    [{ required: true, maxLength: 3, pattern: "^[a-z]+$" }, "string", "ABCD", "too_long"],
  ] as const)("%o on a %s: %o is %s", (memberRules, kind, value, expected) => {
    expect(checkRules(memberRules, kind, value)).toBe(expected);
  });
});

describe("validation on save", () => {
  it("checks the built-in rules before sending", async () => {
    const { context, http, products } = create();
    const product = products.add({ name: "Too long", price: 0, status: "Active" });

    const error = (await context.saveChanges().catch((caught: unknown) => caught)) as RestValidationError;

    expect(error).toBeInstanceOf(RestValidationError);
    expect(error.isLocal).toBe(true);
    expect([error.status, error.code]).toEqual([422, "validation_failed"]);
    expect(error.errors).toEqual([
      { entity: product, member: "name", code: "too_long" },
      { entity: product, member: "price", code: "out_of_range" },
      { entity: product, member: "categoryId", code: "required" },
    ]);
    expect(error.entities).toEqual([product]);
    expect(http.requests).toHaveLength(0);
    expect(context.entry(product).state).toBe("added");
  });

  it("checks only the members an update sends", async () => {
    const { context, http, products } = create();
    const product = {
      id: 1,
      name: "Too long already",
      price: 20,
      status: "Active",
      version: "00000001-0000-0000-0000-000000000000",
      createdAt: "2026-01-10T09:30:00Z",
      releasedOn: null,
      categoryId: 2,
      description: null,
      tags: [],
      categoryName: null,
    } as unknown as ProductDto;
    products.attach(product);
    http.respond({ results: [{ id: "o0" }] });

    product.price = 21;
    await context.saveChanges();
    product.price = 101;
    const error = (await context.saveChanges().catch((caught: unknown) => caught)) as RestValidationError;

    expect(http.requests).toHaveLength(1);
    expect(error.errors.map((issue) => [issue.member, issue.code])).toEqual([["price", "out_of_range"]]);
  });

  it("doesn't check a foreign key set through a new row", async () => {
    const { context, http, products, categories } = create();
    const category = categories.add({ name: "Puzzles" });
    products.add({ name: "Cube", price: 9.5, status: "Active", category });
    http.respond({ results: [{ id: "o0", value: { id: 3, name: "Puzzles" } }, saved("o1")] });

    await expect(context.saveChanges()).resolves.toBe(2);
  });

  it("maps the server's validation problem to entities and members", async () => {
    const { context, http, products } = create();
    const first = products.add({ name: "One", price: 1, status: "Active", categoryId: 1 });
    const second = products.add({ name: "Two", price: 1, status: "Active", categoryId: 1 });
    // The response contract shared with the .NET client's tests.
    http.problem(
      422,
      JSON.parse(
        readFileSync(new URL("../../../tests/golden/response-validation-failed.json", import.meta.url), "utf8"),
      ) as Record<string, unknown>,
    );

    const error = (await context.saveChanges().catch((caught: unknown) => caught)) as RestValidationError;

    expect(error.isLocal).toBe(false);
    expect(error.isTruncated).toBe(true);
    expect(error.errors).toEqual([
      { entity: first, member: "name", code: "duplicate" },
      { entity: second, member: undefined, code: "too_many_products" },
    ]);
    expect(error.errorsFor(first)).toEqual({ name: ["duplicate"] });
    expect(error.errorsFor(second)).toEqual({ "": ["too_many_products"] });
  });

  it("names the member of other save errors", async () => {
    const { context, http, products } = create();
    products.add({ name: "Cube", price: 9.5, status: "Active", categoryId: 3 });
    http.problem(404, {
      code: "not_found",
      pointer: "/operations/0/values/categoryId",
      operationId: "o0",
    });

    const error = (await context.saveChanges().catch((caught: unknown) => caught)) as RestSaveError;

    expect([error.code, error.member]).toEqual(["not_found", "categoryId"]);
  });
});

describe("forms", () => {
  it("validate an entity and read the rules", () => {
    const { products } = create();

    expect(products.validate({ name: "Cube", price: 500 }).map((issue) => [issue.member, issue.code])).toEqual([
      ["price", "out_of_range"],
      ["categoryId", "required"],
    ]);
    expect(products.rules.name).toEqual({ required: true, maxLength: 5 });
    expect(products.rules.status).toBeUndefined();
  });
});
