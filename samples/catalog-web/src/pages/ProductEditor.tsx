import { ArrowDownOutlined, ArrowUpOutlined, DeleteOutlined, PlusOutlined, ReloadOutlined } from "@ant-design/icons";
import {
  Alert,
  Button,
  Card,
  Col,
  Form,
  Input,
  InputNumber,
  Result,
  Row,
  Select,
  Skeleton,
  Space,
  Typography,
} from "antd";
import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { RestConcurrencyError, RestValidationError } from "linqtorest";
import type { CatalogContext, Product, ProductImage, ProductStatus } from "../catalog.generated";
import { describe, fieldMessage } from "../errors";
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
/** Field errors by member: the product's, and each image row's (by row key). */
interface FieldErrors {
  readonly product: Readonly<Record<string, readonly string[]>>;
  readonly images: Readonly<Record<string, readonly string[]>>;
}

const noErrors: FieldErrors = { product: {}, images: {} };

/** The save failed validation (locally or on the server): its errors placed on the form's fields. */
class InvalidForm extends Error {
  constructor(
    readonly validation: RestValidationError,
    readonly fields: FieldErrors,
  ) {
    super(validation.message);
  }
}

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

  const rows = new Map<object, string>();
  form.images.forEach((row, sortOrder) => {
    const image = row.id === undefined ? undefined : existing.get(row.id);
    if (image === undefined) {
      // A new image points at the product; for a new product that becomes a ref in the batch.
      rows.set(catalog.productImages.add({ url: row.url, sortOrder, product }), row.key);
      return;
    }

    image.url = row.url;
    image.sortOrder = sortOrder;
    rows.set(image, row.key);
    existing.delete(image.id);
  });
  for (const removed of existing.values()) {
    catalog.productImages.remove(removed);
  }

  try {
    await catalog.saveChanges();
  } catch (error) {
    // Validation errors from the client's check or the server's 422 name tracked entities; map them to fields.
    if (error instanceof RestValidationError) {
      const images: Record<string, readonly string[]> = {};
      for (const [image, key] of rows) {
        images[key] = Object.values(error.errorsFor(image)).flat();
      }

      throw new InvalidForm(error, { product: error.errorsFor(product), images });
    }

    throw error;
  }

  return product.id;
}

/** The form as the row it would save, for checking the model's rules as the editor types. */
function candidate(form: Form) {
  return {
    sku: form.sku,
    name: form.name,
    description: form.description === "" ? null : form.description,
    price: Number(form.price),
    stock: Number(form.stock),
  };
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
  const [serverErrors, setServerErrors] = useState<FieldErrors>(noErrors);
  const [touched, setTouched] = useState<ReadonlySet<string>>(new Set());

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
    return <Result status="403" title="Editors only" subTitle="Sign in as an editor to change products." />;
  }

  if (form === undefined || options === undefined) {
    return error === undefined ? (
      <Skeleton active paragraph={{ rows: 10 }} />
    ) : (
      <Alert type="error" showIcon title={error} />
    );
  }

  const update = (change: Partial<Form>) => {
    setForm({ ...form, ...change });
    setTouched(new Set([...touched, ...Object.keys(change)]));
    setServerErrors(noErrors);
  };

  // The model's rules, checked as the editor types (touched fields), plus whatever the last save reported.
  const rules = catalog.products.rules;
  const local = catalog.products.validate(candidate(form));
  const help = (member: "sku" | "name" | "description" | "price" | "stock") => {
    const codes = [
      ...local.filter((issue) => issue.member === member && touched.has(member)).map((issue) => issue.code),
      ...(serverErrors.product[member] ?? []),
    ];
    return codes.length === 0
      ? {}
      : {
          validateStatus: "error" as const,
          help: [...new Set(codes)].map((code) => fieldMessage(code, rules[member])).join(" "),
        };
  };
  // Only set an input's maxLength when the model has one (exactOptionalPropertyTypes).
  const limit = (maxLength: number | undefined) => (maxLength === undefined ? {} : { maxLength });
  const imageHelp = (row: ImageRow) => {
    const codes = serverErrors.images[row.key] ?? [];
    return codes.length === 0
      ? undefined
      : codes.map((code) => fieldMessage(code, catalog.productImages.rules.url)).join(" ");
  };

  const submit = (base: Loaded | undefined) => {
    setSaving(true);
    setError(undefined);
    setConflict(false);
    setServerErrors(noErrors);
    save(newCatalog(), form, base)
      .then((savedId) => navigate(`/products/${String(savedId)}`))
      .catch((reason: unknown) => {
        if (reason instanceof InvalidForm) {
          setTouched(new Set(Object.keys(form)));
          setServerErrors(reason.fields);
          setError(describe(reason.validation));
          return;
        }

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
    <Card
      title={id === undefined ? "New product" : `Edit ${loaded?.product.name ?? ""}`}
      style={{ maxWidth: 900, margin: "0 auto" }}
    >
      <Form
        layout="vertical"
        onFinish={() => {
          submit(loaded);
        }}
      >
        <Row gutter={16}>
          <Col xs={24} md={8}>
            <Form.Item
              label="SKU"
              required={rules.sku?.required === true}
              {...help("sku")}
              extra="Capitals, digits and dashes."
            >
              <Input
                value={form.sku}
                onChange={(event) => {
                  update({ sku: event.target.value });
                }}
              />
            </Form.Item>
          </Col>
          <Col xs={24} md={16}>
            <Form.Item label="Name" required={rules.name?.required === true} {...help("name")}>
              <Input
                {...limit(rules.name?.maxLength)}
                showCount
                value={form.name}
                onChange={(event) => {
                  update({ name: event.target.value });
                }}
              />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item label="Description" {...help("description")}>
          <Input.TextArea
            rows={3}
            {...limit(rules.description?.maxLength)}
            value={form.description}
            onChange={(event) => {
              update({ description: event.target.value });
            }}
          />
        </Form.Item>
        <Row gutter={16}>
          <Col xs={12} md={6}>
            <Form.Item label="Price" {...help("price")}>
              <InputNumber
                style={{ width: "100%" }}
                step={0.01}
                value={Number(form.price)}
                onChange={(value) => {
                  update({ price: String(value ?? 0) });
                }}
              />
            </Form.Item>
          </Col>
          <Col xs={12} md={6}>
            <Form.Item label="Stock" {...help("stock")}>
              <InputNumber
                style={{ width: "100%" }}
                value={Number(form.stock)}
                onChange={(value) => {
                  update({ stock: String(value ?? 0) });
                }}
              />
            </Form.Item>
          </Col>
          <Col xs={24} md={12}>
            <Form.Item label="Status">
              <Select<ProductStatus>
                value={form.status}
                onChange={(status) => {
                  update({ status });
                }}
                options={(["Draft", "Active", "Discontinued"] as const).map((status) => ({
                  value: status,
                  label: status,
                }))}
              />
            </Form.Item>
          </Col>
        </Row>
        <Row gutter={16}>
          <Col xs={24} md={12}>
            <Form.Item label="Category">
              <Select
                showSearch={{ optionFilterProp: "label" }}
                value={form.categoryId}
                onChange={(categoryId) => {
                  update({ categoryId });
                }}
                options={options.categories.map((category) => ({ value: category.id, label: category.name }))}
              />
            </Form.Item>
          </Col>
          <Col xs={24} md={12}>
            <Form.Item label="Brand">
              <Select
                value={form.brandId}
                onChange={(brandId) => {
                  update({ brandId });
                }}
                options={options.brands.map((brand) => ({ value: brand.id, label: brand.name }))}
              />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item label="Images" extra="The first image is the card image.">
          <Space orientation="vertical" style={{ width: "100%" }}>
            {images.map((row, index) => (
              <div key={row.key}>
                <Space.Compact style={{ width: "100%" }}>
                  <Input
                    type="url"
                    placeholder="https://…"
                    status={imageHelp(row) === undefined ? "" : "error"}
                    {...limit(catalog.productImages.rules.url?.maxLength)}
                    value={row.url}
                    onChange={(event) => {
                      update({
                        images: images.map((other) =>
                          other.key === row.key ? { ...other, url: event.target.value } : other,
                        ),
                      });
                    }}
                  />
                  <Button
                    icon={<ArrowUpOutlined />}
                    disabled={index === 0}
                    onClick={() => {
                      move(index, -1);
                    }}
                  />
                  <Button
                    icon={<ArrowDownOutlined />}
                    disabled={index === images.length - 1}
                    onClick={() => {
                      move(index, 1);
                    }}
                  />
                  <Button
                    danger
                    icon={<DeleteOutlined />}
                    onClick={() => {
                      update({ images: images.filter((other) => other.key !== row.key) });
                    }}
                  />
                </Space.Compact>
                {imageHelp(row) !== undefined && <Typography.Text type="danger">{imageHelp(row)}</Typography.Text>}
              </div>
            ))}
            <Button
              type="dashed"
              block
              icon={<PlusOutlined />}
              onClick={() => {
                update({ images: [...images, { key: crypto.randomUUID(), id: undefined, url: "" }] });
              }}
            >
              Add image
            </Button>
          </Space>
        </Form.Item>
        {error !== undefined && (
          <Alert type={conflict ? "warning" : "error"} showIcon title={error} style={{ marginBottom: 16 }} />
        )}
        <Space>
          <Button type="primary" htmlType="submit" loading={saving}>
            Save
          </Button>
          {conflict && (
            <Button icon={<ReloadOutlined />} onClick={reloadAndReapply} disabled={saving}>
              Reload and reapply my changes
            </Button>
          )}
        </Space>
      </Form>
    </Card>
  );
}
