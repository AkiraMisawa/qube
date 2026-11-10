import { useCallback, useMemo } from 'react';
import { gridHeaderRequiredPx, type GridColumn, type GridColumnWidth, type GridColumnWidthChange, type GridDensity } from '@ex-grid/core';
import { useWorkspace, useWorkspaceContext } from '../workspace/WorkspaceProvider';

// Column widths (ExGrid ADR-0016). A declared width is a default that never cuts its header; a value longer than the
// header may be cut until the user widens the column. ExGrid moves no width by itself: it reports a drag, the page
// hands the column back at the new width, and a grid given no handler offers no grip at all.

/** The widths the user dragged in one grid, by column name. */
export type ColumnWidths = Readonly<Record<string, number>>;

const NONE: ColumnWidths = {};

/** How the grid draws its headers, which sets what a header needs: the Rates line grid hides its column menu (ADR-0015 §4). */
export interface HeaderOptions {
  readonly density?: GridDensity;
  readonly hideColumnMenu?: boolean;
}

/**
 * A column at its default width: as declared, but never narrower than its header needs, charged as the grid charges
 * an Auto column's (the label, the column-menu button unless the grid hides it, and the sort mark's box when the column
 * sorts). An Auto width is the grid's own to compute, header included.
 */
export function headerFitted<Row>(column: GridColumn<Row>, options: HeaderOptions = {}): GridColumn<Row> {
  const width = column.width;
  if (width === undefined || (typeof width === 'object' && 'auto' in width)) return column;
  const declared = typeof width === 'number' ? width : width.px;
  const required = Math.ceil(gridHeaderRequiredPx(column.header ?? column.name,
    { density: options.density ?? 'compact', menuButton: options.hideColumnMenu !== true, sortable: column.queryable !== false }));
  if (declared >= required) return column;
  return { ...column, width: typeof width === 'number' ? required : { ...width, px: required } };
}

/** The bounds a dragged width keeps from the declaration, as ExGrid asks a column to be handed back. */
function boundsOf(width: GridColumnWidth | undefined): { readonly minPx?: number; readonly maxPx?: number } {
  if (width === undefined || typeof width === 'number') return {};
  return { ...(width.minPx === undefined ? {} : { minPx: width.minPx }), ...(width.maxPx === undefined ? {} : { maxPx: width.maxPx }) };
}

/** The columns as the grid is handed them: each at the width the user dragged it to, else at its default. */
export function columnsAt<Row>(declared: readonly GridColumn<Row>[], dragged: ColumnWidths, options: HeaderOptions = {}): readonly GridColumn<Row>[] {
  return declared.map((column) => {
    const px = dragged[column.name];
    return px === undefined ? headerFitted(column, options) : { ...column, width: { px, ...boundsOf(column.width) } };
  });
}

/**
 * One grid's columns at the user's widths, and the handler that records a drag. The widths are kept in the workspace
 * store by panel, since a panel's page is unmounted whenever another tab is shown, and go when the panel closes.
 */
export function useColumnWidths<Row>(panelId: string, grid: string, declared: readonly GridColumn<Row>[], options: { readonly hideColumnMenu?: boolean } = {}) {
  const { store } = useWorkspaceContext();
  const dragged = useWorkspace((s) => s.columnWidths[panelId]?.[grid]) ?? NONE;
  const hideColumnMenu = options.hideColumnMenu === true;
  const columns = useMemo(() => columnsAt(declared, dragged, { hideColumnMenu }), [declared, dragged, hideColumnMenu]);
  const onColumnWidthChanged = useCallback((change: GridColumnWidthChange) =>
    store.getState().setColumnWidth(panelId, grid, change.column, change.widthPx), [store, panelId, grid]);
  return { columns, onColumnWidthChanged };
}
