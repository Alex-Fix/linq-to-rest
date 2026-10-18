/** The server rejected a query. `code` is one of RestToLinq's stable error codes. */
export class RestQueryError extends Error {
  override readonly name = "RestQueryError";

  constructor(
    readonly status: number,
    readonly code: string,
    readonly location: string | undefined,
    readonly limit: string | undefined,
  ) {
    super(`The query was rejected: ${code}${limit === undefined ? "" : ` (${limit})`} at '${location ?? ""}'.`);
  }
}

/** The server rejected a save. Nothing was written and the tracked state is unchanged. */
export class RestSaveError extends Error {
  override readonly name: string = "RestSaveError";

  constructor(
    readonly status: number,
    readonly code: string,
    readonly location: string | undefined,
    /** The tracked DTOs whose operations were rejected. */
    readonly entities: readonly object[],
  ) {
    super(`The changes were rejected: ${code} at '${location ?? ""}'.`);
  }
}

/** Rows changed since they were read. Reload them and retry. */
export class RestConcurrencyError extends RestSaveError {
  override readonly name = "RestConcurrencyError";
}

/** The API answered with an error that isn't a RestToLinq problem response. */
export class RestHttpError extends Error {
  override readonly name = "RestHttpError";

  constructor(readonly status: number) {
    super(`The RestToLinq API answered ${String(status)} without a problem response.`);
  }
}

export interface Problem {
  readonly code: string;
  readonly pointer: string | undefined;
  readonly limit: string | undefined;
  readonly operationIds: readonly string[];
}

/** Reads a `application/problem+json` body, or undefined when the response isn't one. */
export async function readProblem(response: Response): Promise<Problem | undefined> {
  if (!(response.headers.get("content-type") ?? "").startsWith("application/problem+json")) {
    return undefined;
  }

  const body = (await response.json()) as Record<string, unknown>;
  const text = (name: string): string | undefined => (typeof body[name] === "string" ? body[name] : undefined);
  const code = text("code");
  if (code === undefined) {
    return undefined;
  }

  const many = body.operationIds;
  const one = text("operationId");
  const operationIds = Array.isArray(many)
    ? many.filter((id): id is string => typeof id === "string")
    : one === undefined
      ? []
      : [one];
  return { code, pointer: text("pointer"), limit: text("limit"), operationIds };
}
