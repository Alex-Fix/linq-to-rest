import { useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import type { Product } from "../catalog.generated";
import { describe } from "../errors";
import { useSession } from "../session";

export function ProductPage() {
  const id = Number(useParams().id);
  const { catalog, isEditor } = useSession();
  const [product, setProduct] = useState<Product | null>();
  const [related, setRelated] = useState<{ id: number; name: string; price: number }[]>([]);
  const [error, setError] = useState<string>();

  useEffect(() => {
    const abort = new AbortController();
    const signal = abort.signal;
    catalog.products
      .asNoTracking()
      .include((p) => p.category)
      .include((p) => p.brand)
      .include((p) => p.images)
      .firstOrDefault((p) => p.id.eq(id), { signal })
      .then(async (found) => {
        setError(undefined);
        setProduct(found ?? null);
        if (found === undefined) {
          return;
        }

        // "More in this category": a second, projected query.
        setRelated(
          await catalog.products
            .where((p) => p.categoryId.eq(found.categoryId).and(p.id.ne(found.id)))
            .orderByDescending((p) => p.createdAt)
            .select((p) => ({ id: p.id, name: p.name, price: p.price }))
            .take(4)
            .toList({ signal }),
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

  if (error !== undefined) {
    return <p className="error">{error}</p>;
  }

  if (product === undefined) {
    return <p className="muted">Loading…</p>;
  }

  if (product === null) {
    return <p>This product doesn't exist, or you can't see it.</p>;
  }

  const images = [...product.images].sort((left, right) => left.sortOrder - right.sortOrder);
  return (
    <article className="product">
      <p className="muted">
        {product.category?.name} · {product.brand?.name} · {product.sku}
      </p>
      <h1>{product.name}</h1>
      {product.status !== "Active" && <span className="badge draft">{product.status}</span>}
      <p className="price">{product.price.toFixed(2)}</p>
      <p>{product.inStock ? `${String(product.stock)} in stock` : "Out of stock"}</p>
      {product.description !== null && <p>{product.description}</p>}
      {product.tags.length > 0 && <p className="muted">Tags: {product.tags.join(", ")}</p>}
      <div className="gallery">
        {images.map((image) => (
          <img key={image.id} src={image.url} alt="" />
        ))}
      </div>
      {isEditor && <Link to={`/products/${String(product.id)}/edit`}>Edit</Link>}
      {related.length > 0 && (
        <>
          <h2>More in this category</h2>
          <ul>
            {related.map((item) => (
              <li key={item.id}>
                <Link to={`/products/${String(item.id)}`}>{item.name}</Link> {item.price.toFixed(2)}
              </li>
            ))}
          </ul>
        </>
      )}
    </article>
  );
}
