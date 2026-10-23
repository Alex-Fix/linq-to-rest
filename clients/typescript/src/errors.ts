import type { RestValidationIssue } from "./validation.js";

/** The server rejected a query. `code` is one of LinqToRest's stable error codes. */
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
    /** The member the error is about (for example `price` for `not_writable`), or undefined for a row or the batch. */
    readonly member?: string,
  ) {
    super(`The changes were rejected: ${code} at '${location ?? ""}'.`);
  }
}

/**
 * Values break validation rules (code `validation_failed`): found before sending (`isLocal`, the model's built-in
 * rules), or by the server (422). Nothing was written and the tracked state is unchanged.
 */
export class RestValidationError extends RestSaveError {
  override readonly name = "RestValidationError";

  constructor(
    status: number,
    location: string | undefined,
    /** Every broken rule, in batch order. */
    readonly errors: readonly RestValidationIssue[],
    /** Whether the client found the errors before sending. */
    readonly isLocal: boolean,
    /** Whether the server found more errors than it lists. */
    readonly isTruncated: boolean,
  ) {
    super(status, "validation_failed", location, [...new Set(errors.map((error) => error.entity))]);
  }

  /** The error codes of one entity by member; errors about the whole row are under `""`. */
  errorsFor(entity: object): Record<string, string[]> {
    const result: Record<string, string[]> = {};
    for (const error of this.errors.filter((candidate) => candidate.entity === entity)) {
      (result[error.member ?? ""] ??= []).push(error.code);
    }

    return result;
  }
}

/** Rows changed since they were read. Reload them and retry. */
export class RestConcurrencyError extends RestSaveError {
  override readonly name = "RestConcurrencyError";
}

/** The API answered with an error that isn't a LinqToRest problem response. */
export class RestHttpError extends Error {
  override readonly name = "RestHttpError";

  constructor(readonly status: number) {
    super(`The LinqToRest API answered ${String(status)} without a problem response.`);
  }
}

/** One entry of a `validation_failed` problem's `errors`. */
export interface ProblemIssue {
  readonly operation: string | undefined;
  readonly member: string | undefined;
  readonly code: string;
}

export interface Problem {
  readonly code: string;
  readonly pointer: string | undefined;
  readonly limit: string | undefined;
  readonly operationIds: readonly string[];
  readonly issues: readonly ProblemIssue[] | undefined;
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
  const issues =
    code === "validation_failed" && Array.isArray(body.errors)
      ? body.errors.flatMap((item: unknown): ProblemIssue[] => {
          const error = item as Record<string, unknown>;
          const field = (name: string): string | undefined =>
            typeof error[name] === "string" ? error[name] : undefined;
          const issueCode = field("code");
          return issueCode === undefined
            ? []
            : [{ operation: field("operation"), member: field("member"), code: issueCode }];
        })
      : undefined;
  return { code, pointer: text("pointer"), limit: text("limit"), operationIds, issues };
}
