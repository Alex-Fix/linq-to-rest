import {
  BUILD,
  CollectionExpr,
  type EntityProxy,
  entityProxy,
  Expr,
  isBuildable,
  MapExpr,
  node,
  type Operand,
  PrimitiveCollectionExpr,
  ROOT,
  selectNode,
  type Sortable,
} from "./expressions.js";
import { findSet, type Model, type SetModel } from "./model.js";
import type { QueryNode } from "./nodes.js";
import type { ChangeTracker } from "./tracking.js";
import { Translator } from "./translator.js";
import type { CallOptions, Transport } from "./transport.js";

/** The result type of a projection: expressions become their values, shapes become objects. */
export type Projected<S> =
  S extends Expr<infer T>
    ? T
    : S extends MapExpr<infer E>
      ? Projected<E>[]
      : S extends { readonly __element: infer D }
        ? D[]
        : S extends PrimitiveCollectionExpr<infer E>
          ? Projected<E>[]
          : S extends EntityProxy<infer D>
            ? D | null
            : S extends readonly unknown[]
              ? never
              : S extends object
                ? { -readonly [K in keyof S]: Projected<S[K]> }
                : S;

/** One keyset page. */
export interface CursorPage<T> {
  readonly items: T[];
  /** The cursor of the next page, or undefined on the last one. Pass it to `after`. */
  readonly nextCursor: string | undefined;
  readonly totalCount: number | undefined;
}

export interface QueryHost {
  readonly model: Model;
  readonly transport: Transport;
  readonly tracker: ChangeTracker;
}

type Op =
  | { readonly kind: "where"; readonly predicate: (proxy: never) => Operand<boolean> }
  | { readonly kind: "include"; readonly navigation: (proxy: never) => unknown }
  | { readonly kind: "orderBy"; readonly key: (proxy: never) => unknown; readonly descending: boolean }
  | { readonly kind: "select"; readonly selector: (proxy: never) => unknown };

interface State {
  readonly ops: readonly Op[];
  readonly skip?: number | undefined;
  readonly take?: number | undefined;
  readonly after?: string | undefined;
  readonly noTracking: boolean;
  readonly phase: "compose" | "paged" | "taken";
  readonly selected: boolean;
  readonly sorted: boolean;
}

interface Translated {
  readonly filter: QueryNode | undefined;
  readonly orderBy: readonly { member: string; desc?: true }[];
  readonly select: QueryNode | undefined;
  readonly include: readonly string[];
}

type Page = { mode: "offset"; skip?: number; take: number } | { mode: "cursor"; after?: string; take: number };

const NOTHING: QueryNode = { new: {} };

/**
 * An immutable query over one set: Where/Include/OrderBy/ThenBy, then Select, with After or Skip, then Take, anywhere
 * after the sort — the .NET client's rules. Nothing is sent until a terminal operator runs.
 */
export class Query<T, P, D> {
  /** @internal */
  constructor(
    protected readonly host: QueryHost,
    protected readonly set: SetModel,
    private readonly state: State = { ops: [], noTracking: false, phase: "compose", selected: false, sorted: false },
  ) {}

  where(predicate: (proxy: P) => Operand<boolean>): Query<T, P, D> {
    this.require(this.composing(), "where");
    return this.with({ ops: [...this.state.ops, { kind: "where", predicate }] });
  }

  include(navigation: (proxy: P) => EntityProxy<unknown> | CollectionExpr<EntityProxy<unknown>>): Query<T, P, D> {
    this.require(this.composing(), "include");
    return this.with({ ops: [...this.state.ops, { kind: "include", navigation }] });
  }

  orderBy(key: (proxy: P) => Expr<unknown> & Sortable): Query<T, P, D> {
    return this.sort(key, false, true, "orderBy");
  }

  orderByDescending(key: (proxy: P) => Expr<unknown> & Sortable): Query<T, P, D> {
    return this.sort(key, true, true, "orderByDescending");
  }

  thenBy(key: (proxy: P) => Expr<unknown> & Sortable): Query<T, P, D> {
    return this.sort(key, false, false, "thenBy");
  }

  thenByDescending(key: (proxy: P) => Expr<unknown> & Sortable): Query<T, P, D> {
    return this.sort(key, true, false, "thenByDescending");
  }

  select<S>(selector: (proxy: P) => S): Query<Projected<S>, P, D> {
    this.require(!this.state.selected, "select");
    return this.with({ ops: [...this.state.ops, { kind: "select", selector }], selected: true }) as unknown as Query<
      Projected<S>,
      P,
      D
    >;
  }

  skip(count: number): Query<T, P, D> {
    this.require(this.state.phase === "compose", "skip");
    return this.with({ skip: count, phase: "paged" });
  }

  /** Continues keyset pagination after a cursor from `toCursorPage`. */
  after(cursor: string): Query<T, P, D> {
    this.require(this.state.phase === "compose", "after");
    return this.with({ after: cursor, phase: "paged" });
  }

  take(count: number): Query<T, P, D> {
    this.require(this.state.phase !== "taken", "take");
    return this.with({ take: count, phase: "taken" });
  }

  /** Returns DTOs without tracking them. */
  asNoTracking(): Query<T, P, D> {
    return this.with({ noTracking: true });
  }

  /** Runs the query and returns one page. Requires `take`, so a server page size can't pass for "all rows". */
  async toList(options: CallOptions = {}): Promise<T[]> {
    if (this.state.take === undefined) {
      throw new Error("Add take(n) before toList(), or use stream() to read every row.");
    }

    const response = await this.send(this.page(this.state.take), options);
    return response.items;
  }

  /** Returns one keyset page and the cursor of the next one. */
  async toCursorPage(options: CallOptions & { readonly includeTotalCount?: boolean } = {}): Promise<CursorPage<T>> {
    const page = this.cursor(this.state.after, this.state.take ?? this.set.pagination.defaultPageSize);
    return this.send(page, options, options.includeTotalCount === true);
  }

  /** Streams every matching row, following cursor pages (`take` caps the total). */
  async *stream(options: CallOptions = {}): AsyncGenerator<T, void, undefined> {
    let remaining = this.state.take ?? Number.POSITIVE_INFINITY;
    let after = this.state.after;
    while (remaining > 0) {
      const page = await this.send(
        this.cursor(after, Math.min(remaining, this.set.pagination.defaultPageSize)),
        options,
      );
      yield* page.items;
      remaining -= page.items.length;
      after = page.nextCursor;
      if (after === undefined) {
        return;
      }
    }
  }

  first(options?: CallOptions): Promise<T>;
  first(predicate: (proxy: P) => Operand<boolean>, options?: CallOptions): Promise<T>;
  async first(predicate?: ((proxy: P) => Operand<boolean>) | CallOptions, options?: CallOptions): Promise<T> {
    const items = await this.filtered(predicate).fetch(1, options ?? optionsOf(predicate));
    if (items.length === 0) {
      throw new Error("The query returned no rows.");
    }

    return items[0] as T;
  }

  firstOrDefault(options?: CallOptions): Promise<T | undefined>;
  firstOrDefault(predicate: (proxy: P) => Operand<boolean>, options?: CallOptions): Promise<T | undefined>;
  async firstOrDefault(
    predicate?: ((proxy: P) => Operand<boolean>) | CallOptions,
    options?: CallOptions,
  ): Promise<T | undefined> {
    return (await this.filtered(predicate).fetch(1, options ?? optionsOf(predicate)))[0];
  }

  single(options?: CallOptions): Promise<T>;
  single(predicate: (proxy: P) => Operand<boolean>, options?: CallOptions): Promise<T>;
  async single(predicate?: ((proxy: P) => Operand<boolean>) | CallOptions, options?: CallOptions): Promise<T> {
    const items = await this.filtered(predicate).fetch(2, options ?? optionsOf(predicate));
    if (items.length !== 1) {
      throw new Error(items.length === 0 ? "The query returned no rows." : "The query returned more than one row.");
    }

    return items[0] as T;
  }

  singleOrDefault(options?: CallOptions): Promise<T | undefined>;
  singleOrDefault(predicate: (proxy: P) => Operand<boolean>, options?: CallOptions): Promise<T | undefined>;
  async singleOrDefault(
    predicate?: ((proxy: P) => Operand<boolean>) | CallOptions,
    options?: CallOptions,
  ): Promise<T | undefined> {
    const items = await this.filtered(predicate).fetch(2, options ?? optionsOf(predicate));
    if (items.length > 1) {
      throw new Error("The query returned more than one row.");
    }

    return items[0];
  }

  /** Counts the matching rows (after skip/take, like LINQ). */
  count(options?: CallOptions): Promise<number>;
  count(predicate: (proxy: P) => Operand<boolean>, options?: CallOptions): Promise<number>;
  async count(predicate?: ((proxy: P) => Operand<boolean>) | CallOptions, options?: CallOptions): Promise<number> {
    const query = this.filtered(predicate);
    if (query.state.after !== undefined) {
      throw new Error("Counting after a cursor isn't supported; count the query without after().");
    }

    const response = await query
      .with({ skip: undefined })
      .send(query.with({ skip: undefined }).page(0), options ?? optionsOf(predicate), true, NOTHING);
    const count = Math.max(0, (response.totalCount ?? 0) - (query.state.skip ?? 0));
    return query.state.take === undefined ? count : Math.min(count, query.state.take);
  }

  /** Returns whether any row matches. */
  any(options?: CallOptions): Promise<boolean>;
  any(predicate: (proxy: P) => Operand<boolean>, options?: CallOptions): Promise<boolean>;
  async any(predicate?: ((proxy: P) => Operand<boolean>) | CallOptions, options?: CallOptions): Promise<boolean> {
    const query = this.filtered(predicate);
    const take = query.state.take === undefined ? 1 : Math.min(query.state.take, 1);
    const response = await query.send(query.page(take), options ?? optionsOf(predicate), false, NOTHING);
    return response.items.length > 0;
  }

  /** The request body this query sends, for debugging and tests. */
  toJSON(): unknown {
    return this.request(this.page(this.state.take ?? this.set.pagination.defaultPageSize), false, undefined);
  }

  private async fetch(limit: number, options: CallOptions): Promise<T[]> {
    const take = this.state.take === undefined ? limit : Math.min(this.state.take, limit);
    return (await this.send(this.page(take), options)).items;
  }

  private filtered(predicate: ((proxy: P) => Operand<boolean>) | CallOptions | undefined): Query<T, P, D> {
    return typeof predicate === "function" ? this.where(predicate) : this;
  }

  private sort(
    key: (proxy: P) => Expr<unknown> & Sortable,
    descending: boolean,
    first: boolean,
    name: string,
  ): Query<T, P, D> {
    this.require(this.composing() && this.state.sorted !== first, name);
    return this.with({ ops: [...this.state.ops, { kind: "orderBy", key, descending }], sorted: true });
  }

  private composing(): boolean {
    return this.state.phase === "compose" && !this.state.selected;
  }

  private require(allowed: boolean, name: string): void {
    if (!allowed) {
      throw new Error(
        `'${name}' isn't supported at this point. RestToLinq queries are where/include/orderBy/thenBy, then select, with after or skip, then take, anywhere after the sort; each at most once except where (orderBy once, then thenBy).`,
      );
    }
  }

  private with(changes: Partial<State>): Query<T, P, D> {
    return new Query<T, P, D>(this.host, this.set, { ...this.state, ...changes });
  }

  /** A cursor page after `after`, else offset when the set allows it. */
  private page(take: number): Page {
    if (this.state.after !== undefined) {
      return this.cursor(this.state.after, take);
    }

    if (this.set.pagination.modes.includes("offset")) {
      return this.state.skip === undefined ? { mode: "offset", take } : { mode: "offset", skip: this.state.skip, take };
    }

    if (this.state.skip !== undefined) {
      throw new Error(`The set '${this.set.name}' only allows cursor pagination. Use after() instead of skip().`);
    }

    return this.cursor(undefined, take);
  }

  private cursor(after: string | undefined, take: number): Page {
    if (!this.set.pagination.modes.includes("cursor")) {
      throw new Error(`The set '${this.set.name}' doesn't allow cursor pagination.`);
    }

    if (this.state.skip !== undefined) {
      throw new Error("skip() can't be combined with cursor pagination.");
    }

    return after === undefined ? { mode: "cursor", take } : { mode: "cursor", after, take };
  }

  private translate(): Translated {
    const t = new Translator(this.set);
    const root = entityProxy(this.host.model, this.set, null);
    const filters: QueryNode[] = [];
    const orderBy: { member: string; desc?: true }[] = [];
    const include: string[] = [];
    let select: QueryNode | undefined;
    for (const op of this.state.ops) {
      switch (op.kind) {
        case "where":
          filters.push(t.within("filter", () => node(t, op.predicate(root as never))));
          break;
        case "include":
          include.push(this.navigationPath(op.navigation(root as never)));
          break;
        case "orderBy":
          orderBy.push({
            member: this.sortPath(op.key(root as never), t),
            ...(op.descending ? { desc: true as const } : {}),
          });
          break;
        case "select": {
          const value = op.selector(root as never);
          select = value === root ? undefined : t.within("select", () => selectNode(t, value));
          break;
        }
      }
    }

    const filter = filters.length === 0 ? undefined : filters.length === 1 ? filters[0] : { and: filters };
    return { filter, orderBy, select, include };
  }

  private navigationPath(value: unknown): string {
    // Navigations are entity proxies and DTO collections; scalars, projections and primitive collections aren't.
    const isNavigation =
      value instanceof CollectionExpr ||
      (isBuildable(value) &&
        !(value instanceof Expr) &&
        !(value instanceof MapExpr) &&
        !(value instanceof PrimitiveCollectionExpr));
    const built = isNavigation ? value[BUILD](new Translator(this.set)) : undefined;
    if (built === ROOT || typeof built !== "object" || built === null || !("member" in built)) {
      throw new Error("include() needs a navigation, such as p => p.category.");
    }

    return String(built.member);
  }

  private sortPath(value: unknown, t: Translator): string {
    const built = isBuildable(value) ? t.within("sort", () => value[BUILD](t)) : undefined;
    const path = typeof built === "object" && built !== null && "member" in built ? String(built.member) : undefined;
    const member = path === undefined ? undefined : this.resolve(path);
    if (path === undefined || member === undefined || member.collection) {
      throw new Error("Sort by a scalar DTO member, such as p => p.price.");
    }

    if (!member.sortable) {
      throw new Error(`${this.set.name}: '${path}' isn't sortable (IsSortable in the DTO model).`);
    }

    return path;
  }

  private resolve(path: string): SetModel["members"][number] | undefined {
    const segments = path.split("/");
    let set = this.set;
    for (const segment of segments.slice(0, -1)) {
      const navigation = set.navigations.find((candidate) => candidate.name === segment && !candidate.collection);
      if (navigation === undefined) {
        return undefined;
      }

      set = findSet(this.host.model, navigation.target);
    }

    return set.members.find((member) => member.name === segments.at(-1));
  }

  private request(page: Page, count: boolean, select: QueryNode | undefined): Record<string, unknown> {
    const translated = this.translate();
    const body: Record<string, unknown> = { v: 1 };
    if (translated.filter !== undefined) body.filter = translated.filter;
    if (translated.orderBy.length > 0) body.orderBy = translated.orderBy;
    const projection = select ?? translated.select;
    if (projection !== undefined) body.select = projection;
    if (translated.include.length > 0) body.include = translated.include;
    body.page = page;
    if (count) body.count = true;
    if (translated.select !== undefined && translated.include.length > 0) {
      throw new Error("include() can't be combined with select(); select the navigation instead.");
    }

    return body;
  }

  private async send(page: Page, options: CallOptions, count = false, select?: QueryNode): Promise<CursorPage<T>> {
    const body = this.request(page, count, select);
    const response = (await this.host.transport.query(this.set.name, body, options.signal)) as {
      items: unknown[];
      nextCursor?: string;
      totalCount?: number;
    };
    const fullDtos = select === undefined && !this.state.selected;
    const items = fullDtos ? response.items.map((item) => this.materialize(item)) : response.items;
    return { items: items as T[], nextCursor: response.nextCursor, totalCount: response.totalCount };
  }

  /** Gives missing (not included) navigations their empty value and tracks the DTO unless asNoTracking. */
  private materialize(item: unknown): unknown {
    fillNavigations(this.host.model, this.set, item as Record<string, unknown>);
    return this.state.noTracking ? item : this.host.tracker.trackQueried(item as object, this.set);
  }
}

function optionsOf(value: unknown): CallOptions {
  return typeof value === "object" && value !== null ? value : {};
}

function fillNavigations(model: Model, set: SetModel, dto: Record<string, unknown>): void {
  for (const navigation of set.navigations) {
    const value = dto[navigation.name];
    const target = findSet(model, navigation.target);
    if (value === undefined || value === null) {
      dto[navigation.name] = navigation.collection ? [] : null;
    } else if (Array.isArray(value)) {
      for (const element of value) {
        fillNavigations(model, target, element as Record<string, unknown>);
      }
    } else {
      fillNavigations(model, target, value as Record<string, unknown>);
    }
  }
}
