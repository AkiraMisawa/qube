import type { ShiftEdit, ShiftRow } from './contracts';

// The Scenarios grid (ADR-0010 §4): one row per shift in the order made, then blank rows a paste may fill. Its
// positions are the grid's; what a cell means is the server's, which reads each as its column reads it. A scenario
// command's line (`scenario`, `scenario ladder`, …) is read by the server alone (ADR-0016 note 32): nothing here reads one.

/** The grid's columns that take a value, in the grid's order; Category and Shift type follow the index and the size. */
export const SHIFT_COLUMNS = ['scenario', 'category', 'index', 'factor', 'type', 'size', 'description'] as const;
export type ShiftColumn = (typeof SHIFT_COLUMNS)[number];

/** How many blank rows the grid ends in: what a block pasted onto the first of them may fill. */
export const BLANK_ROWS = 100;

/** A row of the grid: a shift, or a blank row, at its place counted from 1. */
export interface ShiftGridRow {
  readonly key: string;
  readonly position: number;
  readonly shift: ShiftRow | undefined;
}

/** The grid's rows: every shift in the order made, then the blank rows. */
export function shiftGridRows(shifts: readonly ShiftRow[], blanks: number = BLANK_ROWS): readonly ShiftGridRow[] {
  return [
    ...shifts.map((shift, i) => ({ key: shift.id, position: i + 1, shift })),
    ...Array.from({ length: blanks }, (_, k) => ({ key: `blank-${k + 1}`, position: shifts.length + k + 1, shift: undefined })),
  ];
}

/** A cell of the grid by position, as a paste or a clear names it. */
export interface GridCell { readonly row: number; readonly column: number }

/** A rectangle of the grid, as a paste or a clear targets it. */
export interface GridRange { readonly topRow: number; readonly leftColumn: number; readonly rowCount: number; readonly columnCount: number }

/**
 * The edits a block pasted onto the grid makes (ADR-0010 §4): a row's cells as pasted, by column, in the grid's order.
 * A blank row with nothing pasted into it makes nothing; the server reads every cell and refuses the paste whole for one
 * that does not resolve.
 */
export function pastedEdits(rows: readonly ShiftGridRow[], targets: readonly GridRange[], valueFor: (cell: GridCell) => string): readonly ShiftEdit[] {
  return editsOver(rows, targets, valueFor).filter((e) => e.id !== null || Object.values(e.cells).some((v) => v !== null && v.trim() !== ''));
}

/**
 * The edits Delete makes over the Selection: each shift's cells covered cleared, a row with none left removed by the
 * server. A blank row has nothing to clear.
 */
export function clearedEdits(rows: readonly ShiftGridRow[], targets: readonly GridRange[]): readonly ShiftEdit[] {
  return editsOver(rows, targets, () => '').filter((e) => e.id !== null);
}

function editsOver(rows: readonly ShiftGridRow[], targets: readonly GridRange[], valueFor: (cell: GridCell) => string): ShiftEdit[] {
  const byRow = new Map<number, Record<string, string | null>>();
  for (const range of targets)
    for (let r = range.topRow; r < range.topRow + range.rowCount; r++)
      for (let c = range.leftColumn; c < range.leftColumn + range.columnCount; c++) {
        const column = SHIFT_COLUMNS[c];
        if (column === undefined) throw new Error(`Column ${c + 1} of the Scenarios grid takes no value.`);
        const cells = byRow.get(r) ?? {};
        cells[column] = valueFor({ row: r, column: c });
        byRow.set(r, cells);
      }
  return [...byRow].sort(([a], [b]) => a - b).map(([r, cells]) => {
    const row = rows[r];
    if (row === undefined) throw new Error(`Row ${r + 1} is past the Scenarios grid's last row.`);
    return { id: row.shift?.id ?? null, row: row.position, cells };
  });
}
