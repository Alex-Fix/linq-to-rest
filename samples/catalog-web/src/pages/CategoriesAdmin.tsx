import { DeleteOutlined, PlusOutlined } from "@ant-design/icons";
import { App as AntApp, Button, Card, Form, Input, Popconfirm, Result, Table, TreeSelect, Typography } from "antd";
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

interface TreeOption {
  readonly value: number;
  readonly title: string;
  readonly children: TreeOption[];
}

/** Categories as TreeSelect options, leaving out one category and everything below it (it can't move under itself). */
function parentOptions(
  rows: readonly Row[],
  excluded: number | undefined,
  parentId: number | null = null,
): TreeOption[] {
  return rows
    .filter((row) => row.parentId === parentId && row.id !== excluded)
    .map((row) => ({ value: row.id, title: row.name, children: parentOptions(rows, excluded, row.id) }));
}

export function CategoriesAdmin() {
  const { catalog, newCatalog, isEditor } = useSession();
  const { message } = AntApp.useApp();
  const [rows, setRows] = useState<Row[]>([]);
  const [loading, setLoading] = useState(true);
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
      .then((loaded) => {
        setRows(loaded);
        setLoading(false);
      })
      .catch((reason: unknown) => {
        if (!abort.signal.aborted) {
          void message.error(describe(reason));
        }
      });
    return () => {
      abort.abort();
    };
  }, [catalog, version, message]);

  if (!isEditor) {
    return <Result status="403" title="Editors only" subTitle="Sign in as an editor to manage categories." />;
  }

  const run = (action: Promise<unknown>, done: string) => {
    action
      .then(() => {
        void message.success(done);
        setVersion((current) => current + 1);
      })
      .catch((reason: unknown) => {
        void message.error(describe(reason));
      });
  };

  const pathOf = (row: Row): string => {
    const parent = rows.find((candidate) => candidate.id === row.parentId);
    return parent === undefined ? row.name : `${pathOf(parent)} / ${row.name}`;
  };

  const sorted = [...rows].sort((left, right) => pathOf(left).localeCompare(pathOf(right)));
  return (
    <Card title="Categories" style={{ maxWidth: 1000, margin: "0 auto" }}>
      <Form
        layout="inline"
        style={{ marginBottom: 16 }}
        onFinish={() => {
          const unit = newCatalog();
          unit.categories.add({ name, parentId });
          run(
            unit.saveChanges().then(() => {
              setName("");
            }),
            `Added ${name}.`,
          );
        }}
      >
        <Form.Item required>
          <Input
            placeholder="New category"
            value={name}
            onChange={(event) => {
              setName(event.target.value);
            }}
          />
        </Form.Item>
        <Form.Item>
          <TreeSelect
            allowClear
            treeDefaultExpandAll
            placeholder="Top level"
            style={{ width: 260 }}
            value={parentId ?? undefined}
            treeData={parentOptions(rows, undefined)}
            onChange={(value: number | undefined) => {
              setParentId(value ?? null);
            }}
          />
        </Form.Item>
        <Form.Item>
          <Button type="primary" htmlType="submit" icon={<PlusOutlined />} disabled={name.trim() === ""}>
            Add
          </Button>
        </Form.Item>
      </Form>
      <Table<Row>
        rowKey="id"
        loading={loading}
        pagination={false}
        dataSource={sorted}
        columns={[
          {
            title: "Name",
            render: (_, row) => (
              <Input
                key={`${String(row.id)}-${row.name}`}
                defaultValue={row.name}
                variant="borderless"
                onBlur={(event) => {
                  const renamed = event.target.value.trim();
                  if (renamed !== "" && renamed !== row.name) {
                    run(
                      change(newCatalog(), row, (category) => {
                        category.name = renamed;
                      }),
                      `Renamed to ${renamed}.`,
                    );
                  }
                }}
              />
            ),
          },
          {
            title: "Path",
            render: (_, row) => <Typography.Text type="secondary">{pathOf(row)}</Typography.Text>,
          },
          { title: "Products", dataIndex: "productCount", width: 100, align: "right" },
          {
            title: "Parent",
            width: 240,
            render: (_, row) => (
              <TreeSelect
                allowClear
                treeDefaultExpandAll
                placeholder="Top level"
                style={{ width: "100%" }}
                value={row.parentId ?? undefined}
                treeData={parentOptions(rows, row.id)}
                onChange={(value: number | undefined) => {
                  run(
                    change(newCatalog(), row, (category) => {
                      category.parentId = value ?? null;
                    }),
                    `Moved ${row.name}.`,
                  );
                }}
              />
            ),
          },
          {
            width: 60,
            render: (_, row) => (
              <Popconfirm
                title={`Delete ${row.name}?`}
                description="Categories that still have products or subcategories can't be deleted."
                okText="Delete"
                okButtonProps={{ danger: true }}
                onConfirm={() => {
                  // Deleting a category that still has products or subcategories is refused by the database (409).
                  const unit = newCatalog();
                  unit.categories.remove({ ...row, parent: null, children: [], products: [] });
                  run(unit.saveChanges(), `Deleted ${row.name}.`);
                }}
              >
                <Button danger type="text" icon={<DeleteOutlined />} />
              </Popconfirm>
            ),
          },
        ]}
      />
    </Card>
  );
}
