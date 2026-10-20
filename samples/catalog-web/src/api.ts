import { createCatalog, type CatalogContext } from "./catalog.generated";

export type Role = "viewer" | "editor";

/** Catalog.Api's root; set VITE_CATALOG_API to point elsewhere. */
export const apiRoot: string = (import.meta.env.VITE_CATALOG_API as string | undefined) ?? "http://localhost:5095/";

/** A new unit of work. Queries and saves carry the token, if any; each screen that edits makes its own. */
export function catalogFor(token: string | undefined): CatalogContext {
  return createCatalog({
    baseUrl: new URL("api/", apiRoot),
    headers: (): Record<string, string> => (token === undefined ? {} : { Authorization: `Bearer ${token}` }),
  });
}

/** The sample's development-only sign-in: a short-lived token for a fixed viewer or editor. */
export async function devLogin(role: Role): Promise<string> {
  const response = await fetch(new URL("auth/dev-login", apiRoot), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ role }),
  });
  if (!response.ok) {
    throw new Error(`Sign-in failed (${String(response.status)}).`);
  }

  return ((await response.json()) as { token: string }).token;
}
