import type { SetModel } from "./model.js";

/** Where a member is used, which decides the flag the client checks before sending. */
export type Use = "filter" | "select" | "sort" | "group" | "aggregate";

/**
 * Translates one request. Lambdas are recorded as functions and only run here, so lambda variables are allocated in
 * the same order as the .NET client does (outer collection first, then its filters, then its body), which keeps both
 * clients' JSON identical.
 */
export class Translator {
  private next = 0;
  private readonly reserved: ReadonlySet<string>;

  constructor(root: SetModel) {
    this.reserved = new Set([...root.members.map((member) => member.name), ...root.navigations.map((n) => n.name)]);
  }

  use: Use = "filter";

  /** Whether an aggregate over a group is being built, where other collection lambdas aren't allowed. */
  inGroupAggregate = false;

  allocate(): string {
    let name: string;
    do {
      name = `v${String(this.next++)}`;
    } while (this.reserved.has(name));

    return name;
  }

  /** Runs `action` with another member use, restoring the previous one. */
  within<T>(use: Use, action: () => T): T {
    const previous = this.use;
    this.use = use;
    try {
      return action();
    } finally {
      this.use = previous;
    }
  }
}
