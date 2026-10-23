import {
  type MemberRules,
  RestConcurrencyError,
  RestHttpError,
  RestQueryError,
  RestSaveError,
  RestValidationError,
} from "linqtorest";

/** A message for a failed call: the problem's stable code and where in the request it points, never raw values. */
export function describe(error: unknown): string {
  if (error instanceof RestConcurrencyError) {
    return "Someone else changed this since you loaded it.";
  }

  if (error instanceof RestValidationError) {
    const fields = error.errors.map((issue) => `${issue.member ?? "row"}: ${fieldMessage(issue.code, undefined)}`);
    return `${error.isLocal ? "Some values aren't valid" : "The server rejected some values"} (${fields.join(" ")})`;
  }

  if (error instanceof RestSaveError || error instanceof RestQueryError) {
    const messages: Record<string, string> = {
      forbidden: "You aren't allowed to do that. Sign in as an editor.",
      operation_not_allowed: "This kind of change isn't allowed on this set.",
      conflict: "The database refused the change (for example a category that still has products).",
      not_found: "It no longer exists, or you can't see it.",
    };
    return `${messages[error.code] ?? "The server rejected the request."} (${error.code} at ${error.location ?? "/"})`;
  }

  if (error instanceof RestHttpError) {
    return `The server answered ${String(error.status)}.`;
  }

  return error instanceof Error ? error.message : String(error);
}

/**
 * A field's message for a validation code: built-in codes read the member's rules (so limits come from the server's
 * model), the catalog API's own codes are spelled out, anything else shows the code.
 */
export function fieldMessage(code: string, rules: MemberRules | undefined): string {
  switch (code) {
    case "required":
      return "Required.";
    case "too_short":
      return rules?.minLength === undefined ? "Too short." : `At least ${String(rules.minLength)} characters.`;
    case "too_long":
      return rules?.maxLength === undefined ? "Too long." : `At most ${String(rules.maxLength)} characters.`;
    case "out_of_range":
      return rules?.min !== undefined && rules.max !== undefined
        ? `Between ${String(rules.min)} and ${String(rules.max)}.`
        : rules?.min !== undefined
          ? `At least ${String(rules.min)}.`
          : rules?.max !== undefined
            ? `At most ${String(rules.max)}.`
            : "Out of range.";
    case "invalid_format":
      return "Doesn't have the expected format.";
    case "duplicate":
      return "Already taken by another product.";
    case "too_precise":
      return "Use whole cents (at most two decimals).";
    default:
      return code;
  }
}
