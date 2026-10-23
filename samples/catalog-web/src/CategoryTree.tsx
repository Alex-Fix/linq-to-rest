import { Tree, Typography } from "antd";
import type { DataNode } from "antd/es/tree";

export interface CategoryNode {
  readonly id: number;
  readonly name: string;
  readonly parentId: number | null;
  readonly productCount: number;
}

/** The ids of a category and every category below it. */
export function descendants(categories: readonly CategoryNode[], id: number): number[] {
  return [
    id,
    ...categories.filter((category) => category.parentId === id).flatMap((child) => descendants(categories, child.id)),
  ];
}

/** The flat rows as Ant Design tree data, children under their parent. */
export function treeData(categories: readonly CategoryNode[], parentId: number | null = null): DataNode[] {
  return categories
    .filter((category) => category.parentId === parentId)
    .map((category) => ({
      key: category.id,
      title: (
        <span>
          {category.name}
          {category.productCount > 0 && <Typography.Text type="secondary"> {category.productCount}</Typography.Text>}
        </span>
      ),
      children: treeData(categories, category.id),
    }));
}

export function CategoryTree({
  categories,
  selected,
  onSelect,
}: {
  readonly categories: readonly CategoryNode[];
  readonly selected: number | undefined;
  readonly onSelect: (id: number | undefined) => void;
}) {
  return (
    <Tree
      blockNode
      defaultExpandAll
      key={categories.length}
      treeData={treeData(categories)}
      selectedKeys={selected === undefined ? [] : [selected]}
      onSelect={(keys) => {
        onSelect(keys.length === 0 ? undefined : Number(keys[0]));
      }}
    />
  );
}
