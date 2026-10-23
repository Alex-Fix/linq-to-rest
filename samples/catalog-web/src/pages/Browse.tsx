import { PictureOutlined } from "@ant-design/icons";
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Col,
  Empty,
  Input,
  InputNumber,
  Row,
  Select,
  Skeleton,
  Space,
  Switch,
  Tag,
  Typography,
} from "antd";
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

type ProductCard = Awaited<ReturnType<ReturnType<typeof productQuery>["toList"]>>[number];

export function Browse() {
  const { catalog, isEditor } = useSession();
  const [filters, setFilters] = useState<Filters>(initialFilters);
  const [categories, setCategories] = useState<CategoryNode[]>([]);
  const [brands, setBrands] = useState<{ id: number; name: string; productCount: number }[]>([]);
  const [page, setPage] = useState<{ cards: ProductCard[]; next: string | undefined; total: number | undefined }>();
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
    <Row gutter={24}>
      <Col xs={24} md={7} lg={5}>
        <Card title="Categories" size="small">
          <CategoryTree
            categories={categories}
            selected={filters.categoryId}
            onSelect={(categoryId) => {
              update({ categoryId });
            }}
          />
        </Card>
        <Card title="Brands" size="small" style={{ marginTop: 16 }}>
          <Checkbox.Group
            style={{ display: "flex", flexDirection: "column", gap: 4 }}
            value={[...filters.brandIds]}
            onChange={(values) => {
              update({ brandIds: values });
            }}
            options={brands.map((brand) => ({
              value: brand.id,
              label: (
                <span>
                  {brand.name} <Typography.Text type="secondary">{brand.productCount}</Typography.Text>
                </span>
              ),
            }))}
          />
        </Card>
      </Col>
      <Col xs={24} md={17} lg={19}>
        <Card size="small" style={{ marginBottom: 16 }}>
          <Space wrap size="middle">
            <Input.Search
              allowClear
              placeholder="Search products"
              style={{ width: 260 }}
              value={filters.search}
              onChange={(event) => {
                update({ search: event.target.value });
              }}
            />
            <Space.Compact>
              <InputNumber
                min={0}
                placeholder="Min price"
                value={filters.minPrice ?? null}
                onChange={(value) => {
                  update({ minPrice: value ?? undefined });
                }}
              />
              <InputNumber
                min={0}
                placeholder="Max price"
                value={filters.maxPrice ?? null}
                onChange={(value) => {
                  update({ maxPrice: value ?? undefined });
                }}
              />
            </Space.Compact>
            <Space>
              <Switch
                checked={filters.inStockOnly}
                onChange={(inStockOnly) => {
                  update({ inStockOnly });
                }}
              />
              <Typography.Text>In stock only</Typography.Text>
            </Space>
            <Select<Sort>
              style={{ width: 190 }}
              value={filters.sort}
              onChange={(sort) => {
                update({ sort });
              }}
              options={[
                { value: "name", label: "Name" },
                { value: "price", label: "Price: low to high" },
                { value: "priceDesc", label: "Price: high to low" },
                { value: "newest", label: "Newest" },
              ]}
            />
          </Space>
        </Card>
        {error !== undefined && <Alert type="error" showIcon title={error} style={{ marginBottom: 16 }} />}
        {page === undefined ? (
          <Skeleton active />
        ) : (
          <>
            <Typography.Paragraph type="secondary">
              {page.total} products{isEditor ? " (including drafts and discontinued)" : ""}
            </Typography.Paragraph>
            {page.cards.length === 0 ? (
              <Empty description="No products match these filters" />
            ) : (
              <Row gutter={[16, 16]}>
                {page.cards.map((card) => (
                  <Col key={card.id} xs={24} sm={12} xl={8} xxl={6}>
                    <Link to={`/products/${String(card.id)}`}>
                      <Card
                        hoverable
                        className="product-card"
                        cover={
                          card.images[0] !== undefined ? (
                            <img src={card.images[0]} alt="" />
                          ) : (
                            <div className="placeholder">
                              <PictureOutlined />
                            </div>
                          )
                        }
                      >
                        <Card.Meta
                          title={card.name}
                          description={<Typography.Text strong>{card.price.toFixed(2)}</Typography.Text>}
                        />
                        <Space size={4} style={{ marginTop: 8 }} wrap>
                          {card.inStock ? <Tag color="green">In stock</Tag> : <Tag>Out of stock</Tag>}
                          {card.status !== "Active" && (
                            <Tag color={card.status === "Draft" ? "gold" : "red"}>{card.status}</Tag>
                          )}
                        </Space>
                      </Card>
                    </Link>
                  </Col>
                ))}
              </Row>
            )}
            {page.next !== undefined && (
              <div style={{ textAlign: "center", marginTop: 24 }}>
                <Button onClick={loadMore} loading={loadingMore}>
                  Load more
                </Button>
              </div>
            )}
          </>
        )}
      </Col>
    </Row>
  );
}
