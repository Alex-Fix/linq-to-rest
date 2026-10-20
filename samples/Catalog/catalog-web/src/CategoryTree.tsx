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

export function CategoryTree({
  categories,
  selected,
  onSelect,
  parentId = null,
}: {
  readonly categories: readonly CategoryNode[];
  readonly selected: number | undefined;
  readonly onSelect: (id: number | undefined) => void;
  readonly parentId?: number | null;
}) {
  const children = categories.filter((category) => category.parentId === parentId);
  if (children.length === 0) {
    return null;
  }

  return (
    <ul className="tree">
      {parentId === null && (
        <li>
          <button
            type="button"
            aria-pressed={selected === undefined}
            onClick={() => {
              onSelect(undefined);
            }}
          >
            All
          </button>
        </li>
      )}
      {children.map((category) => (
        <li key={category.id}>
          <button
            type="button"
            aria-pressed={selected === category.id}
            onClick={() => {
              onSelect(category.id);
            }}
          >
            {category.name}
            {category.productCount > 0 && <span className="muted"> ({category.productCount})</span>}
          </button>
          <CategoryTree categories={categories} selected={selected} onSelect={onSelect} parentId={category.id} />
        </li>
      ))}
    </ul>
  );
}
