import { readProblem, RestHttpError, RestQueryError } from "./errors.js";

/** How a context talks to the API. */
export interface ContextOptions {
  /** The API root, ending with `/` (for example `https://shop.example/api/`). */
  readonly baseUrl: string | URL;
  /** Headers for every request, such as authorization; a function is called per request. */
  readonly headers?: HeadersInit | (() => HeadersInit | Promise<HeadersInit>);
  /**
   * `query` (default) sends HTTP QUERY to `{set}`; `post` sends POST to `{set}/$query`, for infrastructure that
   * blocks unknown methods.
   */
  readonly transport?: "query" | "post";
  /** A custom fetch, for tests or older runtimes. */
  readonly fetch?: typeof fetch;
}

/** Per-call options of the terminal operators. */
export interface CallOptions {
  readonly signal?: AbortSignal;
}

export class Transport {
  private readonly fetch: typeof fetch;
  private readonly baseUrl: URL;

  constructor(private readonly options: ContextOptions) {
    this.fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.baseUrl = new URL(options.baseUrl);
  }

  async send(path: string, method: string, body: unknown, signal: AbortSignal | undefined): Promise<Response> {
    const headers = new Headers(
      typeof this.options.headers === "function" ? await this.options.headers() : this.options.headers,
    );
    headers.set("content-type", "application/json");
    return this.fetch(new URL(path, this.baseUrl), {
      method,
      headers,
      body: JSON.stringify(body),
      ...(signal === undefined ? {} : { signal }),
    });
  }

  async query(set: string, body: unknown, signal: AbortSignal | undefined): Promise<unknown> {
    const response =
      this.options.transport === "post"
        ? await this.send(`${set}/$query`, "POST", body, signal)
        : await this.send(set, "QUERY", body, signal);
    if (!response.ok) {
      const problem = await readProblem(response);
      throw problem === undefined
        ? new RestHttpError(response.status)
        : new RestQueryError(response.status, problem.code, problem.pointer, problem.limit);
    }

    return response.json();
  }
}
