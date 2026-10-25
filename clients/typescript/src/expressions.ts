import type { MemberModel, Model, SetModel } from "./model.js";
import { findSet } from "./model.js";
import { literal, type QueryNode } from "./nodes.js";
import type { Translator } from "./translator.js";

/** Builds a node once a request is translated. Lambdas only run then, so variables get the .NET client's names. */
export type Build = (t: Translator) => QueryNode;

/** @internal */
export const BUILD: unique symbol = Symbol("linqtorest.build");

/** Anything that turns into a query node. */
export interface Buildable {
  readonly [BUILD]: Build;
}

export function isBuildable(value: unknown): value is Buildable {
  return typeof value === "object" && value !== null && BUILD in value;
}

/** Marks members clients may sort by (generated from `IsSortable()` and keys). */
export interface Sortable {
  /** Type-only: generated proxy types add it to sortable members. */
  readonly __sortable: true;
}

/** Marks members grouped queries may use as keys (generated from `IsGroupable()`). */
export interface Groupable {
  /** Type-only: generated proxy types add it to groupable members. */
  readonly __groupable: true;
}

/** Marks members sum, min, max and average over groups may read (generated from `IsAggregatable()`). */
export interface Aggregatable {
  /** Type-only: generated proxy types add it to aggregatable members. */
  readonly __aggregatable: true;
}

/** A numeric expression, nullable or not. */
export type AnyNumberExpr = NumberExpr | NumberExpr<number | null>;

/** A value usable where an expression of type `T` is expected: a plain value or an expression. */
export type Operand<T> =
  NonNullable<T> | (null extends T ? null : never) | Expr<NonNullable<T>> | Expr<NonNullable<T> | null>;

export function node(t: Translator, value: unknown): QueryNode {
  return isBuildable(value) ? value[BUILD](t) : literal(value);
}

function call(op: string, ...operands: unknown[]): Build {
  return (t) => ({ [op]: operands.map((operand) => node(t, operand)) });
}

/** An expression of type `T` (`null` included for nullable members). */
export class Expr<T> implements Buildable {
  declare readonly __value: T;
  readonly [BUILD]: Build;

  constructor(build: Build) {
    this[BUILD] = build;
  }

  eq(other: Operand<T>): BoolExpr {
    return new BoolExpr(call("eq", this, other));
  }

  ne(other: Operand<T>): BoolExpr {
    return new BoolExpr(call("ne", this, other));
  }

  /** `value in [..]`, sent as one list parameter. */
  in(values: readonly NonNullable<T>[]): BoolExpr {
    return new BoolExpr((t) => ({ in: [this[BUILD](t), values.map(literal)] }));
  }
}

/** A boolean expression; `and`/`or` chains flatten like C#'s `&&`/`||`. */
export class BoolExpr extends Expr<boolean> {
  constructor(
    build: Build,
    private readonly junction?: { readonly op: "and" | "or"; readonly parts: readonly Operand<boolean>[] },
  ) {
    super(build);
  }

  and(...others: Operand<boolean>[]): BoolExpr {
    return this.join("and", others);
  }

  or(...others: Operand<boolean>[]): BoolExpr {
    return this.join("or", others);
  }

  not(): BoolExpr {
    return new BoolExpr(call("not", this));
  }

  private join(op: "and" | "or", others: Operand<boolean>[]): BoolExpr {
    const parts = [this, ...others].flatMap((part) =>
      part instanceof BoolExpr && part.junction?.op === op ? part.junction.parts : [part],
    );
    return new BoolExpr(call(op, ...parts), { op, parts });
  }
}

/** An expression with an order: comparisons beyond equality. */
export class OrderedExpr<T> extends Expr<T> {
  gt(other: Operand<T>): BoolExpr {
    return new BoolExpr(call("gt", this, other));
  }

  ge(other: Operand<T>): BoolExpr {
    return new BoolExpr(call("ge", this, other));
  }

  lt(other: Operand<T>): BoolExpr {
    return new BoolExpr(call("lt", this, other));
  }

  le(other: Operand<T>): BoolExpr {
    return new BoolExpr(call("le", this, other));
  }
}

export class NumberExpr<T extends number | null = number> extends OrderedExpr<T> {
  add(other: Operand<number>): NumberExpr<T> {
    return new NumberExpr(call("add", this, other));
  }

  sub(other: Operand<number>): NumberExpr<T> {
    return new NumberExpr(call("sub", this, other));
  }

  mul(other: Operand<number>): NumberExpr<T> {
    return new NumberExpr(call("mul", this, other));
  }

  div(other: Operand<number>): NumberExpr<T> {
    return new NumberExpr(call("div", this, other));
  }

  mod(other: Operand<number>): NumberExpr<T> {
    return new NumberExpr(call("mod", this, other));
  }

  neg(): NumberExpr<T> {
    return new NumberExpr(call("neg", this));
  }

  coalesce(fallback: Operand<number>): NumberExpr {
    return new NumberExpr(call("coalesce", this, fallback));
  }
}

/** A string expression; `concat` chains flatten like C#'s string `+`. */
export class StringExpr<T extends string | null = string> extends OrderedExpr<T> {
  constructor(
    build: Build,
    private readonly parts?: readonly unknown[],
  ) {
    super(build);
  }

  contains(value: Operand<string>): BoolExpr {
    return new BoolExpr(call("contains", this, value));
  }

  startsWith(value: Operand<string>): BoolExpr {
    return new BoolExpr(call("startsWith", this, value));
  }

  endsWith(value: Operand<string>): BoolExpr {
    return new BoolExpr(call("endsWith", this, value));
  }

  /** Case-insensitive equality: both sides lowercased, like `string.Equals(a, b, OrdinalIgnoreCase)`. */
  equalsIgnoreCase(value: Operand<string>): BoolExpr {
    return new BoolExpr((t) => ({ eq: [{ toLower: [node(t, this)] }, { toLower: [node(t, value)] }] }));
  }

  toLower(): StringExpr<T> {
    return new StringExpr(call("toLower", this));
  }

  toUpper(): StringExpr<T> {
    return new StringExpr(call("toUpper", this));
  }

  trim(): StringExpr<T> {
    return new StringExpr(call("trim", this));
  }

  length(): NumberExpr {
    return new NumberExpr(call("length", this));
  }

  /** Concatenates strings, numbers and other values (converted to text on the server). */
  concat(...values: unknown[]): StringExpr {
    const parts = [...(this.parts ?? [this]), ...values];
    return new StringExpr(call("concat", ...parts), parts);
  }

  coalesce(fallback: Operand<string>): StringExpr {
    return new StringExpr(call("coalesce", this, fallback));
  }
}

/** A `DateTime` or `DateTimeOffset` (ISO 8601 strings). */
export class DateTimeExpr<T extends string | null = string> extends OrderedExpr<T> {
  year(): NumberExpr {
    return new NumberExpr(call("year", this));
  }

  month(): NumberExpr {
    return new NumberExpr(call("month", this));
  }

  day(): NumberExpr {
    return new NumberExpr(call("day", this));
  }

  hour(): NumberExpr {
    return new NumberExpr(call("hour", this));
  }

  minute(): NumberExpr {
    return new NumberExpr(call("minute", this));
  }

  /** The date at midnight. */
  date(): DateTimeExpr<T> {
    return new DateTimeExpr(call("date", this));
  }

  coalesce(fallback: Operand<string>): DateTimeExpr {
    return new DateTimeExpr(call("coalesce", this, fallback));
  }
}

/** A `DateOnly` (`yyyy-MM-dd`). */
export class DateOnlyExpr<T extends string | null = string> extends OrderedExpr<T> {
  year(): NumberExpr {
    return new NumberExpr(call("year", this));
  }

  month(): NumberExpr {
    return new NumberExpr(call("month", this));
  }

  day(): NumberExpr {
    return new NumberExpr(call("day", this));
  }
}

/** A `TimeOnly` (`HH:mm:ss`). */
export class TimeOnlyExpr<T extends string | null = string> extends OrderedExpr<T> {
  hour(): NumberExpr {
    return new NumberExpr(call("hour", this));
  }

  minute(): NumberExpr {
    return new NumberExpr(call("minute", this));
  }
}

/** `test ? whenTrue : whenFalse`. */
export function cond(test: Operand<boolean>, whenTrue: Operand<string>, whenFalse: Operand<string>): StringExpr;
export function cond(test: Operand<boolean>, whenTrue: Operand<number>, whenFalse: Operand<number>): NumberExpr;
export function cond(test: Operand<boolean>, whenTrue: Operand<boolean>, whenFalse: Operand<boolean>): BoolExpr;
export function cond(test: Operand<boolean>, whenTrue: unknown, whenFalse: unknown): Expr<unknown> {
  const build = call("if", test, whenTrue, whenFalse);
  if (typeof whenTrue === "string" || whenTrue instanceof StringExpr) {
    return new StringExpr(build);
  }

  return typeof whenTrue === "boolean" || whenTrue instanceof BoolExpr ? new BoolExpr(build) : new NumberExpr(build);
}

/**
 * A value (such as an address) as seen by a lambda: its members are expressions, and the whole value can only be
 * selected or compared with null. Generated proxy interfaces extend it; it isn't `Sortable` or `Groupable`.
 */
export interface ValueProxy<V> extends Buildable {
  readonly __valueType?: V;
  eq(value: null): BoolExpr;
  ne(value: null): BoolExpr;
}

/** A DTO as seen by a lambda. Generated proxy interfaces extend it. */
export interface EntityProxy<D> extends Buildable {
  readonly __dto?: D;
}

type Selector<P, R> = (element: P) => R;

/** The parts every collection lambda shares. */
abstract class CollectionBase<P> implements Buildable {
  readonly [BUILD]: Build;

  protected constructor(
    protected readonly source: string,
    protected readonly element: (variable: string) => P,
    protected readonly filters: readonly Selector<P, Operand<boolean>>[],
  ) {
    this[BUILD] = () => ({ member: source });
  }

  /** The node the lambdas read: the collection member. */
  protected sourceNode(): QueryNode {
    return { member: this.source };
  }

  /** The member use of an aggregate's body (a group reads only aggregatable members there). */
  protected bodyUse(t: Translator): Translator["use"] {
    return t.use;
  }

  any(predicate?: Selector<P, Operand<boolean>>): BoolExpr {
    return new BoolExpr((t) => this.lambda(t, "any", predicate, true));
  }

  all(predicate: Selector<P, Operand<boolean>>): BoolExpr {
    return new BoolExpr((t) => this.lambda(t, "all", predicate, true));
  }

  count(predicate?: Selector<P, Operand<boolean>>): NumberExpr {
    return new NumberExpr((t) => this.lambda(t, "count", predicate, true));
  }

  /** Projects each element (only in `select`). */
  select<S>(selector: Selector<P, S>): MapExpr<S> {
    return new MapExpr<S>((t) => this.lambda(t, "map", selector, false, true));
  }

  /**
   * Builds `{op: {source, var, where, body}}` like the .NET client: the variable is allocated first, then the
   * filters run, then the body. Any/count without a body use the filter as their body.
   */
  protected lambda(
    t: Translator,
    op: string,
    body: Selector<P, unknown> | undefined,
    predicate: boolean,
    shape = false,
  ): QueryNode {
    if (t.inGroupAggregate && !(this instanceof GroupExpr)) {
      throw new Error("Aggregates over a group can't contain other collection lambdas.");
    }

    const outer = t.inGroupAggregate;
    t.inGroupAggregate = this instanceof GroupExpr;
    try {
      return this.lambdaNode(t, op, body, predicate, shape);
    } finally {
      t.inGroupAggregate = outer;
    }
  }

  private lambdaNode(
    t: Translator,
    op: string,
    body: Selector<P, unknown> | undefined,
    predicate: boolean,
    shape: boolean,
  ): QueryNode {
    const variable = t.allocate();
    const element = this.element(variable);
    const filters = this.filters.map((filter) => t.within("filter", () => node(t, filter(element))));
    let where: QueryNode | undefined =
      filters.length === 0 ? undefined : filters.length === 1 ? filters[0] : { and: filters };
    let built: QueryNode | undefined;
    if (body !== undefined) {
      built = predicate
        ? t.within("filter", () => node(t, body(element)))
        : shape
          ? selectNode(t, body(element))
          : t.within(this.bodyUse(t), () => node(t, body(element)));
    }

    if ((op === "any" || op === "count") && built === undefined && where !== undefined) {
      [built, where] = [where, undefined];
    }

    const lambda: Record<string, unknown> = { source: this.sourceNode() };
    if (built !== undefined || where !== undefined) {
      lambda.var = variable;
    }

    if (where !== undefined) {
      lambda.where = where;
    }

    if (built !== undefined) {
      lambda.body = built;
    }

    return { [op]: lambda };
  }
}

/** A collection navigation: lambdas over DTO elements. */
export class CollectionExpr<P extends EntityProxy<unknown>, D = NonNullable<P["__dto"]>> extends CollectionBase<P> {
  declare readonly __element: D;

  /** @internal */
  constructor(
    source: string,
    element: (variable: string) => P,
    filters: readonly Selector<P, Operand<boolean>>[] = [],
  ) {
    super(source, element, filters);
  }

  where(predicate: Selector<P, Operand<boolean>>): CollectionExpr<P, D> {
    return new CollectionExpr<P, D>(this.source, this.element, [...this.filters, predicate]);
  }

  sum(selector: Selector<P, AnyNumberExpr>): NumberExpr {
    return new NumberExpr((t) => this.lambda(t, "sum", selector, false));
  }

  avg(selector: Selector<P, AnyNumberExpr>): NumberExpr<number | null> {
    return new NumberExpr((t) => this.lambda(t, "avg", selector, false));
  }

  min<E extends OrderedExpr<unknown>>(selector: Selector<P, E>): E {
    return this.extreme("min", selector);
  }

  max<E extends OrderedExpr<unknown>>(selector: Selector<P, E>): E {
    return this.extreme("max", selector);
  }

  private extreme<E extends OrderedExpr<unknown>>(op: string, selector: Selector<P, E>): E {
    const build: Build = (t) => this.lambda(t, op, selector, false);
    return sameKind(selector(this.element("_")), build) as E;
  }
}

/** A primitive collection (`string[]`, `number[]`…): lambdas over scalar elements. */
export class PrimitiveCollectionExpr<E extends Expr<unknown>> extends CollectionBase<E> {
  /** @internal */
  constructor(
    source: string,
    element: (variable: string) => E,
    filters: readonly Selector<E, Operand<boolean>>[] = [],
  ) {
    super(source, element, filters);
  }

  where(predicate: Selector<E, Operand<boolean>>): PrimitiveCollectionExpr<E> {
    return new PrimitiveCollectionExpr<E>(this.source, this.element, [...this.filters, predicate]);
  }

  sum(selector?: Selector<E, AnyNumberExpr>): NumberExpr {
    return new NumberExpr((t) => this.lambda(t, "sum", selector, false));
  }

  avg(selector?: Selector<E, AnyNumberExpr>): NumberExpr<number | null> {
    return new NumberExpr((t) => this.lambda(t, "avg", selector, false));
  }

  min(): E {
    return sameKind(this.element("_"), (t) => this.lambda(t, "min", undefined, false)) as E;
  }

  max(): E {
    return sameKind(this.element("_"), (t) => this.lambda(t, "max", undefined, false)) as E;
  }
}

/**
 * The rows of one group, inside a group `select`: `count`, `sum`, `min`, `max` and `average` over them. Sum, min, max
 * and average may only read aggregatable members (checked when the query is sent).
 */
export class GroupExpr<K, P> extends CollectionBase<P> {
  /** @internal */
  constructor(
    /** The group's key: an expression, or an object of expressions for a composite key. */
    readonly key: K,
    element: (variable: string) => P,
  ) {
    super("", element, []);
  }

  sum(selector: Selector<P, AnyNumberExpr>): NumberExpr {
    return new NumberExpr((t) => this.lambda(t, "sum", selector, false));
  }

  average(selector: Selector<P, AnyNumberExpr>): NumberExpr<number | null> {
    return new NumberExpr((t) => this.lambda(t, "avg", selector, false));
  }

  min<E extends OrderedExpr<unknown>>(selector: Selector<P, E>): E {
    return sameKind(selector(this.element("_")), (t) => this.lambda(t, "min", selector, false)) as E;
  }

  max<E extends OrderedExpr<unknown>>(selector: Selector<P, E>): E {
    return sameKind(selector(this.element("_")), (t) => this.lambda(t, "max", selector, false)) as E;
  }

  /** Groups can only be aggregated, never tested with any/all or projected. */
  override any(): never {
    throw new Error("Over a group, use count, sum, min, max or average.");
  }

  override all(): never {
    throw new Error("Over a group, use count, sum, min, max or average.");
  }

  override select(): never {
    throw new Error("Over a group, use count, sum, min, max or average.");
  }

  protected override sourceNode(): QueryNode {
    return { group: {} };
  }

  protected override bodyUse(): Translator["use"] {
    return "aggregate";
  }
}

/**
 * The keys of a grouped query as `g.key` sees them: one expression (`{"key": 0}`) or an object of them, each of the
 * same kind as the key it stands for.
 */
export function keyProxy(key: unknown): unknown {
  if (isBuildable(key)) {
    return sameKindAs(key, () => ({ key: 0 }));
  }

  return Object.fromEntries(
    Object.entries(key as Record<string, unknown>).map(([name, value], index) => [
      name,
      sameKindAs(value, () => ({ key: index })),
    ]),
  );
}

/** The same kind of expression as `prototype`, building `build` (keys and fields of a grouped query). */
export function sameKindAs(prototype: unknown, build: Build): Expr<unknown> {
  return prototype instanceof BoolExpr
    ? new BoolExpr(build)
    : prototype instanceof Expr && !(prototype instanceof OrderedExpr)
      ? new Expr(build)
      : sameKind(prototype, build);
}

/** A projected collection (`select` over a collection). */
export class MapExpr<S> implements Buildable {
  declare readonly __mapped: S;
  readonly [BUILD]: Build;

  constructor(build: Build) {
    this[BUILD] = build;
  }
}

function sameKind(prototype: unknown, build: Build): Expr<unknown> {
  if (prototype instanceof StringExpr) return new StringExpr(build);
  if (prototype instanceof DateTimeExpr) return new DateTimeExpr(build);
  if (prototype instanceof DateOnlyExpr) return new DateOnlyExpr(build);
  if (prototype instanceof TimeOnlyExpr) return new TimeOnlyExpr(build);
  if (prototype instanceof NumberExpr) return new NumberExpr(build);
  return new OrderedExpr(build);
}

/** The select node of a projection value: shapes become `{"new": …}`, everything else its own node. */
export function selectNode(t: Translator, value: unknown): QueryNode {
  if (isBuildable(value)) {
    return value[BUILD](t);
  }

  if (typeof value === "object" && value !== null && !Array.isArray(value) && !(value instanceof Date)) {
    const fields: Record<string, QueryNode> = {};
    for (const [name, field] of Object.entries(value)) {
      fields[name] = selectNode(t, field);
    }

    return { new: fields };
  }

  return literal(value);
}

function scalar(member: MemberModel, build: Build): Expr<unknown> {
  switch (member.kind) {
    case "number":
      return new NumberExpr(build);
    case "string":
      return new StringExpr(build);
    case "boolean":
      return new BoolExpr(build);
    case "datetime":
    case "datetimeoffset":
      return new DateTimeExpr(build);
    case "dateonly":
      return new DateOnlyExpr(build);
    case "timeonly":
      return new TimeOnlyExpr(build);
    case "enum":
    case "timespan":
      return new OrderedExpr(build);
    case "guid":
    case "binary":
    case "value":
      return new Expr(build);
  }
}

function memberBuild(owner: string, member: MemberModel, path: string): Build {
  return (t) => {
    if (t.use === "filter" && !member.filterable) {
      throw new Error(`${owner}.${member.name} can't be filtered (IsNotFilterable in the DTO model).`);
    }

    if (t.use === "group" && member.groupable !== true) {
      throw new Error(`${owner}.${member.name} isn't groupable: the API marks group keys with IsGroupable().`);
    }

    if (t.use === "aggregate" && member.aggregatable !== true) {
      throw new Error(
        `${owner}.${member.name} isn't aggregatable: the API marks members for sum, min, max and average with IsAggregatable().`,
      );
    }

    return { member: path };
  };
}

/**
 * The proxy of a value-type member at `path`. Its members are paths below it (not filterable when the value isn't);
 * the whole value builds only where a value may stand: a select, or `eq(null)` / `ne(null)`.
 */
function valueProxy(owner: string, member: MemberModel, path: string): ValueProxy<unknown> {
  const valueType = member.value;
  if (valueType === undefined) {
    throw new Error(`${owner}.${member.name} has no value type in the model.`);
  }

  const proxy = {} as Record<PropertyKey, unknown>;
  const build: Build = (t) => {
    if (t.use === "filter" && !member.filterable) {
      throw new Error(`${owner}.${member.name} can't be filtered (IsNotFilterable in the DTO model).`);
    }

    if (t.use !== "filter" && t.use !== "select") {
      throw new Error(
        `${owner}.${member.name} is a value such as an address: filter, sort and group by its members, such as p => p.${member.name}.${valueType.members[0]?.name ?? "x"}.`,
      );
    }

    return { member: path };
  };
  const nullTest =
    (op: "eq" | "ne") =>
    // Typed `null` on ValueProxy; checked here for callers outside TypeScript.
    (value: unknown): BoolExpr => {
      if (value !== null) {
        throw new Error(`${owner}.${member.name} is a value such as an address: it can only be compared with null.`);
      }

      return new BoolExpr((t) => ({ [op]: [build(t), literal(null)] }));
    };
  Object.defineProperty(proxy, BUILD, { value: build });
  Object.defineProperty(proxy, "eq", { value: nullTest("eq") });
  Object.defineProperty(proxy, "ne", { value: nullTest("ne") });
  for (const inner of valueType.members) {
    const nested = { ...inner, filterable: inner.filterable && member.filterable };
    const innerPath = `${path}/${inner.name}`;
    Object.defineProperty(proxy, inner.name, {
      enumerable: true,
      get: () =>
        inner.kind === "value"
          ? valueProxy(valueType.name, nested, innerPath)
          : scalar(nested, memberBuild(valueType.name, nested, innerPath)),
    });
  }

  return proxy as unknown as ValueProxy<unknown>;
}

/** Creates the proxy a lambda receives for one DTO, rooted at `base` (a navigation path or lambda variable). */
export function entityProxy(model: Model, set: SetModel, base: string | null): EntityProxy<unknown> {
  const proxy = {} as Record<PropertyKey, unknown>;
  const path = (name: string): string => (base === null ? name : `${base}/${name}`);
  Object.defineProperty(proxy, BUILD, {
    value: (() => (base === null ? ROOT : { member: base })) satisfies Build,
  });
  for (const member of set.members) {
    Object.defineProperty(proxy, member.name, {
      enumerable: true,
      get: () =>
        member.kind === "value"
          ? valueProxy(set.name, member, path(member.name))
          : member.collection
            ? new PrimitiveCollectionExpr(path(member.name), (variable) =>
                scalar({ ...member, collection: false }, () => ({ var: variable })),
              )
            : scalar(member, memberBuild(set.name, member, path(member.name))),
    });
  }

  for (const navigation of set.navigations) {
    const target = findSet(model, navigation.target);
    Object.defineProperty(proxy, navigation.name, {
      enumerable: true,
      get: () =>
        navigation.collection
          ? new CollectionExpr(path(navigation.name), (variable) => entityProxy(model, target, variable))
          : entityProxy(model, target, path(navigation.name)),
    });
  }

  return proxy as unknown as EntityProxy<unknown>;
}

/** What the root proxy builds into: `select(p => p)` means the full DTO. */
export const ROOT: QueryNode = Object.freeze({ root: true });
