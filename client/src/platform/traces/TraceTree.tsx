import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef } from 'react';
import { ActionIcon, Button, Text } from '@mantine/core';
import { IconChevronDown, IconChevronRight } from '@tabler/icons-react';
import { GridSource, type CellState, type GridColumn, type GridDeclaredKeyPress, type GridSelection, type GridTemplateCell } from '@ex-grid/core';
import { templateColumn, type ExGridApi } from '@ex-grid/react';
import { WorkbenchGrid } from '../grid/WorkbenchGrid';
import type { TraceHeader } from './contracts';
import { symbolTex } from './formula';
import { Tex } from './Tex';
import type { TreeRow } from './tree';
import { citationCell, diffCell, governedCell, sideCell, stateCell, unitText } from './words';

// The tree of terms (ADR-0011 §6) in ExGrid, drawn as ExPivot draws its Compact form: the term column is a pinned
// Template Column with the label, its indent and the open/close button; the grid reports the press, and the page holds
// what is open and pushes the rows. The grid neither sorts nor filters the tree: its order is the formula's or the
// change's, which the server gives. Alt+→ opens the term at the Focus and Alt+← closes it or goes to its parent;
// → and ← stay the grid's, between columns (DEFAULT 2026-11-08, ADR-0011 §6's note).

const ROW_HEIGHT = 28;
const OPEN_KEY = 'Alt+ArrowRight';
const CLOSE_KEY = 'Alt+ArrowLeft';
const DECLARED_KEYS = [OPEN_KEY, CLOSE_KEY];

interface TreeActions {
  readonly toggle: (row: TreeRow) => void;
  readonly more: (row: TreeRow) => void;
}

const Actions = createContext<TreeActions | undefined>(undefined);

/** The term cell: indent, the open/close button, and the label; a loading, more or failed row says what it is. */
function TermCell({ cell }: { readonly cell: GridTemplateCell<TreeRow> }) {
  const actions = useContext(Actions);
  if (actions === undefined) throw new Error('A term cell is drawn only inside the trace tree.');
  const button = useRef<HTMLButtonElement>(null);
  const acted = useRef(0);
  const row = cell.row;
  // Space on the cell hands the keyboard to its button, once per request (ex-grid ADR-0037).
  useLayoutEffect(() => {
    if (cell.focusRequest === 0 || cell.focusRequest === acted.current || button.current === null) return;
    acted.current = cell.focusRequest;
    cell.takeKeyboard(button.current);
  }, [cell]);
  const indent = { paddingInlineStart: `${row.depth * 16}px` };
  if (row.kind === 'more') {
    return (
      <div className="qwb-trace-term" style={indent}>
        <Button ref={button} size="compact-xs" variant="subtle" tabIndex={-1} className="ex-interactive" onClick={() => actions.more(row)}>Read {row.note}</Button>
      </div>
    );
  }
  if (row.kind !== 'term') {
    return <div className="qwb-trace-term" style={indent}><Text size="xs" {...(row.kind === 'failed' ? { c: 'red' } : {})} truncate>{row.note}</Text></div>;
  }
  const label = row.term?.label ?? row.termId;
  return (
    <div className="qwb-trace-term" style={indent} data-open={row.open}>
      {row.opens ? (
        <ActionIcon ref={button} size="sm" variant="subtle" tabIndex={-1} className="ex-interactive" aria-expanded={row.open}
          aria-label={`${row.open ? 'Close' : 'Open'} ${label}`} title={`${row.open ? 'Close' : 'Open'} ${label} (${row.open ? 'Alt+←' : 'Alt+→'})`}
          onClick={() => actions.toggle(row)}>
          {row.open ? <IconChevronDown size={14} stroke={1.75} /> : <IconChevronRight size={14} stroke={1.75} />}
        </ActionIcon>
      ) : <span className="qwb-trace-leaf" aria-hidden />}
      <span className="qwb-trace-label" title={label}>{label}</span>
    </div>
  );
}

// A symbol that only repeats its label (a decision's `Exposure class`) is left out of its column.
const symbolOf = (r: TreeRow) => (r.term === undefined || r.term.symbol === r.term.label ? '' : r.term.symbol);
const termColumn = templateColumn<TreeRow>({ name: 'term', header: 'Term', type: 'text', value: (r) => r.term?.label ?? r.note ?? '', width: 260, queryable: false },
  (cell) => <TermCell cell={cell} />);
const symbolColumn = templateColumn<TreeRow>({ name: 'symbol', header: 'Symbol', type: 'text', value: symbolOf, width: 96, queryable: false },
  (cell) => (symbolOf(cell.row) === '' ? null : <Tex tex={symbolTex(symbolOf(cell.row))} />));

/** A figure as the tree holds it: an amount to the cent, any other figure to six digits, as the formula writes them. */
const figureOf = (value: number | string | undefined, unit: { readonly kind: string } | null | undefined) =>
  typeof value !== 'number' ? value : unit?.kind === 'amount' ? Math.round(value * 100) / 100 : Number(value.toPrecision(6));
const figureText = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 6 });

function columnsOf(header: TraceHeader, allocated: boolean): readonly GridColumn<TreeRow>[] {
  const figure = (name: string, title: string, value: (r: TreeRow) => number | string | undefined): GridColumn<TreeRow> => ({
    name, header: title, type: 'number', width: 150, queryable: false,
    value: (r) => (r.term === undefined ? undefined : figureOf(value(r), r.term.unit)),
    format: (v) => (typeof v === 'number' ? figureText.format(v) : String(v)),
  });
  const text = (name: string, title: string, value: (r: TreeRow) => string, width: number): GridColumn<TreeRow> =>
    ({ name, header: title, type: 'text', width: { auto: true, minPx: width, maxPx: 360 }, queryable: false, value });
  const last = header.sides.at(-1)!;
  return [
    termColumn,
    symbolColumn,
    ...header.sides.map((side) => figure(side, side === 'before' ? 'Before' : 'After', (r) => sideCell(r.term![side], r.term!.unit))),
    // A case priced on one side has no Diff column at all, not an empty one (ADR-0011 §4).
    ...(header.sides.length === 2 ? [figure('diff', 'Diff', (r) => diffCell(r.term!))] : []),
    // An allocation is additive where the Diff is not, so it is a column of its own, shown when the provider gave one (§1, §4).
    ...(allocated ? [figure('allocation', header.sides.length === 2 ? 'Allocation (After)' : 'Allocation', (r) => r.term![last]?.allocation?.value)] : []),
    text('unit', 'Unit', (r) => (r.term === undefined ? '' : unitText(r.term.unit, header)), 64),
    text('state', 'State', (r) => (r.term === undefined ? '' : stateCell(r.term, header)), 96),
    text('governed', 'Governed by', (r) => (r.term === undefined ? '' : governedCell(r.term, header)), 96),
    text('citation', 'Citation', (r) => (r.term === undefined ? '' : citationCell(r.term, header)), 96),
  ];
}

/** An amount or ratio: a number column; a decision's text or a state's word is text, shown whole, never `####`. */
const cellType = (row: TreeRow, column: GridColumn<TreeRow>) => (typeof column.value?.(row) === 'number' ? 'number' as const : 'text' as const);

/** Absent is drawn faint (missing), a figure with no value it can stand behind as an error; the words say which. */
function cellState(row: TreeRow, column: GridColumn<TreeRow>): CellState {
  const term = row.term;
  if (term === undefined) return row.kind === 'failed' ? 'error' : 'normal';
  const sides = column.name === 'before' ? [term.before] : column.name === 'after' ? [term.after] : column.name === 'diff' || column.name === 'term' ? [term.before, term.after] : [];
  const priced = sides.filter((s) => s !== undefined && s !== null);
  if (priced.some((s) => s!.state === 'unavailable' || s!.state === 'notReturned' || s!.check.status === 'inconsistent')) return 'error';
  if (column.name === 'diff' && term.diff?.state === 'unavailable') return 'error';
  if (priced.length > 0 && priced.every((s) => s!.state === 'absent')) return 'missing';
  return 'normal';
}

export function TraceTree({ header, rows, selected, onSelect, onGoTo, onToggle, onMore, place, registerFocus }: {
  readonly header: TraceHeader;
  readonly rows: readonly TreeRow[];
  readonly selected: string | undefined;
  /** The grid moved its Focus onto a term. */
  readonly onSelect: (id: string) => void;
  /** A key asks for a term the Focus is not on (Alt+← on a closed term, its parent): the Focus goes there. */
  readonly onGoTo: (id: string) => void;
  readonly onToggle: (id: string, open: boolean) => void;
  readonly onMore: (id: string) => void;
  /** Asks the grid to put its Focus on the selected term once its row is among those it holds; numbered per ask. */
  readonly place: number;
  /** Gives the panel's "focus grid" command this grid's keyboard. */
  readonly registerFocus: (focus: (() => void) | undefined) => void;
}) {
  const grid = useRef<ExGridApi<TreeRow>>(null);
  useEffect(() => {
    registerFocus(() => grid.current?.focus());
    return () => registerFocus(undefined);
  }, [registerFocus]);
  // One source for the tree's life, its rows replaced whole and paired by key, so a row that did not change keeps
  // its paint and the Focus survives a change that moves no row (ADR-0141).
  const source = useMemo(() => GridSource.from<TreeRow>([], { rowKey: (r) => r.key }), []);
  const allocated = rows.some((r) => header.sides.some((side) => r.term?.[side]?.allocation !== undefined && r.term?.[side]?.allocation !== null));
  const columns = useMemo(() => columnsOf(header, allocated), [header, allocated]);
  const handlers = useRef({ onSelect, onGoTo, onToggle, onMore });
  handlers.current = { onSelect, onGoTo, onToggle, onMore };
  const selectedRef = useRef(selected);
  selectedRef.current = selected;

  // A tree is no live feed: its rows are put out at once, not gathered over an interval (ADR-0141), so the rows the
  // grid holds are the rows the page drew, and a placement made for them is made under the grid's own version.
  useEffect(() => { source.replaceAll(rows); source.publishGathered(); }, [source, rows]);

  // Put the Focus on the selected term's row, under the Row Sequence Version that holds it: a placement for rows the
  // grid has moved past is refused by the grid, and one still waiting for its row is given up once the user moves the
  // Focus, never made late over what the user did since (spine 6).
  const pending = useRef<(() => void) | undefined>(undefined);
  useEffect(() => {
    if (place === 0) return undefined;
    const placeNow = () => {
      source.publishGathered();
      const state = source.snapshot();
      const at = state.window.findIndex((r) => r.kind === 'term' && r.termId === selected);
      if (at < 0 || grid.current === null) return false;
      const row = state.windowStart + at;
      // Refused only for rows the grid has moved past; the next rows the source puts out are tried again.
      return grid.current.placeSelection({ topRow: row, leftColumn: 0, rowCount: 1, columnCount: 1 }, { row, column: 0 }, state.rowSequenceVersion);
    };
    if (placeNow()) return undefined;
    const unsubscribe = source.subscribe(() => { if (placeNow()) stop(); });
    const stop = () => { unsubscribe(); if (pending.current === stop) pending.current = undefined; };
    pending.current = stop;
    return stop;
    // Only when asked, never on every new row: the Focus the user moves is the Selection's own.
  }, [place, source]);

  const rowAt = useCallback((position: number): TreeRow | undefined => {
    const state = source.snapshot();
    return state.window[position - state.windowStart];
  }, [source]);

  // The term the Focus is on, as the grid last said. The grid also says the Selection when rows move under a Focus that
  // stays on its term (a term above opening for a path asked for): that is no move of the user's, so it neither
  // selects nor gives up a placement waiting for its row.
  const focused = useRef<string | undefined>(undefined);
  const onSelectionChanged = useCallback((selection: GridSelection) => {
    const row = selection.focus === undefined ? undefined : rowAt(selection.focus.row);
    const termId = row?.kind === 'term' ? row.termId : undefined;
    if (termId === focused.current) return;
    focused.current = termId;
    if (termId === undefined) return;
    if (termId !== selectedRef.current) pending.current?.();
    handlers.current.onSelect(termId);
  }, [rowAt]);

  const onDeclaredKey = useCallback((press: GridDeclaredKeyPress) => {
    if (press.editOpen || press.selection.focus === undefined) return;
    const row = rowAt(press.selection.focus.row);
    if (row === undefined || row.kind !== 'term') return;
    if (press.key === OPEN_KEY && row.opens && !row.open) handlers.current.onToggle(row.termId, true);
    if (press.key === CLOSE_KEY) {
      if (row.open) { handlers.current.onToggle(row.termId, false); return; }
      // Left on a closed term goes to its parent, the nearest row above it one level up.
      const state = source.snapshot();
      const at = state.window.indexOf(row);
      const parent = state.window.slice(0, at).reverse().find((r) => r.kind === 'term' && r.depth === row.depth - 1);
      if (parent !== undefined) handlers.current.onGoTo(parent.termId);
    }
  }, [rowAt, source]);

  const actions = useMemo<TreeActions>(() => ({
    toggle: (row) => handlers.current.onToggle(row.termId, !row.open),
    more: (row) => handlers.current.onMore(row.termId),
  }), []);

  const height = Math.min(36 + Math.max(rows.length, 3) * ROW_HEIGHT, 36 + 14 * ROW_HEIGHT);
  return (
    <Actions.Provider value={actions}>
      <div aria-label="Terms" data-testid="trace-tree" data-rows={rows.length}>
        <WorkbenchGrid<TreeRow> ref={grid} columns={columns} source={source} density="compact" viewportWidth="stretch" viewportHeight={height}
          pinnedColumnCount={1} locale="en-US" headerClickSelects hideColumnMenu highlightFocusRow
          cellType={cellType} cellState={cellState} declaredKeys={DECLARED_KEYS} onDeclaredKey={onDeclaredKey} onSelectionChanged={onSelectionChanged} />
      </div>
    </Actions.Provider>
  );
}
