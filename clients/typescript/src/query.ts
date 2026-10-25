import {
  type AnyNumberExpr,
  BUILD,
  CollectionExpr,
  type EntityProxy,
  entityProxy,
  Expr,
  type Groupable,
  GroupExpr,
  isBuildable,
  keyProxy,
  MapExpr,
  node,
  type Operand,
  type OrderedExpr,
  PrimitiveCollectionExpr,
  ROOT,
  sameKindAs,
  selectNode,
  type Sortable,
} from "./expressions.js";
import { findSet, type Model, type NavigationModel, type SetModel } from "./model.js";
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

/** A group key: one groupable member, or an object of them for a composite key. */
export type GroupKey = (Expr<unknown> & Groupable) | Readonly<Record<string, Expr<unknown> & Groupable>>;

/** The fields of a group select as `where` and `orderBy` after it see them. */
export type Fields<S> = { readonly [K in keyof S]: S[K] extends Expr<unknown> ? S[K] & Sortable : never };

/** A grouped query before its select: groups are only returned through a select over them. */
export interface GroupedQuery<K, P, D> {
  /** Projects each group: its `key` and aggregates (`count`, `sum`, `min`, `max`, `average`). */
  select<S>(selector: (group: GroupExpr<K, P>) => S): Query<Projected<S>, Fields<S>, D>;
}

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
  | { readonly kind: "groupBy"; readonly key: (proxy: never) => unknown }
  | { readonly kind: "groupSelect"; readonly selector: (group: never) => unknown }
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
  readonly grouped?: boolean;
}

interface Translated {
  readonly filter: QueryNode | undefined;
  readonly orderBy: readonly { member: string; desc?: true }[];
  readonly select: QueryNode | undefined;
  readonly include: readonly string[];
  readonly groupBy: readonly string[] | undefined;
  readonly having: QueryNode | undefined;
}

type Page = { mode: "offset"; skip?: number; take?: number } | { mode: "cursor"; after?: string; take: number };

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
    // After a group select, where filters groups over the selected fields.
    this.require(this.composing() || this.composingGroups(), "where");
    return this.with({ ops: [...this.state.ops, { kind: "where", predicate }] });
  }

  /** Groups the rows by groupable members; follow with `select` over the groups. */
  groupBy<K extends GroupKey>(key: (proxy: P) => K): GroupedQuery<K, P, D> {
    this.require(this.composing() && this.state.grouped !== true, "groupBy");
    const grouped = this.with({ ops: [...this.state.ops, { kind: "groupBy", key }], grouped: true });
    return {
      select: <S>(selector: (group: GroupExpr<K, P>) => S) =>
        grouped.with({
          ops: [...grouped.state.ops, { kind: "groupSelect", selector }],
          selected: true,
        }) as unknown as Query<Projected<S>, Fields<S>, D>,
    };
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

  /**
   * Runs the query and returns one page. Requires `take`, so a server page size can't pass for "all rows"; a grouped
   * query without `take` returns every group, up to the server's limit.
   */
  async toList(options: CallOptions = {}): Promise<T[]> {
    if (this.state.grouped === true && this.state.take === undefined) {
      const groups: Page =
        this.state.skip === undefined ? { mode: "offset" } : { mode: "offset", skip: this.state.skip };
      return (await this.send(groups, options)).items;
    }

    if (this.state.take === undefined) {
      throw new Error("Add take(n) before toList(), or use stream() to read every row.");
    }

    const response = await this.send(this.page(this.state.take), options);
    return response.items;
  }

  /** Returns one keyset page and the cursor of the next one. */
  async toCursorPage(options: CallOptions & { readonly includeTotalCount?: boolean } = {}): Promise<CursorPage<T>> {
    if (this.state.grouped === true) {
      throw new Error("Groups are paged with skip and take, not cursors.");
    }

    const page = this.cursor(this.state.after, this.state.take ?? this.set.pagination.defaultPageSize);
    return this.send(page, options, options.includeTotalCount === true);
  }

  /** Streams every matching row, following cursor pages (`take` caps the total). */
  async *stream(options: CallOptions = {}): AsyncGenerator<T, void, undefined> {
    if (this.state.grouped === true) {
      yield* await this.toList(options);
      return;
    }

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

    // A grouped query keeps its select: having names its fields.
    const unskipped = query.with({ skip: undefined });
    const response = await unskipped.send(
      unskipped.page(0),
      options ?? optionsOf(predicate),
      true,
      query.state.grouped === true ? undefined : NOTHING,
    );
    const count = Math.max(0, (response.totalCount ?? 0) - (query.state.skip ?? 0));
    return query.state.take === undefined ? count : Math.min(count, query.state.take);
  }

  /** Returns whether any row matches. */
  any(options?: CallOptions): Promise<boolean>;
  any(predicate: (proxy: P) => Operand<boolean>, options?: CallOptions): Promise<boolean>;
  async any(predicate?: ((proxy: P) => Operand<boolean>) | CallOptions, options?: CallOptions): Promise<boolean> {
    const query = this.filtered(predicate);
    const take = query.state.take === undefined ? 1 : Math.min(query.state.take, 1);
    const response = await query.send(
      query.page(take),
      options ?? optionsOf(predicate),
      false,
      query.state.grouped === true ? undefined : NOTHING,
    );
    return response.items.length > 0;
  }

  /** Sums a value over every matching row; 0 when none match. The query may only filter. */
  async sum(selector: (proxy: P) => AnyNumberExpr, options: CallOptions = {}): Promise<number> {
    return (await this.aggregate((group) => group.sum(selector), options)) ?? 0;
  }

  /** Averages a value over every matching row; null when none match. The query may only filter. */
  average(selector: (proxy: P) => AnyNumberExpr, options: CallOptions = {}): Promise<number | null> {
    return this.aggregate((group) => group.average(selector), options);
  }

  /** The smallest value over every matching row; null when none match. The query may only filter. */
  min<E extends OrderedExpr<unknown>>(
    selector: (proxy: P) => E,
    options: CallOptions = {},
  ): Promise<NonNullable<E["__value"]> | null> {
    return this.aggregate((group) => group.min(selector), options);
  }

  /** The largest value over every matching row; null when none match. The query may only filter. */
  max<E extends OrderedExpr<unknown>>(
    selector: (proxy: P) => E,
    options: CallOptions = {},
  ): Promise<NonNullable<E["__value"]> | null> {
    return this.aggregate((group) => group.max(selector), options);
  }

  /** The request body this query sends, for debugging and tests. */
  toJSON(): unknown {
    return this.request(this.page(this.state.take ?? this.set.pagination.defaultPageSize), false, undefined);
  }

  private async fetch(limit: number, options: CallOptions): Promise<T[]> {
    const take = this.state.take === undefined ? limit : Math.min(this.state.take, limit);
    return (await this.send(this.page(take), options)).items;
  }

  /** One aggregate over the one group of every matching row (`"groupBy": []`), read as a value. */
  private async aggregate<V>(
    aggregate: (group: GroupExpr<undefined, P>) => unknown,
    options: CallOptions,
  ): Promise<V | null> {
    const filtersOnly = this.state.ops.every((op) => op.kind === "where");
    if (
      !filtersOnly ||
      this.state.selected ||
      this.state.skip !== undefined ||
      this.state.take !== undefined ||
      this.state.after !== undefined
    ) {
      throw new Error("Aggregates apply to the rows a query filters: where only, without select, sorting or paging.");
    }

    const { filter, extra } = this.translateWith((t) => {
      const group = new GroupExpr<undefined, P>(
        undefined,
        (variable) => entityProxy(this.host.model, this.set, variable) as P,
      );
      return node(t, aggregate(group));
    });
    const body: Record<string, unknown> = { v: 1 };
    if (filter.filter !== undefined) body.filter = filter.filter;
    body.groupBy = [];
    body.select = extra;
    body.page = { mode: "offset" };
    const response = (await this.host.transport.query(this.set.name, body, options.signal)) as { items: unknown[] };
    return (response.items[0] ?? null) as V | null;
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
    this.require((this.composing() || this.composingGroups()) && this.state.sorted !== first, name);
    return this.with({ ops: [...this.state.ops, { kind: "orderBy", key, descending }], sorted: true });
  }

  private composing(): boolean {
    return this.state.phase === "compose" && !this.state.selected;
  }

  private composingGroups(): boolean {
    return this.state.phase === "compose" && this.state.selected && this.state.grouped === true;
  }

  private require(allowed: boolean, name: string): void {
    if (!allowed) {
      throw new Error(
        `'${name}' isn't supported at this point. LinqToRest queries are where/include/orderBy/thenBy, then select, with after or skip, then take, anywhere after the sort; each at most once except where (orderBy once, then thenBy).`,
      );
    }
  }

  private with(changes: Partial<State>): Query<T, P, D> {
    return new Query<T, P, D>(this.host, this.set, { ...this.state, ...changes });
  }

  /** A cursor page after `after`, else offset when the set allows it. Groups are always paged by offset. */
  private page(take: number): Page {
    if (this.state.grouped === true) {
      return this.state.skip === undefined ? { mode: "offset", take } : { mode: "offset", skip: this.state.skip, take };
    }

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

  /** Translates the filters, then `extra` with the same translator (variables keep counting). */
  private translateWith(extra: (t: Translator) => QueryNode): { filter: Translated; extra: QueryNode } {
    const t = new Translator(this.set);
    const filter = this.translate(t);
    return { filter, extra: extra(t) };
  }

  private translate(t: Translator = new Translator(this.set)): Translated {
    const root = entityProxy(this.host.model, this.set, null);
    const filters: QueryNode[] = [];
    const having: QueryNode[] = [];
    const orderBy: { member: string; desc?: true }[] = [];
    const include: string[] = [];
    let select: QueryNode | undefined;
    let groupBy: string[] | undefined;
    let key: unknown;
    let fields: Record<string, unknown> | undefined;
    for (const op of this.state.ops) {
      switch (op.kind) {
        case "groupBy": {
          key = op.key(root as never);
          const keys = isBuildable(key) ? [key] : Object.values(key as Record<string, unknown>);
          groupBy = keys.map((part) => {
            const built = isBuildable(part) ? t.within("group", () => part[BUILD](t)) : undefined;
            if (typeof built !== "object" || built === null || !("member" in built)) {
              throw new Error(
                "Group by scalar members, such as p => p.brandId or p => ({ brandId: p.brandId, name: p.brand.name }).",
              );
            }

            return String(built.member);
          });
          break;
        }
        case "groupSelect": {
          const group = new GroupExpr(keyProxy(key), (variable) => entityProxy(this.host.model, this.set, variable));
          const value = op.selector(group as never);
          select = t.within("select", () => selectNode(t, value));
          fields = fieldProxies(value);
          break;
        }
        case "where":
          if (fields !== undefined) {
            having.push(t.within("filter", () => node(t, op.predicate(fields as never))));
          } else {
            filters.push(t.within("filter", () => node(t, op.predicate(root as never))));
          }

          break;
        case "include":
          include.push(this.navigationPath(op.navigation(root as never)));
          break;
        case "orderBy":
          orderBy.push({
            member:
              fields !== undefined ? fieldPath(op.key(fields as never), t) : this.sortPath(op.key(root as never), t),
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

    if (groupBy !== undefined && select === undefined) {
      throw new Error("groupBy needs a select over the groups; LinqToRest doesn't return grouped rows.");
    }

    return { filter: and(filters), orderBy, select, include, groupBy, having: and(having) };
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

  /** The member a path ends with, through reference navigations and values (`category/contact/email`). */
  private resolve(path: string): SetModel["members"][number] | undefined {
    let set: SetModel | undefined = this.set;
    let member: SetModel["members"][number] | undefined;
    for (const segment of path.split("/")) {
      if (member !== undefined) {
        member = member.value?.members.find((candidate) => candidate.name === segment);
        if (member === undefined) {
          return undefined;
        }

        continue;
      }

      if (set === undefined) {
        return undefined;
      }

      member = set.members.find((candidate) => candidate.name === segment);
      const navigation: NavigationModel | undefined = set.navigations.find(
        (candidate) => candidate.name === segment && !candidate.collection,
      );
      set = navigation === undefined ? undefined : findSet(this.host.model, navigation.target);
      if (member === undefined && navigation === undefined) {
        return undefined;
      }
    }

    return member;
  }

  private request(page: Page, count: boolean, select: QueryNode | undefined): Record<string, unknown> {
    const translated = this.translate();
    const body: Record<string, unknown> = { v: 1 };
    if (translated.filter !== undefined) body.filter = translated.filter;
    if (translated.groupBy !== undefined) body.groupBy = translated.groupBy;
    if (translated.orderBy.length > 0) body.orderBy = translated.orderBy;
    const projection = select ?? translated.select;
    if (projection !== undefined) body.select = projection;
    if (translated.having !== undefined) body.having = translated.having;
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

function and(nodes: QueryNode[]): QueryNode | undefined {
  return nodes.length === 0 ? undefined : nodes.length === 1 ? nodes[0] : { and: nodes };
}

/** The fields of a group select, each an expression of the same kind naming the field (`{"member": "count"}`). */
function fieldProxies(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || isBuildable(value)) {
    return {};
  }

  return Object.fromEntries(
    Object.entries(value)
      .filter(([, field]) => field instanceof Expr)
      .map(([name, field]) => [name, sameKindAs(field, () => ({ member: name }))]),
  );
}

function fieldPath(value: unknown, t: Translator): string {
  const built = isBuildable(value) ? value[BUILD](t) : undefined;
  if (typeof built !== "object" || built === null || !("member" in built)) {
    throw new Error("Sort groups by a field of the group select, such as f => f.count.");
  }

  return String(built.member);
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
