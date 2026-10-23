import { createContext, use, useMemo, useState, type ReactNode } from "react";
import { catalogFor, devLogin, type Role } from "./api";
import type { CatalogContext } from "./catalog.generated";

interface Session {
  /** The signed-in role, or undefined when anonymous. */
  readonly role: Role | undefined;
  readonly isEditor: boolean;
  /** A context for read-only screens; it changes when the role does, so queries re-run. */
  readonly catalog: CatalogContext;
  /** Creates a fresh unit of work, for a screen that edits. */
  readonly newCatalog: () => CatalogContext;
  readonly signIn: (role: Role | undefined) => Promise<void>;
}

const SessionContext = createContext<Session | undefined>(undefined);

export function SessionProvider({ children }: { readonly children: ReactNode }) {
  const [auth, setAuth] = useState<{ role: Role; token: string } | undefined>(undefined);
  const session = useMemo<Session>(() => {
    const token = auth?.token;
    return {
      role: auth?.role,
      isEditor: auth?.role === "editor",
      catalog: catalogFor(token),
      newCatalog: () => catalogFor(token),
      signIn: async (role) => {
        setAuth(role === undefined ? undefined : { role, token: await devLogin(role) });
      },
    };
  }, [auth]);
  return <SessionContext value={session}>{children}</SessionContext>;
}

export function useSession(): Session {
  const session = use(SessionContext);
  if (session === undefined) {
    throw new Error("useSession needs a SessionProvider.");
  }

  return session;
}
