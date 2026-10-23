import { RestConcurrencyError, RestHttpError, RestQueryError, RestSaveError } from "linqtorest";

/** A message for a failed call: the problem's stable code and where in the request it points, never raw values. */
export function describe(error: unknown): string {
  if (error instanceof RestConcurrencyError) {
    return "Someone else changed this since you loaded it.";
  }

  if (error instanceof RestSaveError || error instanceof RestQueryError) {
    const messages: Record<string, string> = {
      forbidden: "You aren't allowed to do that. Sign in as an editor.",
      operation_not_allowed: "This kind of change isn't allowed on this set.",
      conflict: "The database refused the change (for example a duplicate SKU, or a category that still has products).",
      not_found: "It no longer exists, or you can't see it.",
    };
    return `${messages[error.code] ?? "The server rejected the request."} (${error.code} at ${error.location ?? "/"})`;
  }

  if (error instanceof RestHttpError) {
    return `The server answered ${String(error.status)}.`;
  }

  return error instanceof Error ? error.message : String(error);
}
