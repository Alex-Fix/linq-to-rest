import { readProblem, RestConcurrencyError, RestHttpError, RestSaveError, RestValidationError } from "./errors.js";
import {
  findSet,
  requireWrite,
  type Model,
  type NavigationModel,
  type SetModel,
  type ValueTypeModel,
} from "./model.js";
import type { Transport } from "./transport.js";
import { checkEntity } from "./validation.js";

/** The tracking state of a DTO. */
export type EntityState = "detached" | "unchanged" | "added" | "modified" | "deleted";

type Dto = Record<string, unknown>;

/** The tracking information of one DTO. */
export class Entry {
  state: EntityState;
  key: readonly unknown[] | undefined;
  snapshot = new Map<string, unknown>();
  navigationSnapshot = new Map<string, unknown>();
  /** Members marked modified explicitly (update, setModified). */
  readonly forced = new Set<string>();
  readonly modified = new Set<string>();
  /** Foreign keys whose value is the key of an added DTO, known only after the save. */
  readonly references = new Map<string, Entry>();

  constructor(
    readonly entity: Dto,
    readonly set: SetModel,
    state: EntityState,
  ) {
    this.state = state;
  }

  takeSnapshot(): void {
    this.snapshot = snapshotOf(this.entity, this.set.members);
    this.navigationSnapshot = snapshotOf(this.entity, this.set.navigations);
  }
}

// A loop rather than new Map(members.map(...)): no [name, value] pair per member and DTO.
function snapshotOf(
  entity: Dto,
  members: readonly { readonly name: string; readonly value?: ValueTypeModel }[],
): Map<string, unknown> {
  const snapshot = new Map<string, unknown>();
  for (const member of members) {
    const value = entity[member.name];
    snapshot.set(member.name, member.value === undefined ? copy(value) : valueSnapshot(member.value, value));
  }

  return snapshot;
}

/** A value's members (nested values too) in model order, so a value changed in place is seen as well as a replaced one. */
function valueSnapshot(valueType: ValueTypeModel, value: unknown): unknown {
  if (value === null || value === undefined) {
    return value;
  }

  const object = value as Dto;
  return valueType.members.map((member) =>
    member.value === undefined ? object[member.name] : valueSnapshot(member.value, object[member.name]),
  );
}

function sameSnapshot(current: unknown, snapshot: unknown): boolean {
  return Array.isArray(current) && Array.isArray(snapshot)
    ? current.length === snapshot.length && current.every((item, index) => sameSnapshot(item, snapshot[index]))
    : current === snapshot;
}

/** A value as batches send it: every member, in model order (both clients write the same JSON). */
function valueBody(valueType: ValueTypeModel, value: unknown): unknown {
  if (value === null || value === undefined) {
    return null;
  }

  const object = value as Dto;
  return Object.fromEntries(
    valueType.members.map((member) => [
      member.name,
      member.value === undefined ? (object[member.name] ?? null) : valueBody(member.value, object[member.name]),
    ]),
  );
}

function copy(value: unknown): unknown {
  return Array.isArray(value) ? [...(value as unknown[])] : value;
}

function same(current: unknown, snapshot: unknown): boolean {
  if (Array.isArray(current) || Array.isArray(snapshot)) {
    return (
      Array.isArray(current) &&
      Array.isArray(snapshot) &&
      current.length === snapshot.length &&
      current.every((item, index) => item === snapshot[index])
    );
  }

  return current === snapshot;
}

/** Whether a collection holds the same items as its snapshot, in any order. */
function sameMembers(current: unknown, snapshot: unknown): boolean {
  const now = members(current);
  const before = members(snapshot);
  return now.size === before.size && [...now].every((item) => before.has(item));
}

function members(collection: unknown): Set<unknown> {
  return new Set(
    Array.isArray(collection) ? (collection as unknown[]).filter((item) => item !== null && item !== undefined) : [],
  );
}

/** An item added to (or removed from) a linkable collection of a tracked parent. */
export interface PendingLink {
  readonly parent: Entry;
  readonly navigation: NavigationModel;
  readonly item: Entry;
  readonly unlink: boolean;
}

function keyOf(dto: Dto, set: SetModel): string {
  return `${set.name}:${JSON.stringify(set.key.map((member) => dto[member]))}`;
}

/** Identity map and snapshot-based change detection for the DTOs of one context. */
export class ChangeTracker {
  private readonly entries = new Map<object, Entry>();
  private readonly identity = new Map<string, Entry>();
  /** The collection items added and removed since the snapshots, as found by `detectChanges()`. */
  links: PendingLink[] = [];

  constructor(private readonly model: Model) {}

  find(entity: object): Entry | undefined {
    return this.entries.get(entity);
  }

  /** Returns the tracked instance for a queried row, tracking it (and its loaded navigations) if it's new. */
  trackQueried(entity: object, set: SetModel): object {
    const dto = entity as Dto;
    const existing = this.identity.get(keyOf(dto, set));
    if (existing !== undefined) {
      return existing.entity;
    }

    for (const navigation of set.navigations) {
      const target = findSet(this.model, navigation.target);
      const value = dto[navigation.name];
      if (navigation.collection && Array.isArray(value)) {
        dto[navigation.name] = value.map((item: object) => this.trackQueried(item, target));
      } else if (!navigation.collection && value !== null && value !== undefined) {
        dto[navigation.name] = this.trackQueried(value, target);
      }
    }

    const entry = new Entry(dto, set, "unchanged");
    entry.key = set.key.map((member) => dto[member]);
    entry.takeSnapshot();
    this.entries.set(entity, entry);
    this.identity.set(keyOf(dto, set), entry);
    return entity;
  }

  add(entity: object, set: SetModel): void {
    requireWrite(set, "insert", "inserts");
    const existing = this.entries.get(entity);
    if (existing !== undefined) {
      throw new Error(`This ${set.name} DTO is already tracked as ${existing.state}.`);
    }

    // Collections start empty, as on queried rows, so items can be pushed onto a new parent.
    const dto = entity as Dto;
    for (const navigation of set.navigations) {
      if (navigation.collection && dto[navigation.name] === undefined) {
        dto[navigation.name] = [];
      }
    }

    this.entries.set(entity, new Entry(dto, set, "added"));
  }

  attach(entity: object, set: SetModel): Entry {
    const existing = this.entries.get(entity);
    if (existing !== undefined) {
      return existing;
    }

    const dto = entity as Dto;
    const key = keyOf(dto, set);
    if (this.identity.has(key)) {
      throw new Error(`Another ${set.name} DTO with the same key is already tracked.`);
    }

    const entry = new Entry(dto, set, "unchanged");
    entry.key = set.key.map((member) => dto[member]);
    entry.takeSnapshot();
    this.entries.set(entity, entry);
    this.identity.set(key, entry);
    return entry;
  }

  update(entity: object, set: SetModel): void {
    requireWrite(set, "update", "updates");
    const entry = this.attach(entity, set);
    for (const member of set.members.filter((candidate) => candidate.writable)) {
      entry.forced.add(member.name);
    }
  }

  remove(entity: object, set: SetModel): void {
    const tracked = this.entries.get(entity);
    if (tracked?.state === "added") {
      this.detach(tracked);
      return;
    }

    requireWrite(set, "delete", "deletes");
    this.attach(entity, set).state = "deleted";
  }

  detach(entry: Entry): void {
    this.entries.delete(entry.entity);
    if (entry.key !== undefined) {
      this.identity.delete(keyOf(entry.entity, entry.set));
    }

    entry.state = "detached";
  }

  /** Marks a saved entry unchanged with its new key and values. */
  acceptSaved(entry: Entry): void {
    if (entry.key === undefined) {
      entry.key = entry.set.key.map((member) => entry.entity[member]);
      this.identity.set(keyOf(entry.entity, entry.set), entry);
    }

    entry.state = "unchanged";
    entry.forced.clear();
    entry.modified.clear();
    entry.references.clear();
    entry.takeSnapshot();
  }

  all(): Entry[] {
    return [...this.entries.values()];
  }

  detectChanges(entry?: Entry): void {
    for (const current of entry === undefined ? this.entries.values() : [entry]) {
      this.detect(current);
    }

    if (entry === undefined) {
      this.links = [];
      for (const current of [...this.entries.values()]) {
        this.detectLinks(current);
      }
    }
  }

  /** After a save: an item linked to a one-to-many collection points at its parent, a detached one at nothing, a deleted one is gone. */
  acceptLinked({ parent, navigation, item, unlink }: PendingLink): void {
    if (navigation.links === "oneToMany") {
      if (unlink && navigation.onUnlink === "delete") {
        this.detach(item);
        return;
      }

      // The item's own reference back to the parent, if its set exposes one with the foreign key.
      const back = item.set.navigations.filter(
        (candidate) => !candidate.collection && candidate.target === parent.set.name && candidate.foreignKey.length > 0,
      );
      if (back.length === 1 && back[0] !== undefined) {
        const reference = back[0];
        reference.foreignKey.forEach((member, index) => {
          item.entity[member] = unlink ? null : parent.key?.[index];
        });
        item.entity[reference.name] = unlink ? null : parent.entity;
      }

      item.takeSnapshot();
    }

    parent.takeSnapshot();
  }

  /** Items added to or removed from linkable collections; unlinkable ones were checked by `detect`. */
  private detectLinks(entry: Entry): void {
    if (entry.state === "deleted" || entry.state === "detached") {
      return;
    }

    for (const navigation of entry.set.navigations) {
      if (!navigation.collection || navigation.links === null) {
        continue;
      }

      const before = members(entry.navigationSnapshot.get(navigation.name));
      const now = members(entry.entity[navigation.name]);
      for (const item of now) {
        if (!before.has(item)) {
          this.addLink(entry, navigation, item, false);
        }
      }

      for (const item of before) {
        if (!now.has(item)) {
          this.addLink(entry, navigation, item, true);
        }
      }
    }
  }

  private addLink(parent: Entry, navigation: NavigationModel, item: unknown, unlink: boolean): void {
    const entry = this.find(item as object);
    if (entry === undefined && !unlink) {
      throw new Error(
        `${parent.set.name}.${navigation.name} holds a DTO this context doesn't track. Add or attach it first.`,
      );
    }

    // A deleted or detached item needs no link: deleting it (or not saving it) covers the collection too.
    if (entry !== undefined && entry.state !== "deleted" && entry.state !== "detached") {
      this.links.push({ parent, navigation, item: entry, unlink });
    }
  }

  private detect(entry: Entry): void {
    if (entry.state === "deleted" || entry.state === "detached") {
      return;
    }

    this.syncForeignKeys(entry);
    if (entry.state === "added") {
      return;
    }

    for (const navigation of entry.set.navigations) {
      if (
        navigation.collection &&
        navigation.links === null &&
        !sameMembers(entry.entity[navigation.name], entry.navigationSnapshot.get(navigation.name))
      ) {
        throw new Error(
          `Items were added to or removed from ${entry.set.name}.${navigation.name}, which doesn't allow links. Set the foreign key of the child DTO instead.`,
        );
      }
    }

    entry.modified.clear();
    for (const member of entry.set.members) {
      if (
        member.writable &&
        (entry.forced.has(member.name) ||
          !(member.value === undefined
            ? same(entry.entity[member.name], entry.snapshot.get(member.name))
            : sameSnapshot(valueSnapshot(member.value, entry.entity[member.name]), entry.snapshot.get(member.name))))
      ) {
        entry.modified.add(member.name);
      }
    }

    entry.state = entry.modified.size > 0 || entry.references.size > 0 ? "modified" : "unchanged";
  }

  /** Copies the key of a referenced DTO into the foreign key, or records refs to an added one. */
  private syncForeignKeys(entry: Entry): void {
    for (const navigation of entry.set.navigations) {
      if (navigation.collection || navigation.foreignKey.length === 0) {
        continue;
      }

      this.syncForeignKey(entry, navigation, navigation.foreignKey);
    }
  }

  private syncForeignKey(entry: Entry, navigation: NavigationModel, foreignKey: readonly string[]): void {
    const target = entry.entity[navigation.name];
    const original = entry.navigationSnapshot.get(navigation.name);
    const keyMembers = foreignKey.map((name) => {
      const member = entry.set.members.find((candidate) => candidate.name === name);
      if (member === undefined) {
        throw new Error(`${entry.set.name}.${name} is a foreign key but not a member of the model.`);
      }

      return member;
    });

    if (target === null || target === undefined) {
      for (const member of keyMembers) {
        entry.references.delete(member.name);
        if (original !== null && original !== undefined && member.nullable) {
          entry.entity[member.name] = null;
        }
      }

      return;
    }

    if (target === original && !entry.references.has(foreignKey[0] ?? "")) {
      return;
    }

    const targetEntry = this.find(target);
    if (targetEntry === undefined) {
      throw new Error(
        `${entry.set.name}.${navigation.name} points at a DTO this context doesn't track. Add or attach it first.`,
      );
    }

    foreignKey.forEach((member, index) => {
      if (targetEntry.state === "added") {
        entry.references.set(member, targetEntry);
      } else {
        entry.references.delete(member);
        entry.entity[member] = targetEntry.key?.[index];
      }
    });
  }
}

interface Operation {
  id: string;
  op: "insert" | "update" | "delete" | "link" | "unlink";
  set: string;
  key?: Record<string, unknown>;
  ref?: string;
  navigation?: string;
  item?: { key?: Record<string, unknown>; ref?: string; token?: unknown };
  values?: Record<string, unknown>;
  refs?: Record<string, string>;
  token?: unknown;
}

/** Sends every change as one atomic batch and applies the results; see `Context.saveChanges`. */
export async function saveChanges(
  tracker: ChangeTracker,
  transport: Transport,
  signal: AbortSignal | undefined,
): Promise<number> {
  tracker.detectChanges();
  for (const entry of tracker.all().filter((candidate) => candidate.state === "modified")) {
    requireWrite(entry.set, "update", "updates");
  }

  const ordered = order(
    tracker.all().filter((entry) => entry.state === "added" || entry.state === "modified" || entry.state === "deleted"),
  );
  const links = [...tracker.links];
  if (ordered.length === 0 && links.length === 0) {
    return 0;
  }

  // The model's built-in rules, before anything is sent; the server checks them (and its own) again. Inserts check
  // every writable member, updates the members they send; members set through a new row's key are known only later.
  const local = ordered
    .filter((entry) => entry.state === "added" || entry.state === "modified")
    .flatMap((entry) =>
      checkEntity(
        entry.set,
        entry.entity,
        (member) => !entry.references.has(member.name) && (entry.state === "added" || entry.modified.has(member.name)),
      ),
    );
  if (local.length > 0) {
    throw new RestValidationError(422, "/operations", local, true, false);
  }

  // Inserts and updates, then links (their rows exist by then), then deletes.
  const writes = ordered.filter((entry) => entry.state !== "deleted");
  const deletes = ordered.filter((entry) => entry.state === "deleted");
  const ids = new Map<Entry, string>(writes.map((entry, index) => [entry, `o${String(index)}`]));
  const linkIds = links.map((_, index) => `o${String(writes.length + index)}`);
  deletes.forEach((entry, index) => ids.set(entry, `o${String(writes.length + links.length + index)}`));
  const body = {
    v: 1,
    operations: [
      ...writes.map((entry) => operation(entry, ids)),
      ...links.map((link, index) => linkOperation(link, linkIds[index] ?? "", ids)),
      ...deletes.map((entry) => operation(entry, ids)),
    ],
  };
  const response = await transport.send("$batch", "POST", body, signal);
  // A failing link is reported on its parent.
  const byId = new Map([
    ...[...ids].map(([entry, id]) => [id, entry] as const),
    ...links.map((link, index) => [linkIds[index] ?? "", link.parent] as const),
  ]);
  // Pointers name operations by position, so the entries go in the order the operations were sent.
  const sent = [...writes, ...links.map((link) => link.parent), ...deletes];
  if (!response.ok) {
    const problem = await readProblem(response);
    if (problem === undefined) {
      throw new RestHttpError(response.status);
    }

    if (problem.issues !== undefined) {
      const errors = problem.issues.flatMap((issue) => {
        const entry = issue.operation === undefined ? undefined : byId.get(issue.operation);
        // Members inside values come as slash paths (address/city); errorsFor uses dots, like form libraries.
        const member = issue.member?.replaceAll("/", ".");
        return entry === undefined ? [] : [{ entity: entry.entity, member, code: issue.code }];
      });
      throw new RestValidationError(
        response.status,
        problem.pointer,
        errors,
        false,
        problem.issues.some((issue) => issue.code === "too_many_errors"),
      );
    }

    const entities = problem.operationIds.map((id) => byId.get(id)?.entity).filter((entity) => entity !== undefined);
    throw response.status === 412
      ? new RestConcurrencyError(response.status, problem.code, problem.pointer, entities)
      : new RestSaveError(response.status, problem.code, problem.pointer, entities, memberOf(problem.pointer, sent));
  }

  const result = (await response.json()) as { results: { id: string; value?: Dto }[] };
  const linkIdSet = new Set(linkIds);
  for (const item of result.results) {
    const entry = linkIdSet.has(item.id) ? undefined : byId.get(item.id);
    if (entry === undefined) {
      continue;
    }

    if (entry.state === "deleted") {
      tracker.detach(entry);
      continue;
    }

    if (item.value !== undefined) {
      for (const member of entry.set.members) {
        if (member.name in item.value) {
          entry.entity[member.name] = item.value[member.name];
        }
      }
    }

    tracker.acceptSaved(entry);
  }

  // Parents have their keys now, so linked items can point at them.
  for (const link of links) {
    tracker.acceptLinked(link);
  }

  return ordered.length + links.length;
}

/** A link or unlink: parent and item by key, or by the id of their insert in this batch. */
function linkOperation(
  { parent, navigation, item, unlink }: PendingLink,
  id: string,
  ids: Map<Entry, string>,
): Operation {
  const token =
    navigation.links === "oneToMany" && item.state !== "added" && item.set.token !== null
      ? item.snapshot.get(item.set.token)
      : undefined;
  return {
    id,
    op: unlink ? "unlink" : "link",
    set: parent.set.name,
    ...(parent.state === "added" ? { ref: idOf(ids, parent) } : { key: keyBody(parent) }),
    navigation: navigation.name,
    item: {
      ...(item.state === "added" ? { ref: idOf(ids, item) } : { key: keyBody(item) }),
      ...(token === undefined ? {} : { token }),
    },
  };
}

function keyBody(entry: Entry): Record<string, unknown> {
  return Object.fromEntries(entry.set.key.map((member, index) => [member, entry.key?.[index]]));
}

/**
 * The member a pointer such as `/operations/2/values/price` names, if it names one of that operation's set (a dotted
 * path such as `address.city` inside a value).
 */
function memberOf(pointer: string | undefined, ordered: readonly Entry[]): string | undefined {
  const match = /^\/operations\/(\d+)\/values\/([A-Za-z0-9]+(?:\/[A-Za-z0-9]+)*)$/.exec(pointer ?? "");
  const entry = match === null ? undefined : ordered[Number(match[1])];
  const path = match?.[2]?.split("/") ?? [];
  let members = entry?.set.members;
  for (const segment of path) {
    const member = members?.find((candidate) => candidate.name === segment);
    if (member === undefined) {
      return undefined;
    }

    members = member.value?.members;
  }

  return path.length === 0 ? undefined : path.join(".");
}

/** Inserts first (each after the inserts it references), then updates, then deletes. */
function order(entries: Entry[]): Entry[] {
  const ordered: Entry[] = [];
  const visiting = new Set<Entry>();
  const visit = (entry: Entry): void => {
    if (ordered.includes(entry)) {
      return;
    }

    if (visiting.has(entry)) {
      throw new Error(`New ${entry.set.name} DTOs reference each other in a cycle; save them in separate batches.`);
    }

    visiting.add(entry);
    for (const target of entry.references.values()) {
      if (target.state === "added") {
        visit(target);
      }
    }

    ordered.push(entry);
  };

  entries.filter((entry) => entry.state === "added").forEach(visit);
  ordered.push(
    ...entries.filter((entry) => entry.state === "modified"),
    ...entries.filter((entry) => entry.state === "deleted"),
  );
  return ordered;
}

function operation(entry: Entry, ids: Map<Entry, string>): Operation {
  const set = entry.set;
  const result: Operation = {
    id: idOf(ids, entry),
    op: entry.state === "added" ? "insert" : entry.state === "modified" ? "update" : "delete",
    set: set.name,
  };

  if (entry.state !== "added") {
    result.key = keyBody(entry);
  }

  if (entry.state !== "deleted") {
    // Inserts send the writable members present on the object; updates the modified ones.
    const written = set.members.filter((member) =>
      entry.state === "added"
        ? member.writable && entry.entity[member.name] !== undefined
        : entry.modified.has(member.name),
    );
    result.values = Object.fromEntries(
      written
        .filter((member) => !entry.references.has(member.name))
        .map((member) => [
          member.name,
          member.value === undefined ? entry.entity[member.name] : valueBody(member.value, entry.entity[member.name]),
        ]),
    );
  }

  if (entry.references.size > 0) {
    result.refs = Object.fromEntries([...entry.references].map(([member, target]) => [member, idOf(ids, target)]));
  }

  if (entry.state !== "added" && set.token !== null) {
    result.token = entry.snapshot.get(set.token);
  }

  return result;
}

function idOf(ids: Map<Entry, string>, entry: Entry): string {
  const id = ids.get(entry);
  if (id === undefined) {
    throw new Error(`A ${entry.set.name} DTO referenced by the batch isn't part of it.`);
  }

  return id;
}
