import type { EntityProxy } from "./expressions.js";
import { findSet, type Model } from "./model.js";
import { Query, type QueryHost } from "./query.js";
import { ChangeTracker, type EntityState, saveChanges } from "./tracking.js";
import { type CallOptions, type ContextOptions, Transport } from "./transport.js";

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

/** A set of DTOs: query it, and add, attach, update or remove DTOs to save with `saveChanges`. */
export class DtoSet<D, P extends EntityProxy<D>> extends Query<D, P, D> {
  /** Starts tracking a new DTO, inserted on save; returns it, typed as the full DTO (its key comes from the server). */
  add(dto: New<D>): D {
    this.host.tracker.add(dto, this.set);
    return dto as D;
  }

  /** Starts tracking an existing DTO as unchanged; later changes are saved. */
  attach(dto: D): void {
    this.host.tracker.attach(dto as object, this.set);
  }

  /** Tracks an existing DTO with every writable member marked modified. */
  update(dto: D): void {
    this.host.tracker.update(dto as object, this.set);
  }

  /** Marks a DTO deleted on save (an added one is simply forgotten). */
  remove(dto: D): void {
    this.host.tracker.remove(dto as object, this.set);
  }
}

/** The tracking information of one DTO. */
export interface EntityEntry<D> {
  readonly state: EntityState;
  isModified(member: WritableKeys<D> & string): boolean;
  /** Marks a member to send (`true`), or accepts its current value as unchanged (`false`). */
  setModified(member: WritableKeys<D> & string, modified: boolean): void;
}

/** A unit of work over a LinqToRest API. Generated contexts add one `DtoSet` per set. */
export interface Context {
  /** Sends every insert, update and delete as one atomic batch; returns the number of operations. */
  saveChanges(options?: CallOptions): Promise<number>;
  entry<D extends object>(dto: D): EntityEntry<D>;
}

/** Creates a context with a `DtoSet` property per set of the model. Generated `create<Name>` functions call this. */
export function createContext(model: Model, options: ContextOptions): Context & Record<string, unknown> {
  const host: QueryHost = { model, transport: new Transport(options), tracker: new ChangeTracker(model) };
  const context: Context & Record<string, unknown> = {
    saveChanges: (call: CallOptions = {}) => saveChanges(host.tracker, host.transport, call.signal),
    entry: <D extends object>(dto: D) => entry<D>(host.tracker, dto),
  };
  for (const name of Object.keys(model.sets)) {
    context[name] = new DtoSet(host, findSet(model, name));
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
