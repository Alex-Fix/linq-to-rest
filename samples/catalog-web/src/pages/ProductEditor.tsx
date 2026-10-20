import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { RestConcurrencyError } from "linqtorest";
import type { CatalogContext, Product, ProductImage, ProductStatus } from "../catalog.generated";
import { describe } from "../errors";
import { useSession } from "../session";

interface ImageRow {
  readonly key: string;
  readonly id: number | undefined;
  readonly url: string;
}

interface Form {
  readonly sku: string;
  readonly name: string;
  readonly description: string;
  readonly price: string;
  readonly status: ProductStatus;
  readonly stock: string;
  readonly categoryId: number;
  readonly brandId: number;
  readonly images: readonly ImageRow[];
}

/** The product and images as the editor loaded them; a save is based on these, concurrency token included. */
interface Loaded {
  readonly product: Product;
  readonly images: readonly ProductImage[];
}

async function load(catalog: CatalogContext, id: number): Promise<Loaded | undefined> {
  const product = await catalog.products.asNoTracking().firstOrDefault((p) => p.id.eq(id));
  if (product === undefined) {
    return undefined;
  }

  const images = await catalog.productImages
    .asNoTracking()
    .where((i) => i.productId.eq(id))
    .orderBy((i) => i.sortOrder)
    .take(50)
    .toList();
  return { product, images };
}

function formOf(loaded: Loaded): Form {
  const { product } = loaded;
  return {
    sku: product.sku,
    name: product.name,
    description: product.description ?? "",
    price: String(product.price),
    status: product.status,
    stock: String(product.stock),
    categoryId: product.categoryId,
    brandId: product.brandId,
    images: loaded.images.map((image) => ({ key: String(image.id), id: image.id, url: image.url })),
  };
}

/**
 * One save is one batch: the product and every image change. The unit of work starts from the rows as the editor
 * loaded them, so a change made elsewhere in the meantime fails with a concurrency conflict instead of being lost.
 */
async function save(catalog: CatalogContext, form: Form, loaded: Loaded | undefined): Promise<number> {
  let product: Product;
  const existing = new Map<number, ProductImage>();
  if (loaded === undefined) {
    product = catalog.products.add({
      sku: form.sku,
      name: form.name,
      price: 0,
      status: "Draft",
      stock: 0,
      categoryId: form.categoryId,
      brandId: form.brandId,
    });
  } else {
    product = { ...loaded.product, category: null, brand: null, images: [] };
    catalog.products.attach(product);
    for (const image of loaded.images) {
      const tracked = { ...image, product: null };
      catalog.productImages.attach(tracked);
      existing.set(image.id, tracked);
    }
  }

  product.sku = form.sku;
  product.name = form.name;
  product.description = form.description === "" ? null : form.description;
  product.price = Number(form.price);
  product.status = form.status;
  product.stock = Number(form.stock);
  product.categoryId = form.categoryId;
  product.brandId = form.brandId;

  form.images.forEach((row, sortOrder) => {
    const image = row.id === undefined ? undefined : existing.get(row.id);
    if (image === undefined) {
      // A new image points at the product; for a new product that becomes a ref in the batch.
      catalog.productImages.add({ url: row.url, sortOrder, product });
      return;
    }

    image.url = row.url;
    image.sortOrder = sortOrder;
    existing.delete(image.id);
  });
  for (const removed of existing.values()) {
    catalog.productImages.remove(removed);
  }

  await catalog.saveChanges();
  return product.id;
}

export function ProductEditor() {
  const params = useParams();
  const id = params.id === undefined ? undefined : Number(params.id);
  const navigate = useNavigate();
  const { catalog, newCatalog, isEditor } = useSession();
  const [loaded, setLoaded] = useState<Loaded>();
  const [form, setForm] = useState<Form>();
  const [options, setOptions] = useState<{
    categories: { id: number; name: string }[];
    brands: { id: number; name: string }[];
  }>();
  const [error, setError] = useState<string>();
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    const abort = new AbortController();
    const signal = abort.signal;
    Promise.all([
      catalog.categories
        .orderBy((c) => c.name)
        .select((c) => ({ id: c.id, name: c.name }))
        .take(200)
        .toList({ signal }),
      catalog.brands
        .orderBy((b) => b.name)
        .select((b) => ({ id: b.id, name: b.name }))
        .take(100)
        .toList({ signal }),
      id === undefined ? Promise.resolve(undefined) : load(catalog, id),
    ])
      .then(([categories, brands, current]) => {
        setOptions({ categories, brands });
        setLoaded(current);
        const first = { categoryId: categories[0]?.id ?? 0, brandId: brands[0]?.id ?? 0 };
        setForm(
          current === undefined
            ? { sku: "", name: "", description: "", price: "0", status: "Draft", stock: "0", ...first, images: [] }
            : formOf(current),
        );
      })
      .catch((reason: unknown) => {
        if (!signal.aborted) {
          setError(describe(reason));
        }
      });
    return () => {
      abort.abort();
    };
  }, [catalog, id]);

  if (!isEditor) {
    return <p>Sign in as an editor to change products.</p>;
  }

  if (form === undefined || options === undefined) {
    return error === undefined ? <p className="muted">Loading…</p> : <p className="error">{error}</p>;
  }

  const update = (change: Partial<Form>) => {
    setForm({ ...form, ...change });
  };

  const submit = (base: Loaded | undefined) => {
    setSaving(true);
    setError(undefined);
    setConflict(false);
    save(newCatalog(), form, base)
      .then((savedId) => navigate(`/products/${String(savedId)}`))
      .catch((reason: unknown) => {
        setConflict(reason instanceof RestConcurrencyError);
        setError(describe(reason));
      })
      .finally(() => {
        setSaving(false);
      });
  };

  // Keeps what the editor typed and applies it on top of the current row (with its new concurrency token).
  const reloadAndReapply = () => {
    if (id === undefined) {
      return;
    }

    load(newCatalog(), id)
      .then((current) => {
        if (current === undefined) {
          setError("The product was deleted meanwhile.");
          return;
        }

        setLoaded(current);
        submit(current);
      })
      .catch((reason: unknown) => {
        setError(describe(reason));
      });
  };

  const images = form.images;
  const move = (index: number, offset: number) => {
    const next = [...images];
    const [row] = next.splice(index, 1);
    if (row !== undefined) {
      next.splice(index + offset, 0, row);
      update({ images: next });
    }
  };

  return (
    <form
      className="editor"
      onSubmit={(event) => {
        event.preventDefault();
        submit(loaded);
      }}
    >
      <h1>{id === undefined ? "New product" : `Edit ${loaded?.product.name ?? ""}`}</h1>
      <label>
        SKU
        <input
          required
          value={form.sku}
          onChange={(event) => {
            update({ sku: event.target.value });
          }}
        />
      </label>
      <label>
        Name
        <input
          required
          value={form.name}
          onChange={(event) => {
            update({ name: event.target.value });
          }}
        />
      </label>
      <label>
        Description
        <textarea
          value={form.description}
          onChange={(event) => {
            update({ description: event.target.value });
          }}
        />
      </label>
      <label>
        Price
        <input
          type="number"
          min={0}
          step="0.01"
          value={form.price}
          onChange={(event) => {
            update({ price: event.target.value });
          }}
        />
      </label>
      <label>
        Stock
        <input
          type="number"
          min={0}
          value={form.stock}
          onChange={(event) => {
            update({ stock: event.target.value });
          }}
        />
      </label>
      <label>
        Status
        <select
          value={form.status}
          onChange={(event) => {
            update({ status: event.target.value as ProductStatus });
          }}
        >
          <option>Draft</option>
          <option>Active</option>
          <option>Discontinued</option>
        </select>
      </label>
      <label>
        Category
        <select
          value={form.categoryId}
          onChange={(event) => {
            update({ categoryId: Number(event.target.value) });
          }}
        >
          {options.categories.map((category) => (
            <option key={category.id} value={category.id}>
              {category.name}
            </option>
          ))}
        </select>
      </label>
      <label>
        Brand
        <select
          value={form.brandId}
          onChange={(event) => {
            update({ brandId: Number(event.target.value) });
          }}
        >
          {options.brands.map((brand) => (
            <option key={brand.id} value={brand.id}>
              {brand.name}
            </option>
          ))}
        </select>
      </label>
      <fieldset>
        <legend>Images (the first one is the card image)</legend>
        {images.map((row, index) => (
          <div key={row.key} className="image-row">
            <input
              type="url"
              required
              value={row.url}
              onChange={(event) => {
                update({
                  images: images.map((other) =>
                    other.key === row.key ? { ...other, url: event.target.value } : other,
                  ),
                });
              }}
            />
            <button
              type="button"
              disabled={index === 0}
              onClick={() => {
                move(index, -1);
              }}
            >
              ↑
            </button>
            <button
              type="button"
              disabled={index === images.length - 1}
              onClick={() => {
                move(index, 1);
              }}
            >
              ↓
            </button>
            <button
              type="button"
              onClick={() => {
                update({ images: images.filter((other) => other.key !== row.key) });
              }}
            >
              Remove
            </button>
          </div>
        ))}
        <button
          type="button"
          onClick={() => {
            update({ images: [...images, { key: crypto.randomUUID(), id: undefined, url: "" }] });
          }}
        >
          Add image
        </button>
      </fieldset>
      {error !== undefined && <p className="error">{error}</p>}
      {conflict && (
        <button type="button" onClick={reloadAndReapply} disabled={saving}>
          Reload and reapply my changes
        </button>
      )}
      <button type="submit" disabled={saving}>
        {saving ? "Saving…" : "Save"}
      </button>
    </form>
  );
}
