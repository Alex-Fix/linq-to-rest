import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import type { CatalogContext } from "../catalog.generated";
import { CategoryTree, descendants, type CategoryNode } from "../CategoryTree";
import { useSession } from "../session";

type Sort = "name" | "price" | "priceDesc" | "newest";

interface Filters {
  readonly search: string;
  readonly categoryId: number | undefined;
  readonly brandIds: readonly number[];
  readonly minPrice: number | undefined;
  readonly maxPrice: number | undefined;
  readonly inStockOnly: boolean;
  readonly sort: Sort;
}

const initialFilters: Filters = {
  search: "",
  categoryId: undefined,
  brandIds: [],
  minPrice: undefined,
  maxPrice: undefined,
  inStockOnly: false,
  sort: "name",
};

/** The product grid's query: every filter is translated to the server, nothing is filtered here. */
function productQuery(catalog: CatalogContext, filters: Filters, categories: readonly CategoryNode[]) {
  let query = catalog.products.asNoTracking();
  if (filters.search.trim() !== "") {
    const search = filters.search.trim();
    query = query.where((p) => p.name.contains(search));
  }

  if (filters.categoryId !== undefined) {
    // The selected category and every category below it.
    const ids = descendants(categories, filters.categoryId);
    query = query.where((p) => p.categoryId.in(ids));
  }

  if (filters.brandIds.length > 0) {
    const brands = filters.brandIds;
    query = query.where((p) => p.brandId.in(brands));
  }

  const { minPrice, maxPrice } = filters;
  if (minPrice !== undefined) {
    query = query.where((p) => p.price.ge(minPrice));
  }

  if (maxPrice !== undefined) {
    query = query.where((p) => p.price.le(maxPrice));
  }

  if (filters.inStockOnly) {
    query = query.where((p) => p.inStock.eq(true));
  }

  const sorted =
    filters.sort === "price"
      ? query.orderBy((p) => p.price)
      : filters.sort === "priceDesc"
        ? query.orderByDescending((p) => p.price)
        : filters.sort === "newest"
          ? query.orderByDescending((p) => p.createdAt)
          : query.orderBy((p) => p.name);

  // Cards only need a few members and the first image, so that's all the server sends.
  return sorted
    .select((p) => ({
      id: p.id,
      name: p.name,
      price: p.price,
      inStock: p.inStock,
      status: p.status,
      images: p.images.where((i) => i.sortOrder.eq(0)).select((i) => i.url),
    }))
    .take(24);
}

type Card = Awaited<ReturnType<ReturnType<typeof productQuery>["toList"]>>[number];

export function Browse() {
  const { catalog, isEditor } = useSession();
  const [filters, setFilters] = useState<Filters>(initialFilters);
  const [categories, setCategories] = useState<CategoryNode[]>([]);
  const [brands, setBrands] = useState<{ id: number; name: string; productCount: number }[]>([]);
  const [page, setPage] = useState<{ cards: Card[]; next: string | undefined; total: number | undefined }>();
  const [error, setError] = useState<string>();
  const [loadingMore, setLoadingMore] = useState(false);

  // About fifteen categories: one query, assembled into a tree here.
  useEffect(() => {
    const abort = new AbortController();
    Promise.all([
      catalog.categories
        .orderBy((c) => c.name)
        .select((c) => ({ id: c.id, name: c.name, parentId: c.parentId, productCount: c.productCount }))
        .take(200)
        .toList({ signal: abort.signal }),
      catalog.brands
        .orderBy((b) => b.name)
        .select((b) => ({ id: b.id, name: b.name, productCount: b.productCount }))
        .take(100)
        .toList({ signal: abort.signal }),
    ])
      .then(([categoryRows, brandRows]) => {
        setCategories(categoryRows);
        setBrands(brandRows);
      })
      .catch((reason: unknown) => {
        if (!abort.signal.aborted) {
          setError(String(reason));
        }
      });
    return () => {
      abort.abort();
    };
  }, [catalog]);

  const query = useMemo(() => productQuery(catalog, filters, categories), [catalog, filters, categories]);
  useEffect(() => {
    const abort = new AbortController();
    query
      .toCursorPage({ includeTotalCount: true, signal: abort.signal })
      .then((result) => {
        setError(undefined);
        setPage({ cards: result.items, next: result.nextCursor, total: result.totalCount });
      })
      .catch((reason: unknown) => {
        if (!abort.signal.aborted) {
          setError(String(reason));
        }
      });
    return () => {
      abort.abort();
    };
  }, [query]);

  const loadMore = () => {
    if (page?.next === undefined) {
      return;
    }

    setLoadingMore(true);
    query
      .after(page.next)
      .toCursorPage()
      .then((result) => {
        setPage({ cards: [...page.cards, ...result.items], next: result.nextCursor, total: page.total });
      })
      .catch((reason: unknown) => {
        setError(String(reason));
      })
      .finally(() => {
        setLoadingMore(false);
      });
  };

  const update = (change: Partial<Filters>) => {
    setFilters((current) => ({ ...current, ...change }));
  };

  return (
    <div className="browse">
      <aside>
        <h2>Categories</h2>
        <CategoryTree
          categories={categories}
          selected={filters.categoryId}
          onSelect={(categoryId) => {
            update({ categoryId });
          }}
        />
        <h2>Brands</h2>
        <ul className="brands">
          {brands.map((brand) => (
            <li key={brand.id}>
              <label>
                <input
                  type="checkbox"
                  checked={filters.brandIds.includes(brand.id)}
                  onChange={(event) => {
                    update({
                      brandIds: event.target.checked
                        ? [...filters.brandIds, brand.id]
                        : filters.brandIds.filter((id) => id !== brand.id),
                    });
                  }}
                />
                {brand.name} <span className="muted">({brand.productCount})</span>
              </label>
            </li>
          ))}
        </ul>
      </aside>
      <section>
        <div className="filters">
          <input
            type="search"
            placeholder="Search products"
            value={filters.search}
            onChange={(event) => {
              update({ search: event.target.value });
            }}
          />
          <input
            type="number"
            placeholder="Min price"
            min={0}
            value={filters.minPrice ?? ""}
            onChange={(event) => {
              update({ minPrice: event.target.value === "" ? undefined : Number(event.target.value) });
            }}
          />
          <input
            type="number"
            placeholder="Max price"
            min={0}
            value={filters.maxPrice ?? ""}
            onChange={(event) => {
              update({ maxPrice: event.target.value === "" ? undefined : Number(event.target.value) });
            }}
          />
          <label>
            <input
              type="checkbox"
              checked={filters.inStockOnly}
              onChange={(event) => {
                update({ inStockOnly: event.target.checked });
              }}
            />
            In stock
          </label>
          <select
            value={filters.sort}
            onChange={(event) => {
              update({ sort: event.target.value as Sort });
            }}
          >
            <option value="name">Name</option>
            <option value="price">Price: low to high</option>
            <option value="priceDesc">Price: high to low</option>
            <option value="newest">Newest</option>
          </select>
        </div>
        {error !== undefined && <p className="error">{error}</p>}
        {page !== undefined && (
          <>
            <p className="muted">
              {page.total} products{isEditor ? " (including drafts and discontinued)" : ""}
            </p>
            <ul className="grid">
              {page.cards.map((card) => (
                <li key={card.id} className="card">
                  <Link to={`/products/${String(card.id)}`}>
                    {card.images[0] !== undefined ? <img src={card.images[0]} alt="" /> : <div className="noimage" />}
                    <span className="name">{card.name}</span>
                  </Link>
                  <span className="price">{card.price.toFixed(2)}</span>
                  {!card.inStock && <span className="badge">Out of stock</span>}
                  {card.status !== "Active" && <span className="badge draft">{card.status}</span>}
                </li>
              ))}
            </ul>
            {page.next !== undefined && (
              <button type="button" onClick={loadMore} disabled={loadingMore}>
                {loadingMore ? "Loading…" : "Load more"}
              </button>
            )}
          </>
        )}
      </section>
    </div>
  );
}
