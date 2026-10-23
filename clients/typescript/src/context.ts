import type { EntityProxy } from "./expressions.js";
import { findSet, type MemberRules, type Model, type WriteOperation } from "./model.js";
import { Query, type QueryHost } from "./query.js";
import { ChangeTracker, type EntityState, saveChanges } from "./tracking.js";
import { type CallOptions, type ContextOptions, Transport } from "./transport.js";
import { checkEntity, type RestValidationIssue } from "./validation.js";

// The usual type-equality trick: the generic functions only exist to compare X and Y exactly.
// eslint-disable-next-line @typescript-eslint/no-unnecessary-type-parameters
type IfEquals<X, Y, A, B> = (<T>() => T extends X ? 1 : 2) extends <T>() => T extends Y ? 1 : 2 ? A : B;

/** The members of `T` that aren't `readonly` (generated DTOs mark keys, tokens and read-only members readonly). */
export type WritableKeys<T> = {
  // A mapped type over K keeps T's readonly modifier, which Record<K, …> would drop.
  // eslint-disable-next-line @typescript-eslint/consistent-indexed-object-style
  [K in keyof T]-?: IfEquals<{ [Q in K]: T[K] }, { -readonly [Q in K]: T[K] }, K, never>;
}[keyof T];

/** What `add` accepts: the writable members and navigations, each optional. */
export type New<T> = { -readonly [K in WritableKeys<T>]?: T[K] };

/** A set at runtime: every write method exists and checks the set's `writes`; `RestSet` types only the allowed ones. */
export class EntitySet<D, P extends EntityProxy<D>> extends Query<D, P, D> {
  /** Starts tracking a new entity, inserted on save; returns it, typed as the full row (its key comes from the server). */
  add(entity: New<D>): D {
    this.host.tracker.add(entity, this.set);
    return entity as D;
  }

  /** Starts tracking an existing entity as unchanged; later changes are saved. */
  attach(entity: D): void {
    this.host.tracker.attach(entity as object, this.set);
  }

  /** Tracks an existing entity with every writable member marked modified. */
  update(entity: D): void {
    this.host.tracker.update(entity as object, this.set);
  }

  /** Marks an entity deleted on save (an added one is simply forgotten). */
  remove(entity: D): void {
    this.host.tracker.remove(entity as object, this.set);
  }

  /**
   * Checks an entity against the model's built-in rules (every writable member) without sending anything; the
   * server may have more rules of its own.
   */
  validate(entity: D | New<D>): RestValidationIssue[] {
    return checkEntity(this.set, entity as object);
  }

  /** The built-in validation rules of the set's members, for forms (required, lengths, range, pattern). */
  get rules(): Partial<Record<keyof D & string, MemberRules>> {
    return Object.fromEntries(
      this.set.members.flatMap((member) => (member.rules === undefined ? [] : [[member.name, member.rules]])),
    ) as Partial<Record<keyof D & string, MemberRules>>;
  }
}

/**
 * A set of the API: query it, attach entities, and use the writes it allows (`W`) to save with `saveChanges`:
 * `add` needs `"insert"`, `update` needs `"update"`, `remove` needs `"delete"`.
 */
export type RestSet<D, P extends EntityProxy<D>, W extends WriteOperation = never> = Query<D, P, D> &
  Pick<EntitySet<D, P>, "attach" | "validate" | "rules"> &
  ("insert" extends W ? Pick<EntitySet<D, P>, "add"> : unknown) &
  ("update" extends W ? Pick<EntitySet<D, P>, "update"> : unknown) &
  ("delete" extends W ? Pick<EntitySet<D, P>, "remove"> : unknown);

/** The tracking information of one DTO. */
export interface EntityEntry<D> {
  readonly state: EntityState;
  isModified(member: WritableKeys<D> & string): boolean;
  /** Marks a member to send (`true`), or accepts its current value as unchanged (`false`). */
  setModified(member: WritableKeys<D> & string, modified: boolean): void;
}

/** A unit of work over a LinqToRest API. Generated contexts add one `RestSet` per set. */
export interface Context {
  /** Sends every insert, update and delete as one atomic batch; returns the number of operations. */
  saveChanges(options?: CallOptions): Promise<number>;
  entry<D extends object>(dto: D): EntityEntry<D>;
}

/** Creates a context with a `RestSet` property per set of the model. Generated `create<Name>` functions call this. */
export function createContext(model: Model, options: ContextOptions): Context & Record<string, unknown> {
  const host: QueryHost = { model, transport: new Transport(options), tracker: new ChangeTracker(model) };
  const context: Context & Record<string, unknown> = {
    saveChanges: (call: CallOptions = {}) => saveChanges(host.tracker, host.transport, call.signal),
    entry: <D extends object>(dto: D) => entry<D>(host.tracker, dto),
  };
  for (const name of Object.keys(model.sets)) {
    context[name] = new EntitySet(host, findSet(model, name));
  }

  return context;
}

function entry<D extends object>(tracker: ChangeTracker, dto: D): EntityEntry<D> {
  const tracked = (): ReturnType<ChangeTracker["find"]> => tracker.find(dto);
  return {
    get state() {
      const current = tracked();
      if (current === undefined) {
        return "detached";
      }

      tracker.detectChanges(current);
      return current.state;
    },
    isModified(member) {
      const current = tracked();
      if (current === undefined) {
        return false;
      }

      tracker.detectChanges(current);
      return current.modified.has(member);
    },
    setModified(member, modified) {
      const current = tracked();
      if (current === undefined) {
        throw new Error("The DTO isn't tracked.");
      }

      if (!current.set.members.some((candidate) => candidate.name === member && candidate.writable)) {
        throw new Error(`${current.set.name}.${member} isn't writable.`);
      }

      if (modified) {
        current.forced.add(member);
      } else {
        current.forced.delete(member);
        current.snapshot.set(member, (dto as Record<string, unknown>)[member]);
      }
    },
  };
}
