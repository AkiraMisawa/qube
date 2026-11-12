import { useLayoutEffect, useRef, type RefObject } from 'react';
import type { CellPosition, GridColumn, GridFilter, GridSource, GridSourceState, GridViewRead, SelectionRange, SortSpec } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { panelKey, usePanelId } from '../panelState';
import { useWorkspaceContext } from '../workspace/WorkspaceProvider';

// A grid's view, kept by its panel while the panel is hidden (ADR-0006 note 10): the sort and the filter its source
// holds, and its Selection. A grid drawn again takes the sort and the filter back at once. Its Selection comes back only
// onto the rows it was made among: positions mean rows only in one order (ExGrid ADR-0011), so the Selection is placed
// again where its rows' order is the one it was read in, named by the page (`OrderOf`), and its columns are the ones it
// was read among. Anything else leaves nothing selected, as ExGrid leaves a grid whose order moved, rather than select
// other rows. A Selection of several ranges is not kept: ExGrid places one range and its Focus from outside a grid
// (`placeSelection`), and a view it read is handed back only to the grid that read it (`showViewWith`). The grid's
// scroll is not kept: the Focus placed is revealed.

/** A grid's view as its panel keeps it. */
interface KeptGrid {
  readonly sorts: readonly SortSpec[];
  readonly filter: GridFilter | undefined;
  readonly selection: KeptSelection | undefined;
}

/** A Selection of one range, with what makes its positions mean the same cells again. */
export interface KeptSelection {
  readonly range: SelectionRange;
  readonly focus: CellPosition;
  /** The grid's columns by name, in order, as its column positions were read among. */
  readonly columns: readonly string[];
  /** The order of the rows its positions were read in (`OrderOf`). */
  readonly order: string;
}

const GRID = panelKey<KeptGrid>('grid');

/**
 * Names the order of the rows a source holds now: one name only where every position holds the same row, so that a
 * position read under it means that row again. Undefined while no rows are in hand to say.
 */
export type OrderOf<Row> = (state: GridSourceState<Row>) => string | undefined;

/** The order of a source that holds every row of its result (`GridSource.from`), named by the rows' keys in turn. */
export function keysInOrder<Row>(keyOf: (row: Row) => string): OrderOf<Row> {
  return (state) => (state.window.length === 0 ? undefined : JSON.stringify(state.window.map(keyOf)));
}

/**
 * The order of rows that never change, such as a Run's trace (retained data is immutable): positions under one sort hold
 * the same rows, so the order is named by the sort alone.
 */
export function rowsThatNeverChange<Row>(): OrderOf<Row> {
  return (state) => (state.window.length === 0 ? undefined : JSON.stringify(state.sorts));
}

/**
 * Keeps a grid's view by its panel under `name`, a name no other grid of the page takes, and gives it back as the grid
 * is drawn again. Called by the component that holds the source and draws the grid, after it makes the source.
 */
export function useKeptGrid<Row>(
  name: string,
  source: GridSource<Row>,
  grid: RefObject<ExGridApi<Row> | null>,
  columns: readonly GridColumn<Row>[],
  orderOf: OrderOf<Row>,
): void {
  const panelId = usePanelId();
  const { screens } = useWorkspaceContext();
  // Read as the grid goes, which may be renders after the effect below last ran.
  const latest = useRef({ columns, orderOf });
  latest.current = { columns, orderOf };
  // What is still to give back to this source: taken by the grid once it is drawn, which may be after the source is made.
  const owed = useRef<{ readonly source: GridSource<Row>; readonly kept: KeptGrid } | undefined>(undefined);
  // The Selection given back and not yet decided, while no rows are in hand to decide it by, and the listener that waits for them.
  const undecided = useRef<{ readonly selection: KeptSelection; readonly stop: () => void } | undefined>(undefined);

  useLayoutEffect(() => {
    owed.current = screens.has(panelId, GRID, name) ? { source, kept: screens.get(panelId, GRID, name) } : undefined;
    return () => {
      // A grid that goes before it took back what it kept keeps it still: a page shown and hidden again at once, or
      // drawn twice on mounting (React's Strict Mode), loses nothing.
      const due = owed.current;
      const waiting = undecided.current;
      owed.current = undefined;
      undecided.current = undefined;
      waiting?.stop();
      const now = due?.kept ?? readKept(source, grid.current, latest.current.columns, latest.current.orderOf);
      screens.set(panelId, GRID, waiting === undefined ? now : { ...now, selection: waiting.selection }, name);
    };
  }, [screens, panelId, name, source, grid]);

  // A source that holds every row takes a sort only once the grid has given it its columns, so what is owed waits for
  // the grid to be drawn: in this commit where it is drawn with the page, in a later one where it is drawn later.
  useLayoutEffect(() => {
    const due = owed.current;
    if (due === undefined || grid.current === null) return;
    owed.current = undefined;
    const { kept } = due;
    if (kept.sorts.length > 0) due.source.setSorts(kept.sorts);
    if (kept.filter !== undefined) due.source.setFilter(kept.filter);
    const selection = kept.selection;
    if (selection === undefined) return;
    // Decided once, as the first rows are in hand under the sort and filter given back.
    const decide = (): boolean => {
      const placement = placementOf(selection, due.source.snapshot(), latest.current.columns, latest.current.orderOf);
      if (placement.kind === 'pending') return false;
      const drawn = grid.current;
      if (placement.kind === 'place' && drawn !== null) drawn.placeSelection(placement.range, placement.focus, placement.rowSequenceVersion);
      return true;
    };
    if (decide()) return;
    const stop = due.source.subscribe(() => {
      if (!decide()) return;
      stop();
      undecided.current = undefined;
    });
    undecided.current = { selection, stop };
  });
}

/** What a grid shows now, to keep: its source's sort and filter, and its Selection where one range of it can be placed again. */
function readKept<Row>(source: GridSource<Row>, grid: ExGridApi<Row> | null, columns: readonly GridColumn<Row>[], orderOf: OrderOf<Row>): KeptGrid {
  const state = source.snapshot();
  return { sorts: state.sorts, filter: state.filter, selection: grid === null ? undefined : keptSelection(grid.readView(), state, columns, orderOf) };
}

/**
 * The Selection of a grid's view to keep, with the order and the columns its positions were read in: none where it is
 * empty, of several ranges, or read in another Row Sequence Version than the source holds.
 */
export function keptSelection<Row>(view: Pick<GridViewRead, 'rowSequenceVersion' | 'selection'>, state: GridSourceState<Row>, columns: readonly GridColumn<Row>[], orderOf: OrderOf<Row>): KeptSelection | undefined {
  const { ranges, focus } = view.selection;
  const [range] = ranges;
  if (ranges.length !== 1 || range === undefined || focus === undefined) return undefined;
  // Positions the grid read in another order than its source holds name no rows the source can say.
  if (view.rowSequenceVersion !== state.rowSequenceVersion) return undefined;
  const order = orderOf(state);
  return order === undefined ? undefined : { range, focus, columns: columns.map((c) => c.name), order };
}

/** Where a kept Selection goes as a grid is drawn again: undecided while no rows are in hand, then placed or not at all. */
export type Placement =
  | { readonly kind: 'pending' }
  | { readonly kind: 'none' }
  | { readonly kind: 'place'; readonly range: SelectionRange; readonly focus: CellPosition; readonly rowSequenceVersion: number };

/**
 * Whether a kept Selection is placed on the rows a source holds now: where they are in the order it was read in and the
 * grid shows the columns it was read among, at the source's Row Sequence Version; otherwise nowhere, never on other cells.
 */
export function placementOf<Row>(selection: KeptSelection, state: GridSourceState<Row>, columns: readonly GridColumn<Row>[], orderOf: OrderOf<Row>): Placement {
  const order = orderOf(state);
  if (order === undefined) return { kind: 'pending' };
  if (order !== selection.order || !sameNames(columns, selection.columns)) return { kind: 'none' };
  return { kind: 'place', range: selection.range, focus: selection.focus, rowSequenceVersion: state.rowSequenceVersion };
}

const sameNames = <Row>(columns: readonly GridColumn<Row>[], names: readonly string[]): boolean =>
  columns.length === names.length && columns.every((c, i) => c.name === names[i]);
