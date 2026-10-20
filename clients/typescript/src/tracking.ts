import { readProblem, RestConcurrencyError, RestHttpError, RestSaveError } from "./errors.js";
import { findSet, requireWrite, type Model, type NavigationModel, type SetModel } from "./model.js";
import type { Transport } from "./transport.js";

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
function snapshotOf(entity: Dto, members: readonly { readonly name: string }[]): Map<string, unknown> {
  const snapshot = new Map<string, unknown>();
  for (const member of members) {
    snapshot.set(member.name, copy(entity[member.name]));
  }

  return snapshot;
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

function keyOf(dto: Dto, set: SetModel): string {
  return `${set.name}:${JSON.stringify(set.key.map((member) => dto[member]))}`;
}

/** Identity map and snapshot-based change detection for the DTOs of one context. */
export class ChangeTracker {
  private readonly entries = new Map<object, Entry>();
  private readonly identity = new Map<string, Entry>();

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

    this.entries.set(entity, new Entry(entity as Dto, set, "added"));
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
  }

  private detect(entry: Entry): void {
    if (entry.state === "deleted" || entry.state === "detached") {
      return;
    }

    this.syncForeignKeys(entry);
    if (entry.state === "added") {
      return;
    }

    for (const navigation of entry.set.navigations.filter((candidate) => candidate.collection)) {
      if (!same(entry.entity[navigation.name], entry.navigationSnapshot.get(navigation.name))) {
        throw new Error(
          `Items were added to or removed from ${entry.set.name}.${navigation.name}. Set the foreign key of the child DTO instead.`,
        );
      }
    }

    entry.modified.clear();
    for (const member of entry.set.members) {
      if (
        member.writable &&
        (entry.forced.has(member.name) || !same(entry.entity[member.name], entry.snapshot.get(member.name)))
      ) {
        entry.modified.add(member.name);
      }
    }

    entry.state = entry.modified.size > 0 || entry.references.size > 0 ? "modified" : "unchanged";
  }

  /** Copies the key of a referenced DTO into the foreign key, or records a ref to an added one. */
  private syncForeignKeys(entry: Entry): void {
    for (const navigation of entry.set.navigations) {
      if (navigation.collection || navigation.foreignKey === null) {
        continue;
      }

      this.syncForeignKey(entry, navigation, navigation.foreignKey);
    }
  }

  private syncForeignKey(entry: Entry, navigation: NavigationModel, foreignKey: string): void {
    const target = entry.entity[navigation.name];
    const original = entry.navigationSnapshot.get(navigation.name);
    const member = entry.set.members.find((candidate) => candidate.name === foreignKey);
    if (member === undefined) {
      throw new Error(`${entry.set.name}.${foreignKey} is a foreign key but not a member of the model.`);
    }

    if (target === null || target === undefined) {
      entry.references.delete(foreignKey);
      if (original !== null && original !== undefined && member.nullable) {
        entry.entity[foreignKey] = null;
      }

      return;
    }

    if (target === original && !entry.references.has(foreignKey)) {
      return;
    }

    const targetEntry = this.find(target);
    if (targetEntry === undefined) {
      throw new Error(
        `${entry.set.name}.${navigation.name} points at a DTO this context doesn't track. Add or attach it first.`,
      );
    }

    if (targetEntry.state === "added") {
      entry.references.set(foreignKey, targetEntry);
    } else {
      entry.references.delete(foreignKey);
      entry.entity[foreignKey] = targetEntry.key?.[0];
    }
  }
}

interface Operation {
  id: string;
  op: "insert" | "update" | "delete";
  set: string;
  key?: Record<string, unknown>;
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
  if (ordered.length === 0) {
    return 0;
  }

  const ids = new Map<Entry, string>(ordered.map((entry, index) => [entry, `o${String(index)}`]));
  const body = { v: 1, operations: ordered.map((entry) => operation(entry, ids)) };
  const response = await transport.send("$batch", "POST", body, signal);
  const byId = new Map([...ids].map(([entry, id]) => [id, entry]));
  if (!response.ok) {
    const problem = await readProblem(response);
    if (problem === undefined) {
      throw new RestHttpError(response.status);
    }

    const entities = problem.operationIds.map((id) => byId.get(id)?.entity).filter((entity) => entity !== undefined);
    throw response.status === 412
      ? new RestConcurrencyError(response.status, problem.code, problem.pointer, entities)
      : new RestSaveError(response.status, problem.code, problem.pointer, entities);
  }

  const result = (await response.json()) as { results: { id: string; value?: Dto }[] };
  for (const item of result.results) {
    const entry = byId.get(item.id);
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

  return ordered.length;
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
    result.key = Object.fromEntries(set.key.map((member, index) => [member, entry.key?.[index]]));
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
        .map((member) => [member.name, entry.entity[member.name]]),
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
