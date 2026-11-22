import type { GridColumn } from '@ex-grid/core';

/** Each column as it came, beside the same column headed on the left: one object per column for as long as it lives. */
const headedLeft = new WeakMap<object, object>();

/**
 * A column's header is left-aligned unless the column gives its own: a header is words, not a figure, so it starts at
 * its column's left edge whatever the cells under it do (owner, 2026-11-22; ADR-0009's note; docs/design-guidelines.md
 * §4). ExGrid heads a column as its `align` says unless told, so a column right-aligned by hand was headed on the
 * right. A column that gives `headerAlign` keeps it, and comes back as it came.
 *
 * A column given again comes back as the same object: ExGrid compares the columns one by one by identity, and keeps an
 * Auto width it measured, and a set of columns made once (ExGrid ADR-0003), only for the same column.
 */
export function labelledColumns<Row>(columns: readonly GridColumn<Row>[]): readonly GridColumn<Row>[] {
  return columns.map((column) => {
    if (column.headerAlign !== undefined) return column;
    const known = headedLeft.get(column);
    if (known !== undefined) return known as GridColumn<Row>;
    const labelled: GridColumn<Row> = { ...column, headerAlign: 'left' };
    headedLeft.set(column, labelled);
    return labelled;
  });
}
