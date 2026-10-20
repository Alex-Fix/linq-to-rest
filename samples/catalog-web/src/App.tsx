import { useState } from "react";
import { Link, Route, Routes } from "react-router";
import type { Role } from "./api";
import { Browse } from "./pages/Browse";
import { CategoriesAdmin } from "./pages/CategoriesAdmin";
import { ProductEditor } from "./pages/ProductEditor";
import { ProductPage } from "./pages/ProductPage";
import { useSession } from "./session";

export function App() {
  return (
    <>
      <header className="header">
        <Link to="/" className="brand">
          Catalog
        </Link>
        <EditorLinks />
        <RoleSwitcher />
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Browse />} />
          <Route path="/products/new" element={<ProductEditor />} />
          <Route path="/products/:id" element={<ProductPage />} />
          <Route path="/products/:id/edit" element={<ProductEditor />} />
          <Route path="/categories" element={<CategoriesAdmin />} />
        </Routes>
      </main>
    </>
  );
}

function EditorLinks() {
  const { isEditor } = useSession();
  return isEditor ? (
    <nav className="editor-links">
      <Link to="/products/new">New product</Link>
      <Link to="/categories">Categories</Link>
    </nav>
  ) : null;
}

/** Anonymous, viewer or editor: switching re-runs the queries, so drafts appear and disappear. */
function RoleSwitcher() {
  const { role, signIn } = useSession();
  const [error, setError] = useState<string>();
  const choose = (next: Role | undefined) => {
    setError(undefined);
    signIn(next).catch((reason: unknown) => {
      setError(reason instanceof Error ? reason.message : String(reason));
    });
  };

  return (
    <div className="roles" role="group" aria-label="Signed in as">
      {(["anonymous", "viewer", "editor"] as const).map((option) => {
        const value = option === "anonymous" ? undefined : option;
        return (
          <button
            key={option}
            type="button"
            aria-pressed={role === value}
            onClick={() => {
              choose(value);
            }}
          >
            {option}
          </button>
        );
      })}
      {error !== undefined && <span className="error">{error}</span>}
    </div>
  );
}
