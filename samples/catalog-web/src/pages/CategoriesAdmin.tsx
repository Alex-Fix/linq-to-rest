import { useEffect, useState } from "react";
import type { Category, CatalogContext } from "../catalog.generated";
import { describe } from "../errors";
import { useSession } from "../session";

type Row = Pick<Category, "id" | "name" | "parentId" | "productCount">;

/** Each action is its own small unit of work: attach the row as shown, change it, save one batch. */
async function change(catalog: CatalogContext, row: Row, apply: (category: Category) => void): Promise<void> {
  const category: Category = { ...row, parent: null, children: [], products: [] };
  catalog.categories.attach(category);
  apply(category);
  await catalog.saveChanges();
}

export function CategoriesAdmin() {
  const { catalog, newCatalog, isEditor } = useSession();
  const [rows, setRows] = useState<Row[]>([]);
  const [error, setError] = useState<string>();
  const [version, setVersion] = useState(0);
  const [name, setName] = useState("");
  const [parentId, setParentId] = useState<number | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    catalog.categories
      .orderBy((c) => c.name)
      .select((c) => ({ id: c.id, name: c.name, parentId: c.parentId, productCount: c.productCount }))
      .take(200)
      .toList({ signal: abort.signal })
      .then(setRows)
      .catch((reason: unknown) => {
        if (!abort.signal.aborted) {
          setError(describe(reason));
        }
      });
    return () => {
      abort.abort();
    };
  }, [catalog, version]);

  if (!isEditor) {
    return <p>Sign in as an editor to manage categories.</p>;
  }

  const run = (action: Promise<void>) => {
    setError(undefined);
    action
      .then(() => {
        setVersion((current) => current + 1);
      })
      .catch((reason: unknown) => {
        setError(describe(reason));
      });
  };

  const pathOf = (row: Row): string => {
    const parent = rows.find((candidate) => candidate.id === row.parentId);
    return parent === undefined ? row.name : `${pathOf(parent)} / ${row.name}`;
  };

  const sorted = [...rows].sort((left, right) => pathOf(left).localeCompare(pathOf(right)));
  return (
    <div className="admin">
      <h1>Categories</h1>
      {error !== undefined && <p className="error">{error}</p>}
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const unit = newCatalog();
          unit.categories.add({ name, parentId });
          run(
            unit.saveChanges().then(() => {
              setName("");
            }),
          );
        }}
      >
        <input
          required
          placeholder="New category"
          value={name}
          onChange={(event) => {
            setName(event.target.value);
          }}
        />
        <ParentSelect rows={sorted} pathOf={pathOf} value={parentId} onChange={setParentId} />
        <button type="submit">Add</button>
      </form>
      <table>
        <thead>
          <tr>
            <th>Category</th>
            <th>Products</th>
            <th>Parent</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {sorted.map((row) => (
            <tr key={row.id}>
              <td>
                <input
                  defaultValue={row.name}
                  aria-label={`Name of ${row.name}`}
                  onBlur={(event) => {
                    const renamed = event.target.value.trim();
                    if (renamed !== "" && renamed !== row.name) {
                      run(
                        change(newCatalog(), row, (category) => {
                          category.name = renamed;
                        }),
                      );
                    }
                  }}
                />
              </td>
              <td>{row.productCount}</td>
              <td>
                <ParentSelect
                  rows={sorted.filter((candidate) => candidate.id !== row.id)}
                  pathOf={pathOf}
                  value={row.parentId}
                  onChange={(moved) => {
                    run(
                      change(newCatalog(), row, (category) => {
                        category.parentId = moved;
                      }),
                    );
                  }}
                />
              </td>
              <td>
                <button
                  type="button"
                  onClick={() => {
                    // Deleting a category that still has products or subcategories is refused by the database (409).
                    const unit = newCatalog();
                    unit.categories.remove({ ...row, parent: null, children: [], products: [] });
                    run(unit.saveChanges().then(() => undefined));
                  }}
                >
                  Delete
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ParentSelect({
  rows,
  pathOf,
  value,
  onChange,
}: {
  readonly rows: readonly Row[];
  readonly pathOf: (row: Row) => string;
  readonly value: number | null;
  readonly onChange: (parentId: number | null) => void;
}) {
  return (
    <select
      value={value ?? ""}
      onChange={(event) => {
        onChange(event.target.value === "" ? null : Number(event.target.value));
      }}
    >
      <option value="">(top level)</option>
      {rows.map((row) => (
        <option key={row.id} value={row.id}>
          {pathOf(row)}
        </option>
      ))}
    </select>
  );
}
