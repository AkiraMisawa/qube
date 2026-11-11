import type { LineFilter, LinePlace, LineSelect, LineSort, LineView } from '../contracts';

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

/**
 * A grid as its page declares it to the line: the table its rows are of, the subset it shows, and its columns' names as
 * shown, in order. `unsorted` names the columns among them the grid itself does not sort by (ExGrid's `queryable:
 * false`), where its page holds every row and the grid is what sorts them.
 */
export interface ScreenGrid {
  readonly table: string;
  readonly subset?: string;
  readonly columns: readonly string[];
  readonly unsorted?: readonly string[];
}

interface Sort {
  readonly column: string;
  readonly direction: 'ascending' | 'descending';
}

/**
 * A grid's Filter as the line reads it (ExGrid's `GridFilter`): the filters of its own columns, which are the grid's to
 * read, and the Opaque Filter, which is its Consumer's. The conditions a line gives a grid (ADR-0016 note 41) are kept
 * there, as typed.
 */
export interface Filter<Spec = unknown> {
  readonly columns: Readonly<Record<string, Spec>>;
  readonly opaque?: unknown;
}

/** The conditions a grid holds from the line, read from its Filter; none where the line gave it none. */
export function whereOf(filter: Filter | undefined): string | undefined {
  const opaque = filter?.opaque;
  if (opaque === undefined) return undefined;
  const where = typeof opaque === 'object' && opaque !== null && 'where' in opaque ? opaque.where : undefined;
  if (typeof where !== 'string') throw new Error('A grid holds an Opaque Filter the command line did not give it.');
  return where;
}

/** Whether a grid's Filter narrows its rows by its own columns: which rows those leave only the grid's source knows. */
export const narrows = (filter: Filter | undefined): boolean => filter !== undefined && Object.keys(filter.columns).length > 0;

/** One answer of a paged grid's reader: the sort and the conditions it was asked for, and the token of the order it answered in. */
export interface Answered {
  readonly sorts: readonly Sort[];
  readonly where?: string;
  readonly orderToken: string;
}

const sameOrder = (a: Answered, b: Answered): boolean =>
  a.orderToken === b.orderToken && a.where === b.where
  && a.sorts.length === b.sorts.length && a.sorts.every((s, i) => s.column === b.sorts[i]!.column && s.direction === b.sorts[i]!.direction);

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
 * not all of one order, and where none is in hand. The conditions are the ones the source holds from the line (note
 * 41), whether or not its rows have arrived under them, so that a line chained on `filter` is read under them; the
 * order is left out until every row in hand was answered under them.
 */
export function pagedView<Row>(
  grid: ScreenGrid,
  read: GridRead,
  source: { readonly window: readonly Row[]; readonly rowSequenceVersion: number; readonly sorts: readonly Sort[]; readonly filter: Filter | undefined },
  answered: (row: Row) => Answered | undefined,
): LineView {
  const where = whereOf(source.filter);
  const first = source.window.length === 0 ? undefined : answered(source.window[0]!);
  const drawn = first !== undefined && first.where === where && read.rowSequenceVersion === source.rowSequenceVersion
    && source.window.every((row) => { const of = answered(row); return of !== undefined && sameOrder(of, first); }) ? first : undefined;
  const { ranges, focus } = read.selection;
  return {
    ...named(grid),
    sorts: (drawn?.sorts ?? source.sorts).map((s) => ({ column: s.column, descending: s.direction === 'descending' })),
    ...(where === undefined ? {} : { where }),
    ...(drawn === undefined ? {} : { order: drawn.orderToken }),
    ...(ranges.length === 0 ? {} : { ranges: ranges.map((r) => ({ start: r.topRow, count: r.rowCount })) }),
    ...(focus === undefined ? {} : { focus: { field: fieldAt(grid, focus.column), position: focus.row } }),
  };
}

/**
 * The view of a grid whose page holds every row: its rows are named by their keys, each once, in the order selected.
 * A selection written in a sequence the source has left is one the grid is about to drop, and names no row. Where the
 * source filters its rows by its own columns, the Window is what those filters leave, and the view names those rows:
 * `shown` is then the rows the user sees, never the table's (ADR-0016 §2). Conditions the line gave the grid (note 41)
 * are said as they are, and the view names no row for them: which rows they leave is the server's to read, at the
 * revision the line is read at, so a reading does not depend on how far the page has drawn them. Beside rows the
 * grid's own filters leave, the server reads the conditions among those rows.
 */
export function heldView<Row>(
  grid: ScreenGrid,
  read: GridRead,
  source: { readonly window: readonly Row[]; readonly windowStart: number; readonly rowSequenceVersion: number; readonly filter: Filter | undefined },
  keyOf: (row: Row) => string,
): LineView {
  const where = whereOf(source.filter);
  const shown = { ...(narrows(source.filter) ? { shown: source.window.map(keyOf) } : {}), ...(where === undefined ? {} : { where }) };
  if (read.rowSequenceVersion !== source.rowSequenceVersion) return { ...named(grid), ...shown };
  const keyAt = (position: number): string => {
    const row = source.window[position - source.windowStart];
    if (row === undefined) throw new Error(`A grid of ${grid.table} has row ${position} selected, and its page holds ${source.window.length} rows from ${source.windowStart}.`);
    return keyOf(row);
  };
  const { ranges, focus } = read.selection;
  const keys = [...new Set(ranges.flatMap((r) => Array.from({ length: r.rowCount }, (_, i) => keyAt(r.topRow + i))))];
  return {
    ...named(grid),
    ...shown,
    ...(keys.length === 0 ? {} : { keys }),
    ...(focus === undefined ? {} : { focus: { field: fieldAt(grid, focus.column), key: keyAt(focus.row) } }),
  };
}

/** What of a grid's source the line gives a sort and conditions to: ExGrid's `setSorts`, `setFilter` and the Filter in its `snapshot`. */
export interface Sourced<Spec> {
  snapshot(): { readonly filter: Filter<Spec> | undefined };
  setSorts(sorts: readonly Sort[]): void;
  setFilter(filter: Filter<Spec> | undefined): void;
}

/**
 * Gives a grid the sort a view verb's reading names (ADR-0016 note 40), as the grid's own Sort, which its header then
 * shows: by its columns' names, the major key first, and none to take the sort off. A field the grid shows no column
 * of, or does not sort by, is refused by name, and the grid keeps the sort it had. Whether the reader of a grid the
 * server pages sorts by a field was the server's to say before it answered the reading.
 */
export function sortGrid(grid: ScreenGrid, source: Pick<Sourced<unknown>, 'setSorts'>, asked: LineSort): string | undefined {
  const absent = asked.sorts.find((sort) => !grid.columns.includes(sort.column));
  if (absent !== undefined) return `The grid on screen shows no column ${absent.column} to sort by.`;
  const unsorted = asked.sorts.find((sort) => grid.unsorted?.includes(sort.column) === true);
  if (unsorted !== undefined) return `The grid on screen does not sort by ${unsorted.column}.`;
  source.setSorts(asked.sorts.map((sort) => ({ column: sort.column, direction: sort.descending ? 'descending' : 'ascending' })));
  return undefined;
}

/**
 * Gives a grid the conditions a view verb's reading names (ADR-0016 note 41), or takes them off: they are kept as its
 * source's Opaque Filter, beside the filters of the grid's own columns, which stay. Conditions before them are replaced.
 */
export function filterGrid<Spec>(source: Pick<Sourced<Spec>, 'snapshot' | 'setFilter'>, asked: Pick<LineFilter, 'where'>): undefined {
  const held = source.snapshot().filter;
  const columns = held === undefined ? {} : held.columns;
  if (asked.where !== undefined) source.setFilter({ columns, opaque: { where: asked.where } });
  else source.setFilter(Object.keys(columns).length === 0 ? undefined : { columns });
  return undefined;
}

/** What a Context Menu was opened over, as ExGrid hands it to a grid's `contextCommands`: the column, and the Selection in its Row Sequence Version. */
export interface MenuOpened {
  readonly column: string;
  readonly selection: readonly { readonly topRow: number; readonly rowCount: number }[];
  readonly rowSequenceVersion: number;
}

/**
 * The view a Context Menu's item is run with (ADR-0016 §4, note 25 of 2026-11-11): the grid's view (`of`) of the
 * Selection the menu was handed, with the cell the menu was opened on as the focused cell, its row by `key`. The
 * grid's own Focus is not read: inside a Selection of several cells it may be another cell, and "this cell" is the
 * one under the menu. The row is named by its key whether or not the server pages the grid, so no position is looked
 * for in rows the page may have been answered again since.
 */
export function menuView(of: (read: GridRead) => LineView, menu: MenuOpened, key: string): LineView {
  const ranges = menu.selection.map((r) => ({ topRow: r.topRow, rowCount: r.rowCount }));
  return { ...of({ rowSequenceVersion: menu.rowSequenceVersion, selection: { ranges, focus: undefined } }), focus: { field: menu.column, key } };
}

/**
 * What of a grid's handle a place is put with: ExGrid's `placeSelection`, which refuses a Row Sequence Version the grid
 * has left and a grid that keeps an edit open, and `readEditText`, by which the two are told apart.
 */
export interface Placing {
  placeSelection(
    range: { readonly topRow: number; readonly rowCount: number; readonly leftColumn: number; readonly columnCount: number },
    focus: { readonly row: number; readonly column: number },
    rowSequenceVersion: number,
  ): boolean;
  readEditText(): string | undefined;
}

/**
 * Puts a grid's Focus on the cell a view verb's reading names (ADR-0016 §9, note 21 of 2026-11-11), as one cell
 * selected: the row at the position the server answered, else the row of that key where the page holds every row and
 * says where each is (`rowOf`); the column by its name, the first where the reading names none. It is placed in the Row
 * Sequence Version the view was read in (`read`), so a grid that has moved since refuses it. Answers why where nothing
 * was placed: a page that holds part of its rows was answered no position because its view named no one order, the
 * grid has moved, or it keeps an edit open that it could not commit. The grid reveals the Focus itself; giving it the
 * keyboard is the page's.
 */
export function placeFocus(grid: ScreenGrid, handle: Placing, read: Pick<GridRead, 'rowSequenceVersion'>, place: LinePlace, rowOf?: (key: string) => number | undefined): string | undefined {
  const column = place.column === undefined ? 0 : grid.columns.indexOf(place.column);
  if (column < 0) return `The grid on screen shows no column ${String(place.column)}: name ${place.key} alone to go to its row.`;
  if (place.position === undefined && rowOf === undefined) return `The grid on screen held its rows in no one order when the line opened, so ${place.key} has no position in it: open the line again.`;
  const row = place.position ?? rowOf?.(place.key);
  if (row === undefined) return `${place.key} is no longer a row the grid on screen holds: open the line again.`;
  if (handle.placeSelection({ topRow: row, rowCount: 1, leftColumn: column, columnCount: 1 }, { row, column }, read.rowSequenceVersion)) return undefined;
  return unplaced(handle);
}

/** Why a grid refused a Selection placed in the version its view was read in: it has left that version, or it keeps an edit open. */
const unplaced = (handle: Pick<Placing, 'readEditText'>): string => (handle.readEditText() === undefined
  ? 'The grid on screen has moved since the line opened: open the line again.'
  : 'The grid on screen keeps an edit open that it could not commit: correct or cancel it first.');

/**
 * Places a grid's Selection on the rows a view verb's reading names (ADR-0016 note 43 of 2026-11-11): one run of whole
 * rows, with the Focus on the first of them in the column it was on, the first where it had none. Where the server
 * pages the grid the run is its first position and its count, as the server answered them of the view's order. Where
 * the page holds every row the reading names the rows by their keys, and whether the grid shows them next to one
 * another is said here, by where the page says each is (`rowOf`): rows apart are refused with how many runs they are
 * in, never the first run alone. Placed in the Row Sequence Version the view was read in (`read`), as a place is.
 * Answers why where nothing was placed.
 */
export function selectRows(grid: ScreenGrid, handle: Placing, read: Pick<GridRead, 'rowSequenceVersion'>, asked: LineSelect, rowOf?: (key: string) => number | undefined): string | undefined {
  const column = asked.column === undefined ? 0 : grid.columns.indexOf(asked.column);
  if (column < 0) throw new Error(`The Focus was read on a column ${String(asked.column)} of a grid of ${grid.table}, and its page names no such column.`);
  let top: number;
  let count: number;
  if (asked.position !== undefined) [top, count] = [asked.position, asked.count];
  else {
    const { keys } = asked;
    if (keys === undefined || keys.length === 0) throw new Error(`The server asked for ${asked.count} rows of ${grid.table} to be selected and named neither their position nor their keys.`);
    if (rowOf === undefined) return 'The grid on screen held its rows in no one order when the line opened, so the rows have no position in it: open the line again.';
    const rows: number[] = [];
    for (const key of keys) {
      const row = rowOf(key);
      if (row === undefined) return `${key} is no longer a row the grid on screen holds: open the line again.`;
      rows.push(row);
    }
    rows.sort((a, b) => a - b);
    const runs = rows.filter((row, at) => at === 0 || row !== rows[at - 1]! + 1).length;
    if (runs > 1) return `The ${rows.length} rows are in ${runs} runs in the grid on screen, and the line selects rows that are next to one another: filter or sort the grid so that they are, then select shown.`;
    [top, count] = [rows[0]!, rows.length];
  }
  if (handle.placeSelection({ topRow: top, rowCount: count, leftColumn: 0, columnCount: grid.columns.length }, { row: top, column }, read.rowSequenceVersion)) return undefined;
  return unplaced(handle);
}
