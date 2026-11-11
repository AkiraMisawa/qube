import type { LineView } from '../contracts';

// What a page reads of one of its grids for the command line (ADR-0016 §9, notes 16 and 17 of 2026-11-11): the view
// the line sends with each reading. A position is said only with the order it is a position in, as the grid's reader
// answered it for the rows on screen; where that cannot be said the order is left out, and the server refuses the
// positions by name. Nothing here knows a table or an application, and nothing of ExGrid's but the shape it reads.

/** What is read of a grid: its Selection's rows and its Focus, as positions in one Row Sequence Version (ExGrid's `readView()`). */
export interface GridRead {
  readonly rowSequenceVersion: number;
  readonly selection: {
    readonly ranges: readonly { readonly topRow: number; readonly rowCount: number }[];
    readonly focus: { readonly row: number; readonly column: number } | undefined;
  };
}

/** A grid as its page declares it to the line: the table its rows are of, the subset it shows, and its columns' names as shown, in order. */
export interface ScreenGrid {
  readonly table: string;
  readonly subset?: string;
  readonly columns: readonly string[];
}

interface Sort {
  readonly column: string;
  readonly direction: 'ascending' | 'descending';
}

/** One answer of a paged grid's reader: the sort it was asked for, and the token of the order it answered in. */
export interface Answered {
  readonly sorts: readonly Sort[];
  readonly orderToken: string;
}

const sameOrder = (a: Answered, b: Answered): boolean =>
  a.orderToken === b.orderToken && a.sorts.length === b.sorts.length && a.sorts.every((s, i) => s.column === b.sorts[i]!.column && s.direction === b.sorts[i]!.direction);

const named = (grid: ScreenGrid): Pick<LineView, 'table' | 'subset'> => ({ table: grid.table, ...(grid.subset === undefined ? {} : { subset: grid.subset }) });

/** The field of the cell the keyboard is on, by its column's name: a column the page did not name is a defect, never another column. */
function fieldAt(grid: ScreenGrid, column: number): string {
  const field = grid.columns[column];
  if (field === undefined) throw new Error(`The Focus is on column ${column} of a grid of ${grid.table}, and its page names ${grid.columns.length} columns.`);
  return field;
}

/**
 * The view of a grid the server pages. Its rows are named by position, and a position means a row only in the order
 * the rows on screen were answered in: the sort and the token are therefore read from those rows' own answers
 * (`answered`, which the page keeps as its reader answers), never from the answer that happened to arrive last. The
 * order is left out where the selection is written in another sequence than the source's, where the rows in hand are
 * not all of one order, and where none is in hand.
 */
export function pagedView<Row>(
  grid: ScreenGrid,
  read: GridRead,
  source: { readonly window: readonly Row[]; readonly rowSequenceVersion: number; readonly sorts: readonly Sort[] },
  answered: (row: Row) => Answered | undefined,
): LineView {
  const first = source.window.length === 0 ? undefined : answered(source.window[0]!);
  const drawn = first !== undefined && read.rowSequenceVersion === source.rowSequenceVersion
    && source.window.every((row) => { const of = answered(row); return of !== undefined && sameOrder(of, first); }) ? first : undefined;
  const { ranges, focus } = read.selection;
  return {
    ...named(grid),
    sorts: (drawn?.sorts ?? source.sorts).map((s) => ({ column: s.column, descending: s.direction === 'descending' })),
    ...(drawn === undefined ? {} : { order: drawn.orderToken }),
    ...(ranges.length === 0 ? {} : { ranges: ranges.map((r) => ({ start: r.topRow, count: r.rowCount })) }),
    ...(focus === undefined ? {} : { focus: { field: fieldAt(grid, focus.column), position: focus.row } }),
  };
}

/**
 * The view of a grid whose page holds every row: its rows are named by their keys, each once, in the order selected.
 * A selection written in a sequence the source has left is one the grid is about to drop, and names no row.
 */
export function heldView<Row>(
  grid: ScreenGrid,
  read: GridRead,
  source: { readonly window: readonly Row[]; readonly windowStart: number; readonly rowSequenceVersion: number },
  keyOf: (row: Row) => string,
): LineView {
  if (read.rowSequenceVersion !== source.rowSequenceVersion) return named(grid);
  const keyAt = (position: number): string => {
    const row = source.window[position - source.windowStart];
    if (row === undefined) throw new Error(`A grid of ${grid.table} has row ${position} selected, and its page holds ${source.window.length} rows from ${source.windowStart}.`);
    return keyOf(row);
  };
  const { ranges, focus } = read.selection;
  const keys = [...new Set(ranges.flatMap((r) => Array.from({ length: r.rowCount }, (_, i) => keyAt(r.topRow + i))))];
  return {
    ...named(grid),
    ...(keys.length === 0 ? {} : { keys }),
    ...(focus === undefined ? {} : { focus: { field: fieldAt(grid, focus.column), key: keyAt(focus.row) } }),
  };
}
