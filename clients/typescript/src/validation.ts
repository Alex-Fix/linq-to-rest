import type { MemberKind, MemberModel, MemberRules, SetModel } from "./model.js";

/** One broken rule: the entity, the member's name (undefined for the whole row) and the code. */
export interface RestValidationIssue {
  readonly entity: object;
  readonly member: string | undefined;
  readonly code: string;
}

const patterns = new Map<string, RegExp>();

/**
 * Checks a value against a member's rules in the order the server uses (required, length, range, pattern) and
 * returns the code of the first broken rule: `required`, `too_short`, `too_long`, `out_of_range` or `invalid_format`.
 */
export function checkRules(rules: MemberRules, kind: MemberKind, value: unknown): string | undefined {
  if (value === null || value === undefined) {
    return rules.required === true ? "required" : undefined;
  }

  const length = typeof value === "string" || Array.isArray(value) ? value.length : undefined;
  if (rules.required === true && (length === 0 || (typeof value === "string" && value.trim() === ""))) {
    return "required";
  }

  if (length !== undefined) {
    if (rules.minLength !== undefined && length < rules.minLength) {
      return "too_short";
    }

    if (rules.maxLength !== undefined && length > rules.maxLength) {
      return "too_long";
    }
  }

  if (
    (rules.min !== undefined && compare(kind, value, rules.min) < 0) ||
    (rules.max !== undefined && compare(kind, value, rules.max) > 0)
  ) {
    return "out_of_range";
  }

  if (rules.pattern !== undefined && typeof value === "string") {
    let pattern = patterns.get(rules.pattern);
    if (pattern === undefined) {
      pattern = new RegExp(rules.pattern);
      patterns.set(rules.pattern, pattern);
    }

    if (!pattern.test(value)) {
      return "invalid_format";
    }
  }

  return undefined;
}

/** Checks the writable members of an entity; `members` narrows them (an update checks the members it sends). */
export function checkEntity(
  set: SetModel,
  entity: object,
  members?: (member: MemberModel) => boolean,
): RestValidationIssue[] {
  const issues: RestValidationIssue[] = [];
  for (const member of set.members) {
    if (member.rules === undefined || !member.writable || (members !== undefined && !members(member))) {
      continue;
    }

    const code = checkRules(member.rules, member.kind, (entity as Record<string, unknown>)[member.name]);
    if (code !== undefined) {
      issues.push({ entity, member: member.name, code });
    }
  }

  return issues;
}

// Bounds are wire values of the member's kind: numbers, or date and time strings.
function compare(kind: MemberKind, value: unknown, bound: number | string): number {
  if (typeof value === "number" && typeof bound === "number") {
    return value - bound;
  }

  if (typeof value !== "string" || typeof bound !== "string") {
    return 0;
  }

  switch (kind) {
    case "datetime":
    case "datetimeoffset":
      return Date.parse(value) - Date.parse(bound);
    case "timespan":
      return ticks(value) - ticks(bound);
    case "timeonly":
      return ticks(value) - ticks(bound);
    default:
      // dateonly ("yyyy-MM-dd") sorts as text.
      return value < bound ? -1 : value > bound ? 1 : 0;
  }
}

// "[-][d.]hh:mm[:ss[.fffffff]]" (TimeSpan "c" format, and time of day) as a comparable number.
function ticks(text: string): number {
  const match = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?$/.exec(text);
  if (match === null) {
    return Number.NaN;
  }

  const [, sign, days, hours, minutes, seconds, fraction] = match;
  const total =
    ((Number(days ?? 0) * 24 + Number(hours)) * 60 + Number(minutes)) * 60 +
    Number(seconds ?? 0) +
    Number(`0.${fraction ?? "0"}`);
  return sign === "-" ? -total : total;
}
