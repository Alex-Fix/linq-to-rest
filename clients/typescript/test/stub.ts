import { createShop, type ShopContext } from "./shop.generated.js";

export interface Recorded {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
  readonly headers: Headers;
}

/** A fetch that records requests and answers with queued responses (or an empty page). */
export class StubFetch {
  readonly requests: Recorded[] = [];
  private readonly responses: Response[] = [];

  get last(): Recorded {
    const last = this.requests.at(-1);
    if (last === undefined) {
      throw new Error("No request was sent.");
    }

    return last;
  }

  respond(body: unknown, status = 200): this {
    this.responses.push(
      new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } }),
    );
    return this;
  }

  problem(status: number, problem: Record<string, unknown>): this {
    this.responses.push(
      new Response(JSON.stringify(problem), { status, headers: { "content-type": "application/problem+json" } }),
    );
    return this;
  }

  readonly fetch = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = new URL(input instanceof Request ? input.url : input);
    this.requests.push({
      method: init?.method ?? "GET",
      path: url.pathname,
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
      headers: new Headers(init?.headers),
    });
    return Promise.resolve(
      this.responses.shift() ??
        new Response(JSON.stringify({ items: [] }), { headers: { "content-type": "application/json" } }),
    );
  };
}

export function shop(options: { readonly transport?: "query" | "post" } = {}): { shop: ShopContext; http: StubFetch } {
  const http = new StubFetch();
  return { shop: createShop({ baseUrl: "https://shop.test/api/", fetch: http.fetch, ...options }), http };
}
