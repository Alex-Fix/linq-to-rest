import { EditOutlined, PictureOutlined } from "@ant-design/icons";
import {
  Alert,
  Breadcrumb,
  Button,
  Card,
  Col,
  Descriptions,
  Empty,
  Image,
  List,
  Result,
  Row,
  Skeleton,
  Space,
  Tag,
  Typography,
} from "antd";
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
    return <Alert type="error" showIcon title={error} />;
  }

  if (product === undefined) {
    return <Skeleton active paragraph={{ rows: 8 }} />;
  }

  if (product === null) {
    return (
      <Result
        status="404"
        title="Product not found"
        subTitle="It doesn't exist, or you can't see it."
        extra={<Link to="/">Back to the catalog</Link>}
      />
    );
  }

  const images = [...product.images].sort((left, right) => left.sortOrder - right.sortOrder);
  return (
    <>
      <Breadcrumb
        style={{ marginBottom: 16 }}
        items={[{ title: <Link to="/">Catalog</Link> }, { title: product.category?.name }, { title: product.name }]}
      />
      <Row gutter={32}>
        <Col xs={24} lg={12}>
          <Card>
            {images.length === 0 ? (
              <Empty image={<PictureOutlined style={{ fontSize: 48, color: "#bfbfbf" }} />} description="No images" />
            ) : (
              <Image.PreviewGroup>
                <Space wrap>
                  {images.map((image, index) => (
                    <Image key={image.id} src={image.url} width={index === 0 ? "100%" : 120} alt="" />
                  ))}
                </Space>
              </Image.PreviewGroup>
            )}
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Space orientation="vertical" size="middle" style={{ width: "100%" }}>
            <Space align="center" wrap>
              <Typography.Title level={2} style={{ margin: 0 }}>
                {product.name}
              </Typography.Title>
              {product.status !== "Active" && (
                <Tag color={product.status === "Draft" ? "gold" : "red"}>{product.status}</Tag>
              )}
            </Space>
            <Typography.Title level={3} type="success" style={{ margin: 0 }}>
              {product.price.toFixed(2)}
            </Typography.Title>
            {product.description !== null && <Typography.Paragraph>{product.description}</Typography.Paragraph>}
            <Descriptions column={1} bordered size="small">
              <Descriptions.Item label="SKU">{product.sku}</Descriptions.Item>
              <Descriptions.Item label="Brand">{product.brand?.name}</Descriptions.Item>
              <Descriptions.Item label="Category">{product.category?.name}</Descriptions.Item>
              <Descriptions.Item label="Availability">
                {product.inStock ? <Tag color="green">{product.stock} in stock</Tag> : <Tag>Out of stock</Tag>}
              </Descriptions.Item>
              {product.tags.length > 0 && (
                <Descriptions.Item label="Tags">
                  {product.tags.map((tag) => (
                    <Tag key={tag}>{tag}</Tag>
                  ))}
                </Descriptions.Item>
              )}
            </Descriptions>
            {isEditor && (
              <Link to={`/products/${String(product.id)}/edit`}>
                <Button type="primary" icon={<EditOutlined />}>
                  Edit
                </Button>
              </Link>
            )}
          </Space>
        </Col>
      </Row>
      {related.length > 0 && (
        <Card title="More in this category" style={{ marginTop: 24 }}>
          <List
            dataSource={related}
            renderItem={(item) => (
              <List.Item extra={<Typography.Text strong>{item.price.toFixed(2)}</Typography.Text>}>
                <Link to={`/products/${String(item.id)}`}>{item.name}</Link>
              </List.Item>
            )}
          />
        </Card>
      )}
    </>
  );
}
