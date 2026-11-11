import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import './xva.css';
import { Alert, Badge, Button, Group, Modal, SegmentedControl, Select, Stack, Table, Text, TextInput, useMantineTheme } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridContextMenuContext, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import { GridUndoContext } from '../../platform/grid/gridUndo';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../platform/api';
import { useColumnWidths } from '../../platform/grid/columnWidths';
import { amountTone, formatAmount } from '../../platform/grid/amounts';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerGridFocus } from '../../shell/focus';
import type { VaryCell, XvaApi } from './api';
import type { EffectiveTrade, HeldEdit, RegisteredColumn, RegisteredMeasure, Resolution, ScenariosMade, SetSaved, ScopeDrop, ShiftEdit, Varied, XvaWorkspaceView } from './contracts';
import { measureNamed, registerXvaPage } from './commands';
import { commandDoor, registerCommandDoor } from '../../platform/commands/served';
import { pagedView, type Answered } from '../../platform/grid/lineView';
import { XvaResult } from './XvaResult';
import { BASE_CASE, BASE_SCENARIO, openTrace, requestTerm } from '../../platform/traces/open';
import type { TraceApi } from '../../platform/traces/api';
import type { LineView, RunRecord } from '../../platform/contracts';
import { dockTabs, METRICS_TAB, type DockTab, type MeasureTab } from './dock';
import { EditQueue, MEASURES_ENTITY, SETTINGS_ENTITY, TRADES_TABLE, refusedEdit, sideSaid, sideTitle, tradeFields, type PendingEdit, type SetText } from './editing';
import { ToolbarField } from './fields';
import { CHANGES_TAB, CREDIT_TAB, SCENARIOS_TAB, SETTINGS_TAB, shownTab, TAB_TITLES, tabShown, XvaInputs, type InputTab } from './XvaInputs';
import { XvaRisks } from './XvaRisks';
import type { ScenarioActions } from './XvaScenarios';
import type { ChangeActions } from './XvaChanges';
import { VARY_COMMAND, VaryDialog, addedText, variedText, type VaryTarget } from './VaryDialog';
import { generatedText, includedText, loadedText, savedText, scenariosText } from './scenarios';
import { compactGridHeight, GRID_FRAME, pixels, Resizer } from './layout';
import { declaring, measureOfField, membershipColumn, scopesOf } from './membership';
import { priceLabel, pricedMeasures } from './metrics';
import { differs, originalMessage } from './original';
import { copyRich, downloadRun } from '../../platform/runs';
import { cellMessage as moduleCellMessage, cellRefusal, cellState as moduleCellState, failed, moduleColumns, parseCellNumber } from './cells';

// The trace tab draws with the trace view and KaTeX, read once a tab is opened (as the trace panel is).
const TraceTab = lazy(() => import('../../platform/traces/TraceTab').then((m) => ({ default: m.TraceTab })));
// The Profile tab draws with ExChart, read once the tab is opened.
const XvaProfile = lazy(() => import('./XvaProfile').then((m) => ({ default: m.XvaProfile })));

// The fixed XVA workspace (docs/03, ADR-0003): the server holds the trades and every rule; this page shows
// the effective rows a page at a time and sends what the user changed. ExGrid draws; it decides nothing.

export const XVA_RESULT_VIEW = 'view.xva.result';

/** A typed term, such as a notional, reads back as typed, grouped; a figure the page shows is an amount (C48). */
const asTyped = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
const rate = new Intl.NumberFormat('en-US', { minimumFractionDigits: 5, maximumFractionDigits: 5 });
const text = (v: unknown) => String(v);

type Row = EffectiveTrade;

const column = (name: keyof Row & string, header: string, type: GridColumn<Row>['type'], width: number, extra: Partial<GridColumn<Row>> = {}): GridColumn<Row> => ({
  name, header, type, width, value: (row) => row[name] ?? undefined, ...extra,
});

/**
 * Made once: a new column is a new declaration, and every row paints again (ExGrid ADR-0003). Each width holds its
 * header and its widest value: the largest synthetic notional, 10,000,000,000, must not fall to `####`. The New and
 * Existing grids (ADR-0010 §6) share them; only an existing trade has a last fixing and an official PV.
 */
const leading: readonly GridColumn<Row>[] = [
  column('tradeId', 'Trade', 'text', 168),
  column('product', 'Product', 'text', 104),
  column('currency', 'Ccy', 'text', 76),
  column('notional', 'Notional', 'number', 168, { editable: true, format: (v) => asTyped.format(v as number) }),
  column('maturity', 'Maturity', 'text', 112),
  column('fixedRate', 'Fixed rate', 'number', 112, { editable: true, format: (v) => rate.format(v as number) }),
  column('schedule', 'Schedule', 'text', 104, { editable: true }),
];
const assigned: readonly GridColumn<Row>[] = [
  column('csa', 'CSA', 'text', 120, { editable: true }),
  column('curve', 'Curve', 'text', 112, { editable: true }),
];
/** The trade fields a selected measure needs on both grids (ADR-0010 §10's note of 2026-11-08): spot IM's IM CSAs, which
 * are assignments as the CSA is (ADR-0012 §2, §3), so they stand beside it. An empty one is no IM CSA. */
const inputColumns: Readonly<Record<string, GridColumn<Row>>> = {
  imR: column('imR', 'IM-R', 'text', 120, { editable: true }),
  imP: column('imP', 'IM-P', 'text', 120, { editable: true }),
};
const sides: readonly GridColumn<Row>[] = [
  column('before', 'Before', 'boolean', 88, { editable: true }),
  column('after', 'After', 'boolean', 80, { editable: true }),
];
/** What the screen calls each column of a trade grid the user may type in: an edit is titled with it (ADR-0006's note 2 of 2026-11-11). */
const TRADE_HEADERS: ReadonlyMap<string, string> = new Map([...leading, ...assigned, ...Object.values(inputColumns), ...sides]
  .flatMap((c): [string, string][] => (c.header === undefined ? [] : [[c.name, c.header]])));
/** The assignments, with the trade fields the selected measures need, then B and A. */
const assignmentOf = (fields: readonly string[]): readonly GridColumn<Row>[] =>
  [...assigned, ...fields.flatMap((f) => (inputColumns[f] === undefined ? [] : [inputColumns[f]])), ...sides];
// The selected modules' columns stand between B and A and these (ADR-0010 §6, §10): on the New grid, XVA's PV01,
// Margin and PV Adj; then a membership column per selected measure that declares one, such as FVA's.
const derived: readonly GridColumn<Row>[] = [
  { name: 'issues', header: 'Issues', type: 'text', width: 260, value: (row) => (row.issues.length === 0 ? undefined : row.issues.join(' ')), format: text },
];
const existingTerms: readonly GridColumn<Row>[] = [
  ...leading,
  column('lastFixing', 'Last fixing', 'number', 116, { format: (v) => rate.format(v as number) }),
  // The books' PV, the source's and read-only (ADR-0010 §6): the PV an adjustment uses.
  column('officialPv', 'Official PV', 'number', 152, { format: formatAmount, tone: amountTone }),
];

export type TradeGrid = 'new' | 'existing';

/** The trade grids, as a measure input's tab: a field there is a column of both (ADR-0010 §10's note of 2026-11-08). */
const TRADES_TAB = 'trades';

/**
 * The heights of the page's own parts (ADR-0010 §1), in pixels the page sets and never reads back. The input tabs fit
 * their content until the handle is dragged: Netting's one row, or the CSA grid's rows up to about seven. The dock's
 * Metrics take up to about four cases. What the context bar, the status line, the tab rows and the captions take
 * together is TAKEN; the Existing grid has the window's rest.
 */
const RESIZER = 12;
const DOCK_HEIGHT = 136;
const TAKEN = 260;
const CSA_ROWS = 7;
/** The Changes tab's rows it is as high as, until its handle is dragged. */
const CHANGE_ROWS = 6;
/** The Scenarios tab's rows before its grid scrolls: its shifts and blank rows to paste onto. */
const SHIFT_ROWS = 10;
/** The open tab's height as its content: Netting's row, the sentence that says there is no CSA or no IM CSA, the CSA grid
 * in its frame under the panel's top gap (`gap`, the theme's `xs`), or the IM CSAs' table a row each. The table's numbers
 * are geometry the theme has no name for: its header row with the panel's top gap and border (40px), and a row of `sm`
 * text at `xs` vertical spacing (28px), as drawn at 1440px (2026-11-08). The Credit tab is its row of controls (one
 * Control Height, 30px), the sentences that say the catalogue could not be read and why CVA is refused (20px each) and
 * the curve's one row; the Settings tab a
 * table a module, its header (32px) and a row a setting: a text input's 39px (the Control Height in 4px spacing and a
 * border), a switch's 30px. */
const inputsFit = (tab: InputTab, ws: XvaWorkspaceView, gap: number, catalogueRefused: boolean) => {
  switch (tab) {
    case 'netting': return 84;
    case 'im': return ws.agreement.imCsas.length === 0 ? 40 : 40 + 28 * ws.agreement.imCsas.length;
    // The Scenarios tab: its shifts and a few blank rows to type or paste onto, up to SHIFT_ROWS, beside its list.
    case SCENARIOS_TAB: return gap + GRID_FRAME + compactGridHeight(Math.min(ws.shifts.length + 4, SHIFT_ROWS));
    // The Changes tab: its header, a row per section and change, and the line under it, up to CHANGE_ROWS.
    case CHANGES_TAB: {
      const rows = ws.changes + ws.cases.length;
      return rows === 0 ? 40 : 72 + 32 * Math.min(rows, CHANGE_ROWS);
    }
    case CREDIT_TAB:
      return gap + 30 + (catalogueRefused ? gap + 20 : 0) + (ws.credit.missing == null ? 0 : gap + 20) + (ws.credit.cds == null ? 0 : gap + GRID_FRAME + compactGridHeight(1));
    case SETTINGS_TAB: return ws.settings.reduce((h, m) => h + gap + 32 + m.settings.reduce((r, x) => r + (x.form === 'flag' ? 30 : 39), 0), 0);
    case 'csa': return ws.csas.length === 0 ? 40 : gap + GRID_FRAME + compactGridHeight(Math.min(ws.csas.length, CSA_ROWS));
  }
};

/** The page's parts, which below `sm` are three tabs (ADR-0010 §1). */
type Section = 'inputs' | 'trades' | 'results';

// A value that differs from Original is marked (ADR-0010 §2), by value and not by edit: one a rule moved is marked, one
// edited back is not. ExGrid's `modified` mark stands in for the Before and After fills until its Split Fill ships.
function cellState(row: Row, col: GridColumn<Row>): CellState {
  if (row.held.some((h) => h.startsWith(`${col.name}:`))) return 'stale';
  // A module's column (ADR-0010 §6, §10): PV01 pending or failed, and a typed Margin against an empty Original.
  if (row.cells?.[col.name] !== undefined) return moduleCellState(row, col.name);
  if ((col.name === 'csa' || col.name === 'curve' || col.name === 'issues') && row.issues.length > 0) return 'error';
  if (differs(row, col.name)) return 'modified';
  return 'normal';
}

function cellMessage(row: Row, col: GridColumn<Row>): string | undefined {
  const held = row.held.find((h) => h.startsWith(`${col.name}:`));
  if (held !== undefined) return held.endsWith('conflict')
    ? 'The source changed under your edit. Keep it or take the source value in the panel above.'
    : 'This edit lost its trade in the new version. Acknowledge or reset it in the panel above.';
  if ((col.name === 'csa' || col.name === 'curve' || col.name === 'issues') && row.issues.length > 0) return row.issues.join(' ');
  if (row.cells?.[col.name] !== undefined) return moduleCellMessage(row, col.name);
  // A measure priced per scope says each scope's standing on its membership cell (ADR-0012 §2).
  const measure = measureOfField(col.name);
  if (measure !== undefined) return scopesOf(row, measure);
  return originalMessage(row, col.name, row[col.name as keyof Row]);
}

/** The failed cell's action (ADR-0010 §6): ask again for that trade's results. */
const RETRY = 'xva.retry';
const commandLabel = (id: string) => (id === RETRY ? 'Ask again' : id === VARY_COMMAND ? 'Vary values…' : undefined);

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

export function XvaWorkspacePanel({ panelId, workspaceId, api, traceApi }: {
  readonly panelId: string; readonly workspaceId: string; readonly api: XvaApi; readonly traceApi: TraceApi;
}) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const theme = useMantineTheme();
  // While a module's cell on a new trade waits for its result, such as PV01, the view is read again until none does.
  const view = useQuery({
    queryKey: ['xva', workspaceId], queryFn: () => api.view(workspaceId),
    refetchInterval: (query) => (query.state.data !== undefined && query.state.data.pendingCells > 0 ? 500 : false),
  });
  const registrations = useQuery({ queryKey: ['xva', 'registrations'], queryFn: () => api.registrations(), staleTime: Infinity });
  // The synthetic CDS catalogue the Credit tab searches and `cds` completes from (ADR-0010 §8's note of 2026-11-09).
  const catalogue = useQuery({ queryKey: ['xva', 'cds'], queryFn: () => api.cdsCatalogue(), staleTime: Infinity });
  const newGrid = useRef<ExGridApi<Row>>(null);
  const existingGrid = useRef<ExGridApi<Row>>(null);
  // The grid the keyboard was last in: `before all` and `after all` act on it, Existing until one was.
  const lastGrid = useRef<TradeGrid>('existing');
  // What the command line is told is on screen (ADR-0016 §9, note 16 of 2026-11-11): the grid of a table the keyboard
  // was last in, read when the line opens. None until one is entered, and none once a grid that is no table is.
  const screen = useRef<(() => LineView | undefined) | undefined>(undefined);
  const [busy, setBusy] = useState<string | undefined>(undefined);
  const [confirm, setConfirm] = useState<{ readonly registration: string; readonly drop: ScopeDrop } | undefined>(undefined);
  const [failure, setFailure] = useState<string | undefined>(undefined);
  const [issue, setIssue] = useState<string | undefined>(undefined);

  // One source per grid, each paged and sorted on the server (ADR-0010 §6); an edit may move a row in either.
  const sources = useMemo(() => {
    const answered = new WeakMap<Row, Answered>();
    const of = (grid: TradeGrid) => GridSource.fetch<Row>(async (query, signal) => {
      // Filtering is not offered by the server yet; a filter set anyway is refused, never ignored.
      if (query.filter !== undefined) throw new Error('This grid cannot filter yet. Clear the filter to see the trades.');
      const page = await api.rows(workspaceId, { start: query.range.start, count: query.range.count, sorts: query.sorts, grid }, signal);
      // Each row keeps the answer it came in: a position means a row only in that answer's order (ADR-0016 note 17).
      const answer: Answered = { sorts: query.sorts, orderToken: page.orderToken };
      for (const row of page.rows) answered.set(row, answer);
      return { rows: page.rows, start: page.start, totalCount: page.totalCount, orderToken: page.orderToken };
    }, { rowKey: (row) => row.tradeId, onFetchFailed: (event) => setFailure(describe(event.error)) });
    const grids = { new: of('new'), existing: of('existing') };
    return {
      ...grids,
      answered: (row: Row) => answered.get(row),
      changed: () => { grids.new.notifyChanged([]); grids.existing.notifyChanged([]); },
      requery: () => { grids.new.requery(); grids.existing.requery(); },
      dispose: () => { grids.new.dispose(); grids.existing.dispose(); },
    };
  }, [api, workspaceId]);
  useEffect(() => () => sources.dispose(), [sources]);

  // The revision the page last showed of its own doing, so that one someone else made (the AI, another page) is told
  // from it: the grids' rows are read again for that one, as for the page's own edits.
  const ownRevision = useRef<number | undefined>(undefined);
  const show = useCallback((next: XvaWorkspaceView) => {
    ownRevision.current = next.revision;
    queryClient.setQueryData(['xva', workspaceId], next);
  }, [queryClient, workspaceId]);

  const queue = useMemo(() => new EditQueue(0,
    (revision, edit: PendingEdit) => edit.measures !== undefined
      ? api.measures(workspaceId, revision, edit.measures)
      : edit.resolution === undefined
        ? api.edit(workspaceId, revision, edit.entity, edit.field, edit.value)
        : api.resolve(workspaceId, revision, edit.entity, edit.field, edit.resolution, edit.case),
    (next, edit) => {
      show(next);
      sources.changed();
      // B or A for a whole grid says what it did (ADR-0006's note 2 of 2026-11-11); a cell's own edit shows in its cell.
      if (edit.resolution === undefined && edit.measures === undefined && edit.says !== undefined) workspace.notify('done', edit.says(next));
    },
    (edit, error) => {
      workspace.notify('refused', refusedEdit(edit, describe(error)));
      void api.view(workspaceId).then((fresh) => { queue.rebase(fresh.revision); show(fresh); sources.changed(); });
    }), [api, workspaceId, show, sources, workspace]);
  // The queue follows the revision on screen: the one first read, and any read since that someone else moved
  // (the AI, another page), so the next edit is not refused for a change the user can already see.
  const shownRevision = view.data?.revision;
  useEffect(() => { if (shownRevision !== undefined) queue.shown(shownRevision); }, [shownRevision, queue]);
  useEffect(() => {
    if (shownRevision === undefined) return;
    if (ownRevision.current !== undefined && shownRevision !== ownRevision.current) sources.changed();
    ownRevision.current = shownRevision;
  }, [shownRevision, sources]);

  // The selected modules' trade columns (ADR-0010 §10), made again only when they change (ExGrid ADR-0003).
  const tradeColumns = view.data?.tradeColumns;
  const columnsKey = tradeColumns === undefined ? '' : tradeColumns.map((c) => c.field).join('|');
  const registered = useRef<readonly RegisteredColumn[]>([]);
  // The grids' columns as last made, for their Context Menus to read (ADR-0010 §3).
  const gridColumns = useRef<{ readonly new: readonly GridColumn<Row>[]; readonly existing: readonly GridColumn<Row>[] }>({ new: [], existing: [] });
  registered.current = tradeColumns ?? [];
  // A membership column per selected measure that declares one (ADR-0010 §10), on both grids.
  const declared = view.data === undefined ? [] : declaring(view.data.measures, view.data.selectedMeasures);
  const declaredKey = declared.map((m) => m.id).join('|');
  const declaredNow = useRef<readonly RegisteredMeasure[]>([]);
  declaredNow.current = declared;
  const membership = useMemo(() => declaredNow.current.map((m) => membershipColumn<Row>(m)), [declaredKey]);
  // The trade fields the selected measures need, on both grids (ADR-0010 §10): a hidden one keeps its value.
  const fieldsKey = (view.data?.inputs ?? []).filter((i) => i.tab === TRADES_TAB).map((i) => i.field).join('|');
  const assignment = useMemo(() => assignmentOf(fieldsKey === '' ? [] : fieldsKey.split('|')), [fieldsKey]);
  const newColumns = useMemo<readonly GridColumn<Row>[]>(() => [...leading, ...assignment, ...moduleColumns(registered.current), ...membership, ...derived], [columnsKey, assignment, membership]);
  const existingColumns = useMemo<readonly GridColumn<Row>[]>(() => [...existingTerms, ...assignment, ...membership, ...derived], [assignment, membership]);
  gridColumns.current = { new: newColumns, existing: existingColumns };
  // Each grid's columns at the widths the user dragged them to, kept per panel while it is open.
  const newWidths = useColumnWidths(panelId, 'trades/new', newColumns);
  const existingWidths = useColumnWidths(panelId, 'trades/existing', existingColumns);

  // The layout (ADR-0010 §1): the input tabs open, or collapsed to their row, at a height the user drags; the dock the
  // same at the bottom; and below `sm` the part in view.
  const [section, setSection] = useState<Section>('trades');
  const [inputTab, setInputTab] = useState<InputTab>('netting');
  const [inputsOpen, setInputsOpen] = useState(true);
  // The input tabs are as high as the open tab's content until the handle is dragged; from then on, as high as it set.
  const [inputsDragged, setInputsDragged] = useState<number | undefined>(undefined);
  const [dockOpen, setDockOpen] = useState(true);
  const [dockHeight, setDockHeight] = useState(DOCK_HEIGHT);
  // The dock's tab, Metrics or a measure's own (ADR-0010 §7), the case its trace is of, and whether the dock is widened
  // over the input tabs and the trade grids, as opening a measure's tab does (owner, 2026-11-08).
  const [resultTab, setResultTab] = useState<string>(METRICS_TAB.id);
  const [traceCase, setTraceCase] = useState(BASE_CASE);
  // The market the dock shows (ADR-0010 §4): the base market, or a scenario the Run priced, picked in that Run.
  const [scenarioPick, setScenarioPick] = useState<{ readonly runId: string | undefined; readonly scenario: string }>({ runId: undefined, scenario: BASE_SCENARIO });
  const [dockFull, setDockFull] = useState(false);
  const [focusRequest, setFocusRequest] = useState(0);
  /** `open netting`, `open csa`, `open im`: the tab open and in view, with the keyboard in it (ADR-0010 §9), so a dock
   * widened over the tabs is put back. A measure's own tab opens while the measure is selected, and says so otherwise. */
  const openTab = (tab: InputTab) => {
    if (view.data !== undefined && !tabShown(view.data, tab)) {
      workspace.notify('refused', tab === SETTINGS_TAB
        ? 'The Settings tab shows only while a selected measure has a setting, and none does now.'
        : `The ${TAB_TITLES[tab]} tab shows only while a selected measure needs it, and none does now.`);
      return;
    }
    setInputTab(tab); setInputsOpen(true); setDockFull(false); setSection('inputs'); setFocusRequest((n) => n + 1);
  };

  // A result that arrives changes the New grid's rows with no edit: they are read again when the count waiting moves.
  const pendingCells = view.data?.pendingCells;
  const seenPending = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (pendingCells === undefined) return;
    if (seenPending.current !== undefined && seenPending.current !== pendingCells) sources.new.notifyChanged([]);
    seenPending.current = pendingCells;
  }, [pendingCells, sources]);

  const onEdit = useCallback((intent: GridEditIntent<Row>) => {
    const module = registered.current.find((c) => c.field === intent.column);
    if (module !== undefined) {
      // As the server would refuse it: PV Adj is read-only while Margin is entered, and says why (ADR-0010 §6).
      const refusal = cellRefusal(intent.row, module);
      if (refusal !== undefined) { intent.refuse(refusal); return; }
      if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
      const typed = parseCellNumber(intent.value);
      if (!typed.ok) { intent.refuse(typed.message); return; }
      void queue.push({ entity: `trade:${intent.row.tradeId}`, field: intent.column, value: typed.value, title: `${intent.row.tradeId} ${module.title}` });
      return;
    }
    const parse = tradeFields[intent.column];
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    const header = TRADE_HEADERS.get(intent.column);
    if (header === undefined) throw new Error(`The trade grids have an editable column ${intent.column} with no header.`);
    void queue.push({ entity: `trade:${intent.row.tradeId}`, field: intent.column, value: parsed.value, title: `${intent.row.tradeId} ${header}` });
  }, [queue]);

  // `focus grid` gives the keyboard to the New grid, or to a trace tab's tree while the dock is widened over the grids.
  const treeFocus = useRef<(() => void) | undefined>(undefined);
  const traceShown = useRef(false);
  const registerTreeFocus = useCallback((focus: (() => void) | undefined) => { treeFocus.current = focus; }, []);
  useEffect(() => {
    registerGridFocus(panelId, () => {
      const tree = treeFocus.current;
      if (traceShown.current && tree !== undefined) tree();
      else newGrid.current?.focus();
    });
    return () => registerGridFocus(panelId, undefined);
  }, [panelId]);

  /** A command that re-reads the source: the order may move, so the grid starts over (ExGrid ADR-0011). */
  const reacquire = async (label: string, run: (revision: number) => Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: { readonly carried: number; readonly conflicted: number; readonly lost: number } }>) => {
    setBusy(label);
    try {
      // After the edits already sent, so that what they changed carries (spine 6).
      const { workspace: next, carried } = await queue.after(async (revision) => {
        const answer = await run(revision);
        queue.rebase(answer.workspace.revision);
        return answer;
      });
      show(next);
      sources.requery();
      // Edits in conflict or lost wait for the user's decision: the notice that counts them does not leave by itself.
      workspace.notify('done', `${label}: ${carried.carried} edit(s) carried, ${carried.conflicted} in conflict, ${carried.lost} lost.`,
        { stays: carried.conflicted + carried.lost > 0 });
    } catch (error) {
      workspace.notify('refused', `${label} failed: ${describe(error)}`);
      const fresh = await api.view(workspaceId);
      queue.rebase(fresh.revision);
      show(fresh);
    } finally {
      setBusy(undefined);
    }
  };

  const chooseCounterparty = async (registration: string | null) => {
    if (registration === null || view.data === undefined || registration === view.data.registration.key) return;
    const drop = await api.previewCounterpartyChange(workspaceId);
    if (drop.count === 0) await reacquire('Counterparty changed', (revision) => api.changeCounterparty(workspaceId, revision, registration, 0));
    else setConfirm({ registration, drop });
  };

  // The figures of the latest Price stay on the page (XvaResult); the Run itself opens in a panel only when asked.
  const [priced, setPriced] = useState<string | undefined>(undefined);
  const price = async () => {
    setBusy('Pricing');
    try {
      // Price prices the edits already sent, once they have landed (spine 6).
      const key = crypto.randomUUID();
      const run = await queue.after((revision) => api.price(workspaceId, revision, key));
      setPriced(run.runId);
      workspace.notify('done', `Price was accepted as Run ${run.runId}.`);
      void queryClient.invalidateQueries({ queryKey: ['xva', workspaceId] });
      void queryClient.invalidateQueries({ queryKey: ['history'] });
    } catch (error) {
      const targets = error instanceof ApiError && error.envelope.issues.length > 0 ? ` (${error.envelope.issues.map((i) => i.path).join(', ')})` : '';
      workspace.notify('refused', `Price was refused: ${describe(error)}${targets}`);
    } finally {
      setBusy(undefined);
    }
  };

  const openRun = (runId: string) => {
    const resultView = workspace.catalog.views.find((v) => v.definitionId === XVA_RESULT_VIEW);
    workspace.store.getState().openPanel({ kind: 'run', title: `CVA/FVA ${view.data?.registration.key ?? ''}`, runId, viewKey: resultView === undefined ? undefined : keyOf(resultView) });
  };

  /** Asks again for the failed results of one trade, or of all (`pv01 retry`, ADR-0010 §6). */
  const retry = async (tradeId?: string) => {
    try {
      const { workspace: next, asked } = await api.retry(workspaceId, tradeId);
      show(next);
      sources.new.notifyChanged([]);
      workspace.notify('done', asked === 0 ? 'No result had failed, so nothing was asked again.' : `Asked again for ${asked} result(s).`);
    } catch (error) {
      workspace.notify('refused', `Nothing was asked again: ${describe(error)}`);
    }
  };
  // A trade grid's Context Menu: Ask again on a failed result, and Vary values… on a cell the user may type now
  // (ADR-0010 §3), which a module's read-only cell, such as PV Adj while Margin is entered, is not.
  const tradeCommands = useCallback((grid: TradeGrid) => (context: GridContextMenuContext<Row>) => {
    const { row, column: name } = context;
    const column = gridColumns.current[grid].find((c) => c.name === name);
    const module = registered.current.find((c) => c.field === name);
    const typed = column?.editable === true && (module === undefined || cellRefusal(row, module) === undefined);
    return [
      ...(failed(row, name) ? [{ id: RETRY, enabled: true, invoke: () => void pageRef.current.retry(row.tradeId) }] : []),
      ...(typed && column !== undefined
        ? [{ id: VARY_COMMAND, enabled: true, invoke: () => setVarying({ entity: `trade:${row.tradeId}`, field: name, title: `${row.tradeId} ${column.header}` }) }] : []),
    ];
  }, []);
  const newCommands = useMemo(() => tradeCommands('new'), [tradeCommands]);
  const existingCommands = useMemo(() => tradeCommands('existing'), [tradeCommands]);

  /** The Run the dock shows: the one priced here last, else the workspace's latest. */
  const dockRun = () => priced ?? view.data?.runs[0]?.runId;
  // A scenario's id is its position in the Run that priced it (s1, s2), so a pick holds in that Run alone: the dock
  // showing another Run shows the base market until one of its scenarios is picked (spine 6).
  const scenarioInView = scenarioPick.runId !== undefined && scenarioPick.runId === dockRun() ? scenarioPick.scenario : BASE_SCENARIO;
  const setScenarioInView = (scenario: string) => setScenarioPick({ runId: dockRun(), scenario });
  /** Copy quote of the market in view (ADR-0010 §4): a scenario's quote names it, so a stressed figure is never the quote. */
  const copyQuote = async (scenario: string = scenarioInView) => {
    const runId = dockRun();
    if (runId === undefined) { workspace.notify('refused', 'Nothing is priced yet, so there is no quote to copy.'); return; }
    try {
      const quote = await api.quote(runId, scenario === BASE_SCENARIO ? undefined : scenario);
      await copyRich(quote.text, quote.html);
      workspace.notify('done', quote.scenario === undefined || quote.scenario === null
        ? `The quote of ${runId} is on the clipboard.` : `The quote of ${runId} under scenario ${quote.scenario} is on the clipboard.`);
    } catch (error) {
      workspace.notify('refused', `The quote was not copied: ${describe(error)}`);
    }
  };
  const download = () => {
    const runId = dockRun();
    if (runId === undefined) { workspace.notify('refused', 'Nothing is priced yet, so there is no Run to download.'); return; }
    downloadRun(runId);
  };

  /** The tabs of the dock's Run: Metrics, then those its priced measures register while selected (ADR-0010 §7). */
  const resultTabs = (): readonly DockTab[] => {
    const runId = dockRun();
    const run = runId === undefined ? undefined : queryClient.getQueryData<RunRecord>(['run', runId]);
    return dockTabs(view.data?.measures ?? [], run === undefined ? undefined : pricedMeasures(run.input), view.data?.selectedMeasures ?? []);
  };
  /** Shows a dock tab (`open result simm`); a measure's own widens the dock over the grids. */
  const openResult = (id: string) => {
    const tabs = resultTabs();
    const tab = tabs.find((t) => t.id === id.toLowerCase());
    if (tab === undefined) { workspace.notify('refused', `The dock's Run has no ${id} tab. Its tabs: ${tabs.map((t) => t.id).join(', ')}.`); return; }
    setResultTab(tab.id); setDockOpen(true); setSection('results');
    if (tab.view !== 'metrics') setDockFull(true);
  };
  const tabIdOf = (tab: string) => `${panelId}#${tab}`;
  /** The trace the dock shows in a measure's own tab, for the trace commands; none while it shows Metrics or is closed. */
  const traceInView = (): string | undefined => {
    const tab = resultTabs().find((t) => t.id === resultTab);
    return dockOpen && tab !== undefined && tab.view === 'trace' ? tabIdOf(tab.id) : undefined;
  };

  /**
   * Opens a measure's trace for a case of the dock's Run, at a term or its root (ADR-0011 §6, §7): in the measure's own
   * tab where it registers one and the Run priced it (ADR-0012 §7), widening the dock; otherwise in a trace panel. The
   * case and the market are those the tab shows unless a Metrics cell names others, so the line a trace offers comes back
   * to them.
   */
  const explain = (measure: string, path?: string, caseId: string = traceCase, scenario: string = scenarioInView) => {
    const runId = dockRun();
    if (runId === undefined) { workspace.notify('refused', 'Nothing is priced yet, so there is no trace to explain.'); return; }
    const registered = measureNamed(measure, view.data?.measures ?? []);
    const traced = (view.data?.measures ?? []).filter((m) => m.traced).map((m) => m.id);
    if (registered === undefined) { workspace.notify('refused', `${measure} is not a measure of this page. The measures that keep a trace: ${traced.join(', ') || 'none'}.`); return; }
    if (!registered.traced) { workspace.notify('refused', `${registered.title} keeps no trace to explain. The measures that do: ${traced.join(', ') || 'none'}.`); return; }
    const own = resultTabs().find((t): t is MeasureTab => t.view === 'trace' && t.measure === registered.id);
    if (own !== undefined) {
      setResultTab(own.id); setTraceCase(caseId); setScenarioInView(scenario); setDockOpen(true); setDockFull(true); setSection('results');
      requestTerm(tabIdOf(own.id), path);
      return;
    }
    const under = scenario === BASE_SCENARIO ? '' : ` · ${scenario}`;
    openTrace(workspace.store.getState(), { runId, case: caseId, scenario, measure: registered.id }, `${registered.title} · ${view.data?.registration.key ?? ''}${under}`.trim(), path);
  };

  const setMeasures = (measures: readonly string[]) => void queue.push({ entity: MEASURES_ENTITY, field: 'measures', measures });
  /** B or A for every row of a grid, as one edit (ADR-0010 §6); a row's own edit made later wins over it. */
  const setSide = (side: 'before' | 'after', value: boolean, grid: TradeGrid = lastGrid.current) =>
    void queue.push({
      entity: `grid:${grid}`, field: side, value, title: sideTitle(side, grid),
      says: (next) => sideSaid(side, value, grid, grid === 'new' ? next.newTrades : next.trades - next.newTrades),
    });

  const refresh = () => reacquire('Refreshed', (revision) => api.refresh(workspaceId, revision));

  // The Changes tab's list (ADR-0010 §3's note of 2026-11-08), read while the tab is open, at the revision on screen.
  const changesOpen = inputsOpen && view.data !== undefined && shownTab(view.data, inputTab) === CHANGES_TAB;
  const changeList = useQuery({
    queryKey: ['xva', workspaceId, 'changes', view.data?.revision],
    queryFn: () => api.changes(workspaceId),
    enabled: changesOpen,
  });
  /** A command of the Changes tab, after the edits already sent (spine 6); a Common change moves the grids' rows.
   * Refused, the page says why after `label`, which says what was not done, and the answer is the server's reason;
   * taken, it is `undefined`. */
  const attempt = async (label: string, run: (revision: number) => Promise<XvaWorkspaceView>): Promise<string | undefined> => {
    try {
      const next = await queue.after(async (revision) => {
        const answer = await run(revision);
        queue.rebase(answer.revision);
        return answer;
      });
      show(next);
      sources.changed();
      return undefined;
    } catch (error) {
      const reason = describe(error);
      workspace.notify('refused', `${label}: ${reason}`);
      return reason;
    }
  };
  const apply = async (label: string, run: (revision: number) => Promise<XvaWorkspaceView>) => (await attempt(label, run)) === undefined;
  /** Removes a case by its title or id; the tab names it by its id, which no other case has. */
  const removeCase = async (name: string, title: string = name) => {
    if (await apply(`${title} was not removed`, (revision) => api.removeCase(workspaceId, revision, name))) workspace.notify('done', `Removed the case ${title}.`);
  };
  /**
   * The page's door for the commands the server reads (`set`, `reset`, `remove`, `undo`, `redo`; ADR-0016 §5): they
   * take their turn in the queue, after the edits already sent (spine 6), and the workspace is read again in that same
   * turn, since any of its rows may have moved, so this view never arrives after a later edit's.
   */
  const door = useMemo(() => commandDoor({
    workspaceId,
    catalog: workspace.catalog,
    api: workspace.api,
    turns: queue,
    view: () => screen.current?.(),
    reread: async () => {
      const fresh = await api.view(workspaceId);
      show(fresh);
      sources.changed();
      return fresh.revision;
    },
    notify: workspace.notify,
  }), [api, workspaceId, workspace, queue, show, sources]);
  /** The grids' undo and redo keys (ADR-0016 §11): the notice says what was taken back or put back, or why nothing was. */
  const takeBack = async (verb: 'undo' | 'redo') => {
    const reason = await door.invoke(`workspace.${verb}`, {});
    if (reason !== undefined) workspace.notify('refused', reason.sentence);
  };
  const clearCases = async () => {
    if (await apply('No case was removed', (revision) => api.clearCases(workspaceId, revision))) workspace.notify('done', 'Removed every case.');
  };
  /** A value as written (a spread with its unit, a setting), read and checked by the server as its cell reads it, in the
   * Base; refused, the page says why and the answer is the reason. */
  const setText: SetText = (entity, field, text, title) =>
    attempt(`${title} was not changed`, (revision) => api.setChange(workspaceId, revision, entity, field, text));
  /** `cds ABCH USD SNRFOR`, `cds proxy financials asia A`, `cds none` (ADR-0010 §8's note of 2026-11-09): the server reads the
   * words against its catalogue and says what it chose; one it cannot resolve is refused by name. */
  const chooseCds = async (text: string) => {
    const said: string[] = [];
    const chosen = await apply('cds chose nothing', async (revision) => {
      const answer = await api.cds(workspaceId, revision, text);
      said.push(answer.said);
      return answer.workspace;
    });
    if (chosen) workspace.notify('done', said.join(' '));
  };
  const changeActions: ChangeActions = {
    set: (change, text) => void apply(`${change.title} was not changed`, (revision) =>
      api.setChange(workspaceId, revision, change.entity, change.field, text, change.case ?? undefined)),
    remove: (change) => void queue.push({ entity: change.entity, field: change.field, resolution: 'reset', case: change.case ?? undefined, title: change.title }),
    removeCase: (caseId, title) => void removeCase(caseId, title),
    clearCases: () => void clearCases(),
  };

  /**
   * A case per value of a cell (ADR-0010 §3, §9), after the edits already sent (spine 6). The workspace is the Base and
   * does not move, so the grids are not read again. Refused, it throws with the server's reason.
   */
  const varyCell = async (cell: VaryCell, values: readonly string[]): Promise<Varied> => {
    const answer = await queue.after(async (revision) => {
      const varied = await api.vary(workspaceId, revision, cell, values);
      queue.rebase(varied.workspace.revision);
      return varied;
    });
    show(answer.workspace);
    return answer;
  };
  /** `vary` from the command line: the page says what was made and what was skipped, and why. */
  const vary = async (cell: VaryCell, values: readonly string[]) => {
    try {
      workspace.notify('done', variedText(await varyCell(cell, values)));
    } catch (error) {
      workspace.notify('refused', `vary made no case: ${describe(error)}`);
    }
  };
  /**
   * `add trades` (docs/03 §5): a case per New row with A ticked, each that trade alone, after the edits already sent. As
   * for vary, the workspace is the Base and does not move. A refusal, such as a candidate in Before, is said by name.
   */
  const addTrades = async () => {
    try {
      const answer = await queue.after(async (revision) => {
        const added = await api.addTrades(workspaceId, revision);
        queue.rebase(added.workspace.revision);
        return added;
      });
      show(answer.workspace);
      workspace.notify('done', addedText(answer));
    } catch (error) {
      workspace.notify('refused', `add trades made no case: ${describe(error)}`);
    }
  };
  /**
   * `scenario s1 jpy_ois +10bp; usdjpy -5%` (ADR-0010 §4): the scenario with every shift, after the edits already sent
   * (spine 6). The server reads the shifts and refuses one it cannot resolve by name; the workspace's trades do not move.
   */
  const defineScenario = async (name: string, shifts: string) => {
    try {
      workspace.notify('done', scenariosText(await scenariosMade((revision) => api.defineScenario(workspaceId, revision, name, shifts))));
    } catch (error) {
      workspace.notify('refused', `scenario made nothing: ${describe(error)}`);
    }
  };
  /**
   * A scenario command or a gesture of the Scenarios tab (ADR-0010 §4), after the edits already sent (spine 6). The
   * workspace's trades do not move, so the grids are not read again. Refused, it throws with the server's reason.
   */
  const scenarioCommand = async (run: (revision: number) => Promise<XvaWorkspaceView>): Promise<XvaWorkspaceView> => {
    const next = await queue.after(async (revision) => {
      const answer = await run(revision);
      queue.rebase(answer.revision);
      return answer;
    });
    show(next);
    return next;
  };
  /** A scenario command whose answer says what it made, after the edits already sent; refused, it throws. */
  const scenariosMade = async (run: (revision: number) => Promise<ScenariosMade>): Promise<ScenariosMade> => {
    let made: ScenariosMade | undefined;
    await scenarioCommand(async (revision) => { made = await run(revision); return made.workspace; });
    if (made === undefined) throw new Error('The scenario command answered nothing.');
    return made;
  };
  const editShifts = async (rows: readonly ShiftEdit[]) => { await scenarioCommand((revision) => api.editShifts(workspaceId, revision, rows)); };
  const includeScenario = async (name: string, included: boolean) => {
    try {
      await scenarioCommand((revision) => api.includeScenario(workspaceId, revision, name, included));
      workspace.notify('done', includedText(name, included));
    } catch (error) {
      workspace.notify('refused', `${name} was not ${included ? 'included' : 'excluded'}: ${describe(error)}`);
    }
  };
  const removeScenario = async (name: string) => {
    try {
      await scenarioCommand((revision) => api.removeScenario(workspaceId, revision, name));
      workspace.notify('done', `Removed the scenario ${name}.`);
    } catch (error) {
      workspace.notify('refused', `${name} was not removed: ${describe(error)}`);
    }
  };
  /** Duplicate: the dialog stays open with the server's reason when it is refused, so this throws. */
  const duplicateScenario = async (name: string, copy: string) => {
    const made = await scenariosMade((revision) => api.duplicateScenario(workspaceId, revision, name, copy));
    workspace.notify('done', `Duplicated ${name} as ${made.made.join(', ')}.`);
  };
  const duplicate = async (name: string, copy: string) => {
    try { await duplicateScenario(name, copy); } catch (error) { workspace.notify('refused', `${name} was not duplicated: ${describe(error)}`); }
  };
  /** `scenario ladder …`, `scenario cross …`: the page says how many it made and which of the same name it replaced. */
  const generateScenarios = async (generator: 'ladder' | 'cross', text: string) => {
    try {
      workspace.notify('done', generatedText(await scenariosMade((revision) => api.generateScenarios(workspaceId, revision, generator, text))));
    } catch (error) {
      workspace.notify('refused', `scenario ${generator} made nothing: ${describe(error)}`);
    }
  };
  const clearScenarios = async () => {
    try {
      await scenarioCommand((revision) => api.clearScenarios(workspaceId, revision));
      workspace.notify('done', 'Removed every scenario; Price prices the base market alone.');
    } catch (error) {
      workspace.notify('refused', `No scenario was removed: ${describe(error)}`);
    }
  };
  // The saved sets the user can see (ADR-0010 §4, C32), for `scenario load` to offer; read again after a save.
  const scenarioSets = useQuery({ queryKey: ['xva', 'scenario-sets'], queryFn: () => api.scenarioSets() });
  /** `scenario save <set> [desk|all]`: what the workspace holds once the edits already sent are in (spine 6). */
  const saveScenarios = async (set: string, share?: 'desk' | 'all') => {
    try {
      const saved: SetSaved = await queue.after((revision) => api.saveScenarios(workspaceId, revision, set, share));
      workspace.notify('done', savedText(saved));
      await queryClient.invalidateQueries({ queryKey: ['xva', 'scenario-sets'] });
    } catch (error) {
      workspace.notify('refused', `scenario save saved nothing: ${describe(error)}`);
    }
  };
  /** `scenario load <set>`: the page says which scenarios it added and which of the same name it replaced. */
  const loadScenarios = async (set: string) => {
    try {
      workspace.notify('done', loadedText(set, await scenariosMade((revision) => api.loadScenarios(workspaceId, revision, set))));
    } catch (error) {
      workspace.notify('refused', `scenario load loaded nothing: ${describe(error)}`);
    }
  };
  const scenarioActions: ScenarioActions = {
    edit: editShifts,
    include: (name, included) => void includeScenario(name, included),
    duplicate: duplicateScenario,
    remove: (name) => void removeScenario(name),
    clear: () => void clearScenarios(),
    notify: workspace.notify,
  };
  // *Vary values…* (ADR-0010 §3): the cell whose Context Menu opened the dialog, while it is open.
  const [varying, setVarying] = useState<VaryTarget | undefined>(undefined);
  const setCurrency = (currency: string) => void queue.push({ entity: SETTINGS_ENTITY, field: 'displayCurrency', value: currency, title: 'The display currency' });

  // The commands run what the page's own controls run, as the page stands when they run (ADR-0010 §9).
  const pageCommands = { takeBack, chooseCds, cdsCatalogue: () => catalogue.data, price, setMeasures, setSide, retry, copyQuote, download, refresh, setCurrency, openTab, explain, openResult, setDockFull, resultTabs, traceInView, vary, addTrades, removeCase, clearCases, defineScenario, includeScenario, removeScenario, duplicate, clearScenarios, generateScenarios, saveScenarios, loadScenarios, scenarioSets: () => scenarioSets.data ?? [], scenarios: () => view.data?.scenarios ?? [], pricing: () => (view.data === undefined ? undefined : { cases: view.data.cases.length, cap: view.data.caseCap }), measures: () => view.data?.measures ?? [], currencies: () => view.data?.currencies ?? [], examples: () => ({ csa: view.data?.csas[0]?.csaId }) };
  const pageRef = useRef(pageCommands);
  pageRef.current = pageCommands;
  useEffect(() => {
    registerXvaPage(panelId, {
      price: () => void pageRef.current.price(),
      chooseCds: (text) => void pageRef.current.chooseCds(text),
      cdsCatalogue: () => pageRef.current.cdsCatalogue(),
      setMeasures: (measures) => pageRef.current.setMeasures(measures),
      setSide: (side, value, grid) => pageRef.current.setSide(side, value, grid),
      vary: (cell, values) => void pageRef.current.vary({ cell }, values),
      addTrades: () => void pageRef.current.addTrades(),
      removeCase: (name) => void pageRef.current.removeCase(name),
      clearCases: () => void pageRef.current.clearCases(),
      defineScenario: (name, shifts) => void pageRef.current.defineScenario(name, shifts),
      includeScenario: (name, included) => void pageRef.current.includeScenario(name, included),
      removeScenario: (name) => void pageRef.current.removeScenario(name),
      duplicateScenario: (name, copy) => void pageRef.current.duplicate(name, copy),
      clearScenarios: () => void pageRef.current.clearScenarios(),
      scenarios: () => pageRef.current.scenarios(),
      generateScenarios: (generator, text) => void pageRef.current.generateScenarios(generator, text),
      saveScenarios: (set, share) => void pageRef.current.saveScenarios(set, share),
      loadScenarios: (set) => void pageRef.current.loadScenarios(set),
      scenarioSets: () => pageRef.current.scenarioSets(),
      pricing: () => pageRef.current.pricing(),
      retry: () => void pageRef.current.retry(),
      downloadRun: () => pageRef.current.download(),
      copyQuote: () => void pageRef.current.copyQuote(),
      refresh: () => void pageRef.current.refresh(),
      setCurrency: (currency) => pageRef.current.setCurrency(currency),
      openTab: (tab) => pageRef.current.openTab(tab),
      explain: (measure, path) => pageRef.current.explain(measure, path),
      openResult: (tab) => pageRef.current.openResult(tab),
      setDockFull: (full) => pageRef.current.setDockFull(full),
      resultTabs: () => pageRef.current.resultTabs(),
      traceInView: () => pageRef.current.traceInView(),
      examples: () => pageRef.current.examples(),
      measures: () => pageRef.current.measures(),
      currencies: () => pageRef.current.currencies(),
    });
    registerCommandDoor(panelId, door);
    return () => { registerXvaPage(panelId, undefined); registerCommandDoor(panelId, undefined); };
  }, [panelId, door]);
  // A trade grid as the line is told of it: the trades of its subset, its Selection as positions in the order its rows
  // were answered in, and its Focus by its column's name, which is the field's where the column is one.
  const tradeScreen = useMemo(() => {
    const of = (grid: TradeGrid, handle: { readonly current: ExGridApi<Row> | null }) => () => (handle.current === null ? undefined
      : pagedView({ table: TRADES_TABLE, subset: grid, columns: gridColumns.current[grid].map((c) => c.name) }, handle.current.readView(), sources[grid].snapshot(), sources.answered));
    return { new: of('new', newGrid), existing: of('existing', existingGrid) };
  }, [sources]);
  // The grids' undo and redo keys run the same two commands; the functions are the same for the page's life, so no
  // grid is made again for them.
  const gridUndo = useMemo(() => ({ undo: () => void pageRef.current.takeBack('undo'), redo: () => void pageRef.current.takeBack('redo') }), []);

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const blocking = ws.conflicts.length + ws.lost.filter((l) => !l.acknowledged).length;

  const pricedRun = priced ?? ws.runs[0]?.runId;
  const pricedRevision = ws.runs.find((r) => r.runId === pricedRun)?.revision;
  // Widened, the dock covers the input tabs and the grids while it is open; collapsed, it is its line again.
  const full = dockFull && dockOpen && pricedRun !== undefined;
  traceShown.current = full && resultTab !== METRICS_TAB.id;
  const notBusy = busy === undefined;

  const enforceable = ws.agreement.enforceable ? 'enforceable' : 'not enforceable';
  const inputsHeight = inputsDragged ?? inputsFit(shownTab(ws, inputTab), ws, pixels(theme.spacing.xs), catalogue.isError);
  const newHeight = compactGridHeight(Math.max(1, Math.min(ws.newTrades, 8)));
  // What the parts other than Existing take, from the heights the page set (never measured): Existing has the rest.
  const taken = TAKEN + (inputsOpen ? inputsHeight + RESIZER : 0) + newHeight
    + (pricedRun === undefined ? 24 : dockOpen ? dockHeight + RESIZER + 32 : 48);
  const page = (
    <Stack gap="sm" className="qube-xva" data-section={section} data-dock={full ? 'full' : 'part'} style={{ ['--qube-xva-taken' as string]: `${taken}px` }}
      // A grid entered is the screen only where it says so below, after this: a grid of no table leaves the line none.
      onFocusCapture={(event) => { if (event.target instanceof Element && event.target.closest('.ex-grid') !== null) screen.current = undefined; }}>
      {/* The context bar (ADR-0010 §1; docs/design-guidelines.md §3, §4): one 30px Control Height, each label beside its input. */}
      <div className="qube-toolbar" role="toolbar" aria-label="Workspace">
        <ValueDate value={ws.context.valueDate} disabled={!notBusy}
          onCommit={(date) => void reacquire('Value date moved', (revision) => api.changeValueDate(workspaceId, revision, date))} />
        <ToolbarField label="Counterparty" wide>
          {(id) => (
            <Select id={id} size="xs" className="qube-toolbar-input qube-toolbar-input--wide" allowDeselect={false} searchable value={ws.registration.key}
              data={(registrations.data ?? [ws.registration]).map((r) => ({ value: r.key, label: `${r.code} | ${r.legalEntity} | ${r.name}` }))}
              onChange={(value) => void chooseCounterparty(value)} disabled={!notBusy} comboboxProps={{ withinPortal: true }} />
          )}
        </ToolbarField>
        <div className="qube-toolbar-field" data-testid="netting-set">
          <Text size="xs" c="dimmed" className="qube-toolbar-label">Netting set</Text>
          <Badge variant="light" color={ws.agreement.enforceable ? 'gray' : 'yellow'} size="lg" radius="sm"
            title={`${ws.agreement.maId}, the main netting set, is ${enforceable} (the source's).`}>{ws.agreement.nettingSetId} · {enforceable}</Badge>
        </div>
        <ToolbarField label="Currency">
          {(id) => (
            <Select id={id} size="xs" className="qube-toolbar-input qube-toolbar-input--currency" allowDeselect={false} value={ws.displayCurrency}
              data={ws.currencies} onChange={(value) => { if (value !== null) setCurrency(value); }} disabled={!notBusy} comboboxProps={{ withinPortal: true }} />
          )}
        </ToolbarField>
        <Button size="xs" variant="default" onClick={() => void refresh()} loading={busy === 'Refreshed'} disabled={!notBusy && busy !== 'Refreshed'}
          title="Acquire the trades again for this value date; your edits carry or are held.">Refresh</Button>
        <div className="qube-toolbar-field qube-toolbar-field--measures" role="group" aria-label="Measures">
          <Text size="xs" c="dimmed" className="qube-toolbar-label" aria-hidden>Measures</Text>
          {ws.measures.map((m) => {
            const on = ws.selectedMeasures.includes(m.id);
            return (
              <Button key={m.id} size="xs" variant={on ? 'light' : 'default'} aria-pressed={on} disabled={!notBusy}
                title={on ? `Price ${m.title}. Press to leave it out.` : `Leave ${m.title} out. Press to price it.`}
                onClick={() => setMeasures(on ? ws.selectedMeasures.filter((id) => id !== m.id) : [...ws.selectedMeasures, m.id])}>{m.title}</Button>
            );
          })}
        </div>
        <Button size="xs" onClick={() => void price()} loading={busy === 'Pricing'} disabled={!notBusy && busy !== 'Pricing'} data-testid="price"
          {...(blocking > 0 ? { title: `${blocking} held edit(s) on priced trades must be resolved first.` } : {})}>{priceLabel(ws.measures, ws.selectedMeasures, ws.cases.length, { before: ws.inBefore > 0, after: ws.inAfter > 0 }, 1 + ws.scenarios.filter((x) => x.included).length)}</Button>
      </div>
      {/* The status line: what the figures stand on, and SIMULATED while a provider is. */}
      <Group gap="xs" wrap="wrap" className="qube-xva-status">
        <Text size="xs" data-testid="workspace-summary">{ws.trades} trades · {ws.overrides} edit(s) · revision {ws.revision} · {ws.sourceVersion}</Text>
        <Text size="xs">{ws.agreement.maId} · {ws.agreement.collateralised ? `main CSA ${ws.agreement.mainCsaId}` : ws.agreement.noCsa}</Text>
        {ws.simulated.length > 0 && (
          <Badge variant="light" color="yellow" data-testid="simulated" title={`Figures from a simulated provider: ${ws.simulated.join(', ')}. They are not a model of anything.`}>SIMULATED</Badge>
        )}
      </Group>
      {/* Below `sm` the screen is three tabs (ADR-0010 §1), with the context bar above them. */}
      <SegmentedControl hiddenFrom="sm" size="xs" fullWidth aria-label="Part of the page" value={section} onChange={(v) => setSection(v as Section)}
        data={[{ value: 'inputs', label: 'Inputs' }, { value: 'trades', label: 'Trades' }, { value: 'results', label: 'Results' }]} />
      <div data-part="inputs" className="qube-xva-part">
        <XvaInputs panelId={panelId} ws={ws} onIssue={setIssue} push={(entity, field, value, title) => void queue.push({ entity, field, value, title })} setText={setText}
          catalogue={catalogue.isError ? { refused: describe(catalogue.error) } : catalogue.data} onGridEntered={(view) => { screen.current = view; }}
          tab={inputTab} onTab={(tab) => { setInputTab(tab); setInputsOpen(true); }} open={inputsOpen} onOpen={setInputsOpen} height={inputsHeight} focusRequest={focusRequest}
          changeList={changeList.data} changeActions={changeActions} scenarioActions={scenarioActions} onVary={setVarying} commandLabel={commandLabel} />
        <VaryDialog target={varying} vary={(target, values) => varyCell({ entity: target.entity, field: target.field }, values)} onClose={() => setVarying(undefined)} />
        {inputsOpen && <Resizer label="Height of the input tabs" value={inputsHeight} min={48} max={640} grows="down" onChange={setInputsDragged} />}
      </div>
      {issue !== undefined && <Text size="xs" c="red" role="alert" data-testid="field-issue">{issue}</Text>}
      <div data-part="trades" className="qube-xva-part">
        <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(edit, resolution) => void queue.push({ entity: edit.entity, field: edit.field, resolution, case: edit.case ?? undefined, title: `${edit.entity} ${edit.field}` })} />
        {failure !== undefined && <Alert color="red" variant="light" withCloseButton onClose={() => setFailure(undefined)} title="The trades could not be read">{failure}</Alert>}
        {/* New trades above Existing (ADR-0010 §6): New as high as its rows, about eight at most; Existing takes the rest. */}
        <div className="qube-xva-grids">
          <TradeCaption title="New trades" count={ws.newTrades} grid="new" setSide={setSide} disabled={!notBusy} />
          <div aria-label="New trades" onFocusCapture={() => { lastGrid.current = 'new'; screen.current = tradeScreen.new; }}>
            <QubeGrid<Row> ref={newGrid} columns={newWidths.columns} onColumnWidthChanged={newWidths.onColumnWidthChanged} source={sources.new} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
              contextCommands={newCommands} commandLabel={commandLabel}
              density="compact" viewportWidth="stretch" viewportHeight={newHeight} pinnedColumnCount={1} locale="en-US" />
          </div>
          <TradeCaption title="Existing trades" count={ws.trades - ws.newTrades} grid="existing" setSide={setSide} disabled={!notBusy} />
          <div aria-label="Existing trades" onFocusCapture={() => { lastGrid.current = 'existing'; screen.current = tradeScreen.existing; }}>
            <QubeGrid<Row> ref={existingGrid} className="qube-xva-trades" columns={existingWidths.columns} onColumnWidthChanged={existingWidths.onColumnWidthChanged}
              source={sources.existing} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
              contextCommands={existingCommands} commandLabel={commandLabel}
              density="compact" viewportWidth="stretch" viewportHeight="stretch" pinnedColumnCount={1} locale="en-US" />
          </div>
        </div>
      </div>
      {/* The result dock (ADR-0010 §7) at the bottom, in view while the trades scroll: one line a case collapsed. */}
      <div data-part="results" className="qube-xva-part qube-xva-dock">
        {pricedRun === undefined
          ? <Text size="sm" data-testid="xva-result-none">Nothing is priced yet. Price shows its figures here.</Text>
          : (
            <>
              {dockOpen && !full && <Resizer label="Height of the result dock" value={dockHeight} min={48} max={560} grows="up" onChange={setDockHeight} />}
              <XvaResult runId={pricedRun} pricedRevision={pricedRevision} revision={ws.revision} register={ws.measures} selected={ws.selectedMeasures} onOpen={() => openRun(pricedRun)}
                onCopyQuote={(scenario) => void copyQuote(scenario)} onTrace={(measure, caseId, scenario, term) => explain(measure, term, caseId, scenario)}
                open={dockOpen} onOpenChange={setDockOpen} height={dockHeight}
                tab={resultTab} onTab={(tab) => { setResultTab(tab); if (tab !== METRICS_TAB.id) setDockFull(true); }} full={full} onFull={setDockFull}
                traceCase={traceCase} onTraceCase={setTraceCase} scenario={scenarioInView} onScenario={setScenarioInView}
                renderTab={(tab, caption, caseId, scenario) => {
                  switch (tab.view) {
                    case 'risks': return <XvaRisks runId={pricedRun} measures={tab.measures} register={ws.measures} caseId={caseId} scenario={scenario} />;
                    case 'profile': return (
                      <Suspense fallback={<Text size="sm">Reading the profiles…</Text>}>
                        <XvaProfile runId={pricedRun} measures={tab.measures} register={ws.measures} caseId={caseId} scenario={scenario} />
                      </Suspense>
                    );
                    case 'trace': return (
                      <Suspense fallback={<Text size="sm">Reading the trace…</Text>}>
                        <TraceTab key={`${pricedRun}:${caseId}:${scenario}:${tab.measure}`} tabId={tabIdOf(tab.id)} api={traceApi} registerFocus={registerTreeFocus}
                          address={{ runId: pricedRun, case: caseId, scenario, measure: tab.measure }} caption={caption} />
                      </Suspense>
                    );
                  }
                }} />
            </>
          )}
      </div>
      <Modal opened={confirm !== undefined} onClose={() => setConfirm(undefined)} title="Change counterparty" trapFocus transitionProps={{ duration: 0 }}>
        {confirm !== undefined && (
          <Stack gap="sm">
            <Text size="sm">Moving to {confirm.registration} drops {confirm.drop.count} edit(s) made for {ws.registration.key}. They stay in the log and in any Run that priced them; nothing brings them back by itself.</Text>
            <Table withTableBorder={false}><Table.Tbody>
              {Object.entries(confirm.drop.byKind).map(([kind, count]) => <Table.Tr key={kind}><Table.Td>{kind}</Table.Td><Table.Td>{count}</Table.Td></Table.Tr>)}
            </Table.Tbody></Table>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setConfirm(undefined)}>Keep {ws.registration.key}</Button>
              <Button data-autofocus color="red" onClick={() => {
                const { registration, drop } = confirm;
                setConfirm(undefined);
                void reacquire('Counterparty changed', (revision) => api.changeCounterparty(workspaceId, revision, registration, drop.count));
              }}>Drop {confirm.drop.count} and change</Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
  // A grid's Ctrl+Z and Ctrl+Y take back and put back the workspace's last command, as `undo` and `redo` do (ADR-0016 §11).
  return <GridUndoContext.Provider value={gridUndo}>{page}</GridUndoContext.Provider>;
}

/**
 * A trade grid's caption: its title and count, and B and A for every row of it (ADR-0010 §6). ExGrid gives a Consumer
 * no header action, so the all/none controls sit here, and are the commands `before all|none` and `after all|none`.
 */
function TradeCaption({ title, count, grid, setSide, disabled }: {
  readonly title: string;
  readonly count: number;
  readonly grid: TradeGrid;
  readonly setSide: (side: 'before' | 'after', value: boolean, grid: TradeGrid) => void;
  readonly disabled: boolean;
}) {
  const one = grid === 'new' ? 'new trade' : 'existing trade';
  return (
    <Group gap="xs" wrap="wrap" className="qube-grid-caption">
      <Text size="sm" fw={600}>{title}</Text>
      <Text size="xs">{count}</Text>
      <Group gap="xs" ml="auto" role="group" aria-label={`Before and After for every ${one}`}>
        {(['before', 'after'] as const).flatMap((side) => [true, false].map((value) => (
          <Button key={`${side}.${value}`} size="compact-xs" variant="default" disabled={disabled}
            aria-label={`${value ? 'Tick' : 'Clear'} ${side === 'before' ? 'Before' : 'After'} on every ${one}`}
            onClick={() => setSide(side, value, grid)}>{side === 'before' ? 'B' : 'A'} {value ? 'all' : 'none'}</Button>
        )))}
      </Group>
    </Group>
  );
}

function ValueDate({ value, disabled, onCommit }: { readonly value: string; readonly disabled: boolean; readonly onCommit: (date: string) => void }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => { if (draft !== value && /^\d{4}-\d{2}-\d{2}$/.test(draft)) onCommit(draft); };
  return (
    <ToolbarField label="Value date">
      {(id) => (
        <TextInput id={id} type="date" size="xs" className="qube-toolbar-input qube-toolbar-input--date" value={draft} disabled={disabled}
          onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
      )}
    </ToolbarField>
  );
}

function HeldEdits({ conflicts, lost, resolve }: {
  readonly conflicts: readonly HeldEdit[];
  readonly lost: readonly HeldEdit[];
  readonly resolve: (edit: HeldEdit, resolution: Resolution) => void;
}) {
  if (conflicts.length === 0 && lost.length === 0) return null;
  const value = (v: unknown) => (v === null || v === undefined ? 'empty' : String(v));
  return (
    <Alert color="yellow" variant="light" title={`${conflicts.length} conflict(s), ${lost.length} lost edit(s)`} data-testid="held-edits">
      <Stack gap="xs">
        {conflicts.map((c) => (
          <Group key={`${c.entity}.${c.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{c.entity} {c.field}</Text>
            <Text size="xs">yours {value(c.value)}, source now {Object.entries(c.source ?? {}).map(([f, v]) => `${f} ${value(v)}`).join(', ')}</Text>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'keep')}>Keep mine</Button>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'takeSource')}>Take source</Button>
          </Group>
        ))}
        {lost.map((l) => (
          <Group key={`${l.entity}.${l.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{l.entity} {l.field}</Text>
            <Text size="xs">{value(l.value)}: the trade is not in this version{l.acknowledged ? ' (seen)' : ''}</Text>
            {!l.acknowledged && <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'acknowledge')}>Seen</Button>}
            <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'reset')}>Discard</Button>
          </Group>
        ))}
      </Stack>
    </Alert>
  );
}
