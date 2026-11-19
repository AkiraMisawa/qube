import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef } from 'react';
import { flushSync } from 'react-dom';
import { ActionIcon, Badge, Button, Text } from '@mantine/core';
import { IconChevronDown, IconChevronRight } from '@tabler/icons-react';
import { GridSource, type CellState, type GridColumn, type GridDeclaredKeyPress, type GridSelection, type GridTemplateCell } from '@ex-grid/core';
import { templateColumn, type ExGridApi } from '@ex-grid/react';
import { compactGridHeight } from '../grid/metrics';
import { QubeGrid } from '../grid/QubeGrid';
import type { TraceHeader } from './contracts';
import { symbolTex } from './formula';
import { Tex } from './Tex';
import { CHILDREN_WAIT_WORDS, KeyHolds, reportChildrenNotRead, traceWhere } from './keyHolds';
import { KeyHints, type KeyHint } from './KeyHints';
import { RowVersions, childrenShown, treeMove, type TreeRow } from './tree';
import { SIDE_TITLE, allocationCell, allocationDiffCell, citationCell, diffCell, governedCell, governsMark, sideCell, stateCell, unitText } from './words';
import { useColumnWidths } from '../grid/columnWidths';
import { telemetry } from '../telemetry';

// The tree of terms (ADR-0011 §6) in ExGrid, drawn as ExPivot draws its Compact form: the term column is a pinned
// Template Column with the label, its indent and the open/close button; the grid reports the press, and the page holds
// what is open and pushes the rows. The grid neither sorts nor filters the tree: its order is the formula's or the
// change's, which the server gives. Alt+→ opens the term at the Focus, and on an open term goes into its first child, so
// that pressed again and again it walks down; Alt+← closes the term or goes up to its parent. → and ← stay the grid's,
// between columns: ExGrid refuses a key it answers itself as a declared one (ex-grid ADR-0050 item 14), so the tree takes
// the two with Alt (ADR-0011 §6's notes of 2026-11-08 and 2026-11-18).

const OPEN_KEY = 'Alt+ArrowRight';
const CLOSE_KEY = 'Alt+ArrowLeft';
const DECLARED_KEYS = [OPEN_KEY, CLOSE_KEY];
// How many Row Sequence Versions of rows the tree keeps for the keys pressed under them: a press is read within a
// frame or two of the rows it was pressed on, so a handful covers keys sent at machine speed.
const KEPT_VERSIONS = 8;

/**
 * A key that opened a term or goes into it, holding the keys after it until the term's children are shown (going into
 * it, until the Focus has moved to the first), within `CHILDREN_WAIT_MS`. Past the bound it is given up, said in the
 * grid's live region and logged, and ExGrid takes the keys held behind it against the rows as they stand then.
 */
interface TreeHold {
  readonly termId: string;
  readonly label: string;
  readonly into: boolean;
  readonly column: number;
}

/** The label of a term the tree has a row of: a term's row always carries its term. */
function labelOf(rows: readonly TreeRow[], termId: string): string {
  const term = rows.find((r) => r.kind === 'term' && r.termId === termId)?.term;
  if (term === undefined) throw new Error(`The trace tree has no row of term ${termId}.`);
  return term.label;
}

/** What the grid's live region says when a key held for a term's children is given up: what the key did, and the keys after it. */
function givenUpSentence(hold: TreeHold): string {
  const notRead = `The terms below ${hold.label} were not read within ${CHILDREN_WAIT_WORDS}`;
  return hold.into
    ? `${notRead}, so Alt+→ stayed on it and the keys pressed after it were taken from there. Press Alt+→ again once they show.`
    : `${notRead}. It is open, and the keys pressed after Alt+→ were taken without them.`;
}

interface TreeActions {
  readonly toggle: (row: TreeRow) => void;
  readonly more: (row: TreeRow) => void;
  readonly header: TraceHeader;
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
      <div className="qube-trace-term" style={indent}>
        <Button ref={button} size="compact-xs" variant="subtle" tabIndex={-1} className="ex-interactive" onClick={() => actions.more(row)}>Read {row.note}</Button>
      </div>
    );
  }
  if (row.kind !== 'term') {
    return <div className="qube-trace-term" style={indent}><Text size="xs" {...(row.kind === 'failed' ? { c: 'red' } : {})} truncate>{row.note}</Text></div>;
  }
  const label = row.term?.label ?? row.termId;
  // The operand its parent's max took is marked, and the others drawn faint: an operand that did not govern (ADR-0011 §6).
  const mark = row.term === undefined ? undefined : governsMark(row.term, actions.header);
  return (
    <div className="qube-trace-term" style={indent} data-open={row.open} {...(mark === undefined ? {} : { 'data-governs': mark.governs })}>
      {row.opens ? (
        <ActionIcon ref={button} size="sm" variant="subtle" tabIndex={-1} className="ex-interactive" aria-expanded={row.open}
          aria-label={`${row.open ? 'Close' : 'Open'} ${label}`} title={`${row.open ? `Close ${label} (Alt+←)` : `Open ${label} (Alt+→)`}`}
          onClick={() => actions.toggle(row)}>
          {row.open ? <IconChevronDown size={14} stroke={1.75} /> : <IconChevronRight size={14} stroke={1.75} />}
        </ActionIcon>
      ) : <span className="qube-trace-leaf" aria-hidden />}
      <span className="qube-trace-label" title={label}>{label}</span>
      {mark !== undefined && <Badge size="sm" variant="light" {...(mark.governs ? {} : { color: 'gray' })} className="qube-trace-mark" data-testid="trace-governs">{mark.text}</Badge>}
    </div>
  );
}

// A symbol that only repeats its label (a decision's `Exposure class`) is left out of its column.
const symbolOf = (r: TreeRow) => (r.term === undefined || r.term.symbol === r.term.label ? '' : r.term.symbol);
const termColumn = templateColumn<TreeRow>({ name: 'term', header: 'Term', type: 'text', value: (r) => r.term?.label ?? r.note ?? '', width: 300, queryable: false },
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
  const both = header.sides.length === 2;
  return [
    termColumn,
    symbolColumn,
    ...header.sides.map((side) => figure(side, SIDE_TITLE[side], (r) => sideCell(r.term![side], r.term!.unit))),
    // A case priced on one side has no Diff column at all, not an empty one (ADR-0011 §4).
    ...(header.sides.length === 2 ? [figure('diff', 'Diff', (r) => diffCell(r.term!))] : []),
    // An allocation is additive where the Diff is not, so it has columns of its own, on each side and its change, shown
    // when the provider gave one (§1, §4; ADR-0012 §6).
    ...(allocated ? header.sides.map((side) => figure(`allocation.${side}`, both ? `Allocation ${SIDE_TITLE[side]}` : 'Allocation', (r) => allocationCell(r.term!, side))) : []),
    ...(allocated && both ? [figure('allocation.diff', 'Allocation Diff', (r) => allocationDiffCell(r.term!))] : []),
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
  /** A press asks for a term the Focus may not be on (the open or close button of another row): the Focus goes there. */
  readonly onGoTo: (id: string) => void;
  /** Opens or closes a term, and nothing else. The tree's keys call it inside `flushSync`, so that the rows it draws are
   * the grid's before the next key is read. */
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
  const declared = useMemo(() => columnsOf(header, allocated), [header, allocated]);
  const { columns, onColumnWidthChanged } = useColumnWidths('trace/tree', declared, { hideColumnMenu: true });
  const handlers = useRef({ onSelect, onGoTo, onToggle, onMore });
  handlers.current = { onSelect, onGoTo, onToggle, onMore };
  const selectedRef = useRef(selected);
  selectedRef.current = selected;
  // The last placement the page asked for (`place`), and the last the tree took up: while they differ, the page has asked
  // for a term the tree has not yet placed the Focus on, and that ask outranks keeping the Focus where it was. Written as
  // the page commits, before the rows are put out below, never by a render that may not commit.
  const placeAsked = useRef(place);
  const placeTaken = useRef(place);
  useLayoutEffect(() => { placeAsked.current = place; }, [place]);
  // A placement the page asked for that still waits for its term's row; the call gives it up.
  const pending = useRef<(() => void) | undefined>(undefined);

  // A tree is no live feed: its rows are put out at once, not gathered over an interval (ADR-0141), so the rows the
  // grid holds are the rows the page drew, and a placement made for them is made under the grid's own version. They are
  // put out as the page commits them (a layout effect), so that a key that opens or closes a term, drawn inside
  // `flushSync`, leaves the grid holding the rows it drew before the next key is read. The rows of the last few versions
  // are kept, so that a key is read against the rows it was pressed on (spine 6).
  const versions = useMemo(() => new RowVersions(KEPT_VERSIONS), []);
  const holds = useMemo(() => new KeyHolds<TreeHold>(), []);
  useLayoutEffect(() => {
    const before = grid.current?.readView();
    const kept = before === undefined ? undefined : versions.rowAt(before.rowSequenceVersion, before.selection.focus?.row);
    source.replaceAll(rows);
    source.publishGathered();
    const state = source.snapshot();
    versions.put(state.rowSequenceVersion, state.windowStart, state.window);
    // Rows that open or close around the Focus move the sequence, and the grid drops its Selection (ex-grid ADR-0011,
    // ADR-0141). The term at the Focus is the same term, so the Focus is put back on it, in its column: unless the page
    // asked for another term (an ask not yet taken up, or one still waiting for its row), or something placed a Focus as
    // the rows came, or the term's row went with them.
    const after = grid.current?.readView();
    const dropped = after !== undefined && (after.selection.focus === undefined || after.rowSequenceVersion !== state.rowSequenceVersion);
    const asked = placeAsked.current !== placeTaken.current || pending.current !== undefined;
    if (kept !== undefined && before?.selection.focus !== undefined && dropped && !asked) {
      placeOnKey(kept.key, before.selection.focus.column);
    }
    // A key held for a term's children goes on once they are shown, or once the term is no longer there to wait on.
    holds.release(landWhenShown);
  }, [source, rows]);
  // A tree taken away lets every key it holds go: the grid that held them goes with it.
  useEffect(() => () => holds.releaseAll(), [holds]);

  // Put the Focus on the selected term's row, under the Row Sequence Version that holds it: a placement for rows the
  // grid has moved past is refused by the grid, and one still waiting for its row is given up once the user moves the
  // Focus, never made late over what the user did since (spine 6).
  useEffect(() => {
    placeTaken.current = place;
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

  /**
   * Puts the Focus on the row of a key (a term's is its id), in a column, under the version the source holds now. A row
   * no longer there is no fault (a term above it closed); a placement the grid refuses is, since the grid took these
   * rows as they were put out, and is logged.
   */
  function placeOnKey(key: string, column: number): void {
    const state = source.snapshot();
    const at = state.window.findIndex((r) => r.key === key);
    if (at < 0 || grid.current === null) return;
    const row = state.windowStart + at;
    if (grid.current.placeSelection({ topRow: row, leftColumn: column, rowCount: 1, columnCount: 1 }, { row, column }, state.rowSequenceVersion)) return;
    telemetry.report({ level: 'warning', kind: 'trace-key', message: `The trace tree (${traceWhere(header)}) could not put its Focus on ${key} under row version ${state.rowSequenceVersion}: the grid refused it.` });
  }

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

  /** The term at the grid's Focus as it holds it now, read in the rows of the version its Selection is written in. */
  const focusedTerm = (): string | undefined => {
    const view = grid.current?.readView();
    const row = view === undefined ? undefined : versions.rowAt(view.rowSequenceVersion, view.selection.focus?.row);
    return row?.kind === 'term' ? row.termId : undefined;
  };

  /**
   * Lands a key held for a term's children once they are shown, or once the term is gone, and answers whether it did.
   * Going into the term, it puts the Focus on the first child, but only while the Focus is still on the term the key
   * was pressed on: a reader who moved it meanwhile, by a click or a command, keeps it where they put it.
   */
  function landWhenShown(hold: TreeHold): boolean {
    const shown = childrenShown(source.snapshot().window, hold.termId);
    if (shown.kind === 'reading') return false;
    if (hold.into && shown.kind === 'shown' && shown.first !== undefined && focusedTerm() === hold.termId) placeOnKey(shown.first, hold.column);
    return true;
  }

  /** Lands the key at once where the term's children are shown; otherwise holds the keys after it until they are. */
  const holdUntilShown = (hold: TreeHold): Promise<void> | undefined => {
    if (landWhenShown(hold)) return undefined;
    return holds.hold(hold, (given) => {
      grid.current?.announce(givenUpSentence(given));
      reportChildrenNotRead(header, given.termId, given.into
        ? 'Alt+→ did not go into it, and the keys held behind it were taken from it'
        : 'it was opened, and the keys held behind Alt+→ were taken without its children');
    });
  };

  // A key acts on the row it was pressed on, found by the position the press names in the rows of its version, and
  // then by its key in the rows the grid holds now, which a key just before it may have changed. What it does is read
  // from those rows: Alt+→ on a term just opened goes into it. A press under a version no longer kept is not acted on.
  // A key that opens a term or goes into it answers once the term's children are shown, and the grid holds the keys
  // pressed after it until then (ex-grid ADR-0050 item 14), so a reader at machine speed ends where a slow one does.
  const onDeclaredKey = (press: GridDeclaredKeyPress): Promise<void> | undefined => {
    const focus = press.selection.focus;
    if (press.editOpen || focus === undefined) return undefined;
    if (!versions.has(press.rowSequenceVersion)) {
      telemetry.report({ level: 'info', kind: 'trace-key', message: `${press.key} was pressed in the trace tree (${traceWhere(header)}) under row version ${press.rowSequenceVersion}, which it no longer keeps, so it was not acted on.` });
      return undefined;
    }
    const pressed = versions.rowAt(press.rowSequenceVersion, focus.row);
    const now = source.snapshot().window;
    const move = treeMove(now, pressed === undefined ? -1 : now.findIndex((r) => r.key === pressed.key), press.key === OPEN_KEY ? 'in' : 'out');
    switch (move.kind) {
      case 'open': {
        const label = labelOf(now, move.termId);
        flushSync(() => handlers.current.onToggle(move.termId, true));
        return holdUntilShown({ termId: move.termId, label, into: false, column: focus.column });
      }
      case 'into': return holdUntilShown({ termId: move.termId, label: labelOf(now, move.termId), into: true, column: focus.column });
      case 'close': flushSync(() => handlers.current.onToggle(move.termId, false)); return undefined;
      case 'goTo': placeOnKey(move.termId, focus.column); return undefined;
      case 'none': return undefined;
    }
  };
  // The grid is handed one function for its life; it reads the latest of what it calls.
  const keyHandler = useRef(onDeclaredKey);
  keyHandler.current = onDeclaredKey;
  const onDeclaredKeyStable = useCallback((press: GridDeclaredKeyPress) => keyHandler.current(press), []);

  const actions = useMemo<TreeActions>(() => ({
    toggle: (row) => { handlers.current.onToggle(row.termId, !row.open); handlers.current.onGoTo(row.termId); },
    more: (row) => handlers.current.onMore(row.termId),
    header,
  }), [header]);

  // As high as its rows, three at least and fourteen at most, by ExGrid's own Grid Metrics.
  const height = compactGridHeight(Math.min(Math.max(rows.length, 3), 14));
  return (
    <Actions.Provider value={actions}>
      <div aria-label="Terms" data-testid="trace-tree" data-rows={rows.length}>
        <QubeGrid<TreeRow> ref={grid} columns={columns} onColumnWidthChanged={onColumnWidthChanged} source={source} density="compact" viewportWidth="stretch" viewportHeight={height}
          pinnedColumnCount={1} locale="en-US" headerClickSelects hideColumnMenu highlightFocusRow
          cellType={cellType} cellState={cellState} declaredKeys={DECLARED_KEYS} onDeclaredKey={onDeclaredKeyStable} onSelectionChanged={onSelectionChanged} />
      </div>
      <KeyHints label="Keys in the tree" testId="trace-tree-keys" hints={TREE_KEYS} />
    </Actions.Provider>
  );
}

// The tree's keys, said under it, since Alt is not what a reader tries first: ↑ and ↓ move between terms and the formula
// follows, Alt+→ opens and goes in, Alt+← closes and goes up.
const TREE_KEYS: readonly KeyHint[] = [
  { keys: [['↑'], ['↓']], does: 'move' },
  { keys: [['Alt', '→']], does: 'open, then go in' },
  { keys: [['Alt', '←']], does: 'close, then go up' },
];
