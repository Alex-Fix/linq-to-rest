/** A JSON literal of the query tree. */
export type Literal = string | number | boolean | null;

/** A node of the query tree, exactly as it goes on the wire. */
export type QueryNode = Literal | Readonly<Record<string, unknown>>;

/** The `in` list, the one place a JSON array of literals appears. */
export type ListNode = readonly Literal[];

/** Converts a JavaScript value into a literal node, rejecting what has no wire form. */
export function literal(value: unknown): Literal {
  switch (typeof value) {
    case "string":
    case "boolean":
      return value;
    case "number":
      if (!Number.isFinite(value)) {
        throw new TypeError("LinqToRest can't send NaN or Infinity.");
      }

      return value;
    case "object":
      if (value === null) {
        return null;
      }

      if (value instanceof Date) {
        throw new TypeError("LinqToRest sends dates as ISO 8601 strings, not Date objects. Use value.toISOString().");
      }

      break;
    default:
      break;
  }

  throw new TypeError(`LinqToRest can't send a value of type ${typeof value}.`);
}
