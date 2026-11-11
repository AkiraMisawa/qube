import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import './xva.css';
import { Alert, Badge, Button, Group, Modal, SegmentedControl, Select, Stack, Table, Text, useMantineTheme } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridContextMenuContext, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import { GridUndoContext } from '../../platform/grid/gridUndo';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useColumnWidths } from '../../platform/grid/columnWidths';
import { amountTone, formatAmount } from '../../platform/grid/amounts';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerPlaces, type Place } from '../../shell/places';
import type { XvaApi } from './api';
import type { CaseView, EffectiveTrade, HeldEdit, Iteration, RegisteredColumn, RegisteredMeasure, Resolution, ScopeDrop, ShiftEdit, XvaWorkspaceView } from './contracts';
import { measureNamed, registerXvaPage } from './commands';
import { commandDoor, registerCommandDoor, tableMenu, type TableMenu } from '../../platform/commands/served';
import { filterGrid, menuView, pagedView, placeFocus, selectRows, sortGrid, whereOf, type Answered, type ScreenGrid } from '../../platform/grid/lineView';
import { GridFilterLine, type FilterSaid } from '../../platform/grid/GridFilterLine';
import { useWhere, type Heard } from '../../platform/grid/heldFilter';
import type { CommandRequest, Screen } from '../../platform/commands/commands';
import type { LineView } from '../../platform/contracts';
import { XvaResult } from './XvaResult';
import { BASE_CASE, BASE_SCENARIO, openTrace, requestTerm } from '../../platform/traces/open';
import type { TraceApi } from '../../platform/traces/api';
import type { RunRecord } from '../../platform/contracts';
import { dockTabs, METRICS_TAB, type DockTab, type MeasureTab } from './dock';
import { CSAS_TABLE, EditQueue, SETTINGS_ENTITY, TRADES_TABLE, XVA_APP, refusedEdit, tradeFields, type PendingEdit, type SetText } from './editing';
import { ToolbarField } from './fields';
import { CommittedDateInput } from '../../platform/IsoDateInput';
import { CHANGES_TAB, CREDIT_TAB, SCENARIOS_TAB, SETTINGS_TAB, shownTab, TAB_TITLES, tabShown, XvaInputs, type InputTab } from './XvaInputs';
import { XvaRisks } from './XvaRisks';
import type { ScenarioActions } from './XvaScenarios';
import type { ChangeActions } from './XvaChanges';
import { VARY_COMMAND, VaryDialog, type VaryTarget } from './VaryDialog';
import { CASE_COMMAND, CaseDialog } from './CaseDialog';
import { tradeLeftOut } from './iterations';
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
/** Why a button of the page is disabled while the page prices, refreshes or acquires (design guidelines §10). */
const WAIT = 'The page is busy: wait until it has finished, then press again.';
const pageLabel = (id: string) => (id === RETRY ? 'Ask again' : id === VARY_COMMAND ? 'Vary values…' : id === CASE_COMMAND ? 'Make case from the selection…' : undefined);
/** No table's actions: what a grid's menu holds of them until the page has read its tables. */
const NO_MENU: TableMenu = { commands: () => [], label: () => undefined };

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

/** What a workspace's trades were read of: the counterparty, the value date and the source's version of them. */
const sourceOf = (view: XvaWorkspaceView) => `${view.registration.key} ${view.context.valueDate} ${view.sourceVersion}`;

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
  // A Run the workspace gains, priced here, by the AI or on another page, is one more row of the Pricing DB.
  const latestRun = view.data?.runs[0]?.runId;
  const read = view.data !== undefined;
  const seenRun = useRef<{ readonly runId: string | undefined } | undefined>(undefined);
  useEffect(() => {
    if (!read) return;
    if (seenRun.current !== undefined && seenRun.current.runId !== latestRun) void queryClient.invalidateQueries({ queryKey: ['history'] });
    seenRun.current = { runId: latestRun };
  }, [read, latestRun, queryClient]);
  const registrations = useQuery({ queryKey: ['xva', 'registrations'], queryFn: () => api.registrations(), staleTime: Infinity });
  // The synthetic CDS catalogue the Credit tab searches and `cds` completes from (ADR-0010 §8's note of 2026-11-09).
  const catalogue = useQuery({ queryKey: ['xva', 'cds'], queryFn: () => api.cdsCatalogue(), staleTime: Infinity });
  const newGrid = useRef<ExGridApi<Row>>(null);
  const existingGrid = useRef<ExGridApi<Row>>(null);
  // What the command line is told is on screen (ADR-0016 §9, note 16 of 2026-11-11): the grid of a table the keyboard
  // was last in, read when the line opens. None until one is entered, and none once a grid that is no table is.
  const screen = useRef<(() => Screen | undefined) | undefined>(undefined);
  // The actions of the tables the grids are bound to (ADR-0016 §4, note 25 of 2026-11-11), as their Context Menus offer
  // them: read by the functions the grids were given once, so no grid is made again when they arrive.
  const menus = useRef<{ readonly trades: TableMenu; readonly csas: TableMenu }>({ trades: NO_MENU, csas: NO_MENU });
  const [busy, setBusy] = useState<string | undefined>(undefined);
  const [confirm, setConfirm] = useState<{ readonly registration: string; readonly drop: ScopeDrop } | undefined>(undefined);
  // Why each trade grid's rows could not be read, until its reader answers again: a grid's own, so that the other's
  // answer does not take it back.
  const [failure, setFailure] = useState<Readonly<Record<TradeGrid, string | undefined>>>(NOT_FAILED);
  const [issue, setIssue] = useState<string | undefined>(undefined);

  // What each trade grid's reader last answered under the conditions the grid holds from the line (ADR-0016 note 41):
  // how many rows they leave, of how many. None for a grid with no conditions, and none once a read under them failed.
  const [counted, setCounted] = useState<Readonly<Record<TradeGrid, Counted | undefined>>>({ new: undefined, existing: undefined });

  // One source per grid, each paged, sorted and narrowed on the server (ADR-0010 §6); an edit may move a row in either.
  const sources = useMemo(() => {
    const answered = new WeakMap<Row, Answered>();
    const count = (grid: TradeGrid, next: Counted | undefined) => setCounted((before) => {
      const held = before[grid];
      const same = held === undefined || next === undefined ? held === next : held.where === next.where && held.left === next.left && held.of === next.of;
      return same ? before : { ...before, [grid]: next };
    });
    const failed = (grid: TradeGrid, why: string | undefined) => setFailure((before) => (before[grid] === why ? before : { ...before, [grid]: why }));
    const of = (grid: TradeGrid) => GridSource.fetch<Row>(async (query, signal) => {
      // The server reads the line's conditions (the grid's Opaque Filter), not a column's own filter: one set is
      // refused by name, never ignored.
      if (query.filter !== undefined && Object.keys(query.filter.columns).length > 0) {
        throw new Error('A column of this grid cannot be filtered by itself yet: clear its filter, and filter from the command line (filter where …).');
      }
      const where = whereOf(query.filter);
      const page = await api.rows(workspaceId, { start: query.range.start, count: query.range.count, sorts: query.sorts, grid, ...(where === undefined ? {} : { where }) }, signal);
      if (where !== undefined && page.of === undefined) throw new Error('The server answered a page under conditions and did not say of how many rows.');
      // Each row keeps the answer it came in: a position means a row only in that answer's order (ADR-0016 note 17).
      const answer: Answered = { sorts: query.sorts, orderToken: page.orderToken, ...(where === undefined ? {} : { where }) };
      for (const row of page.rows) answered.set(row, answer);
      count(grid, where === undefined || page.of === undefined ? undefined : { where, left: page.totalCount, of: page.of });
      // The grid's trades are read: a failure said of it before is no longer how it stands.
      failed(grid, undefined);
      return { rows: page.rows, start: page.start, totalCount: page.totalCount, orderToken: page.orderToken };
    }, { rowKey: (row) => row.tradeId, onFetchFailed: (event) => { count(grid, undefined); failed(grid, describe(event.error)); } });
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
  // from it: the grids' rows are read again for that one, as for the page's own edits. And what its trades were read
  // of then: where that has moved (`refresh`, or an `undo` of one), the order may have, so the grids start over
  // (ExGrid ADR-0011).
  const ownRevision = useRef<number | undefined>(undefined);
  const ownSource = useRef<string | undefined>(undefined);
  const show = useCallback((next: XvaWorkspaceView) => {
    ownRevision.current = next.revision;
    ownSource.current = sourceOf(next);
    queryClient.setQueryData(['xva', workspaceId], next);
  }, [queryClient, workspaceId]);

  const queue = useMemo(() => new EditQueue(0,
    (revision, edit: PendingEdit) => edit.resolution === undefined
      ? api.edit(workspaceId, revision, edit.entity, edit.field, edit.value)
      : api.resolve(workspaceId, revision, edit.entity, edit.field, edit.resolution, edit.case),
    (next) => {
      show(next);
      sources.changed();
    },
    (edit, error) => {
      workspace.notify('refused', refusedEdit(edit, describe(error)));
      void api.view(workspaceId).then((fresh) => { queue.rebase(fresh.revision); show(fresh); sources.changed(); });
    }), [api, workspaceId, show, sources, workspace]);
  // The queue follows the revision on screen: the one first read, and any read since that someone else moved
  // (the AI, another page), so the next edit is not refused for a change the user can already see.
  const shownRevision = view.data?.revision;
  useEffect(() => { if (shownRevision !== undefined) queue.shown(shownRevision); }, [shownRevision, queue]);
  const shownSource = view.data === undefined ? undefined : sourceOf(view.data);
  useEffect(() => {
    if (shownRevision === undefined) return;
    if (ownRevision.current !== undefined && shownRevision !== ownRevision.current) {
      if (ownSource.current !== undefined && shownSource !== ownSource.current) sources.requery();
      else sources.changed();
    }
    ownRevision.current = shownRevision;
    ownSource.current = shownSource;
  }, [shownRevision, shownSource, sources]);

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
  /** `go csa`, `open netting`: the tab open and in view, with the keyboard in it (ADR-0010 §9), so a dock widened over
   * the tabs is put back. A measure's own tab opens while the measure is selected; otherwise nothing moves, and the
   * reason is answered. */
  const openTab = (tab: InputTab): string | undefined => {
    if (view.data !== undefined && !tabShown(view.data, tab)) {
      return tab === SETTINGS_TAB
        ? 'The Settings tab shows only while a selected measure has a setting, and none does now.'
        : `The ${TAB_TITLES[tab]} tab shows only while a selected measure needs it, and none does now.`;
    }
    setInputTab(tab); setInputsOpen(true); setDockFull(false); setSection('inputs'); setFocusRequest((n) => n + 1);
    return undefined;
  };
  // The keyboard asked into a trade grid, the dock or a trace by name (`go existing`, `find`; ADR-0016 §9, notes 20 and
  // 21): the part it is in is shown first, where the page is three parts or the dock is widened over the grids, and the
  // keyboard follows once that is drawn.
  const dock = useRef<HTMLDivElement>(null);
  const treeFocus = useRef<(() => void) | undefined>(undefined);
  const [placeRequest, setPlaceRequest] = useState<{ readonly place: TradeGrid | 'results' | 'trace'; readonly request: number } | undefined>(undefined);
  const goTo = (place: TradeGrid | 'results' | 'trace'): string | undefined => {
    if (place === 'new' || place === 'existing') { setDockFull(false); setSection('trades'); }
    else if (place === 'results' && dockRun() === undefined) return 'Nothing is priced yet: Price shows its results here.';
    else { if (place === 'results') setDockOpen(true); setSection('results'); }
    setPlaceRequest((last) => ({ place, request: (last?.request ?? 0) + 1 }));
    return undefined;
  };
  useEffect(() => {
    if (placeRequest === undefined) return;
    const { place } = placeRequest;
    if (place === 'results') {
      // The dock's keyboard starts on its tab in view, or on its first control where its Run has one tab; a dock that
      // draws neither, such as a Run that could not be read, takes it itself.
      const part = dock.current;
      if (part === null) throw new Error('The pricer page was asked to put the keyboard in its dock, and has drawn none.');
      (part.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]') ?? part.querySelector<HTMLElement>('button, select') ?? part).focus();
      return;
    }
    // A place is offered only while what takes its keyboard is drawn (`places` below): one that is gone by now is a defect.
    const grid = place === 'trace' ? undefined : (place === 'new' ? newGrid : existingGrid).current;
    const take = place === 'trace' ? treeFocus.current : grid === undefined || grid === null ? undefined : () => grid.focus();
    if (take === undefined) throw new Error(`The pricer page was asked to put the keyboard in ${place}, and has drawn nothing there to take it.`);
    take();
  }, [placeRequest]);

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

  // The page's places (ADR-0016 §9, notes 20 and 30): its two trade grids and the dock, which `go` takes, and its input
  // tabs, which `open` takes. `focus grid` goes to the first that is a grid: New trades, or a trace tab's tree while the dock is widened
  // over the grids.
  const traceShown = useRef(false);
  const registerTreeFocus = useCallback((focus: (() => void) | undefined) => { treeFocus.current = focus; }, []);
  const places = (): readonly Place[] => {
    // While the workspace is being read, or could not be, the page draws no part to go to.
    if (view.isPending || view.isError) return [];
    return [
      ...(traceShown.current && treeFocus.current !== undefined ? [{ word: 'trace', title: 'the trace', grid: true, go: () => goTo('trace') }] : []),
      { word: 'new', title: 'New trades', grid: true, go: () => goTo('new') },
      { word: 'existing', title: 'Existing trades', grid: true, go: () => goTo('existing') },
      ...(Object.keys(TAB_TITLES) as InputTab[]).map((tab) => ({ word: tab, title: `the ${TAB_TITLES[tab]} tab`, tab: true, go: () => openTab(tab) })),
      { word: 'results', title: 'the results', go: () => goTo('results') },
    ];
  };
  const placesNow = useRef(places);
  placesNow.current = places;
  useEffect(() => {
    registerPlaces(panelId, () => placesNow.current());
    return () => registerPlaces(panelId, undefined);
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

  /** A control's command through the door (ADR-0016 §5): a refusal is a notice that opens with what was not done. */
  const invoked = async (capability: string, request: Readonly<Record<string, string>>, notDone: string) => {
    const reason = await door.invoke(capability, request);
    if (reason !== undefined) workspace.notify('refused', `${notDone}: ${reason.sentence}`);
  };
  /**
   * The Price button invokes `price`, the command the line types (ADR-0016 note 33), so the two cannot differ: one Run
   * of the workspace as it stands once the edits already sent have landed (spine 6). The page reads the workspace again
   * in that turn, and the dock shows its latest Run, which is the one just accepted; the figures stay on the page
   * (XvaResult), and the Run itself opens in a panel only when asked.
   */
  const price = async () => {
    setBusy('Pricing');
    try {
      await invoked('xva.workspace.price', {}, 'Price was refused');
    } finally {
      setBusy(undefined);
    }
  };

  const openRun = (runId: string) => {
    const resultView = workspace.catalog.views.find((v) => v.definitionId === XVA_RESULT_VIEW);
    workspace.store.getState().openPanel({ kind: 'run', title: `CVA/FVA ${view.data?.registration.key ?? ''}`, runId, viewKey: resultView === undefined ? undefined : keyOf(resultView) });
  };

  /** A failed cell's Ask again (ADR-0010 §6): `pv01 retry` for its trade, as the line types it (ADR-0016 note 33). */
  const retry = (tradeId: string) => invoked('xva.pv01.retry', { trade: tradeId }, 'Nothing was asked again');
  /** A trade grid as the line is told of it: the trades' table, the grid's subset, and its columns' names as shown. */
  const declaredOf = (grid: TradeGrid): ScreenGrid => ({ table: TRADES_TABLE, subset: grid, columns: gridColumns.current[grid].map((c) => c.name) });
  // A trade grid's Context Menu: what the trades' table offers on the rows selected and the cell under the menu
  // (ADR-0016 note 25), then the page's own: Ask again on a failed result, Vary values… on a cell the user may type
  // now (ADR-0010 §3), which a module's read-only cell, such as PV Adj while Margin is entered, is not, and Make case
  // from the selection… (ADR-0016 note 37), of this grid's rows as the menu has them and the other grid's Selection.
  const tradeCommands = useCallback((grid: TradeGrid) => (context: GridContextMenuContext<Row>) => {
    const { row, column: name } = context;
    const column = gridColumns.current[grid].find((c) => c.name === name);
    const module = registered.current.find((c) => c.field === name);
    const typed = column?.editable === true && (module === undefined || cellRefusal(row, module) === undefined);
    const under = menuView((read) => pagedView(declaredOf(grid), read, sources[grid].snapshot(), sources.answered), context, row.tradeId);
    return [
      ...menus.current.trades.commands(under),
      ...(failed(row, name) ? [{ id: RETRY, enabled: true, invoke: () => void pageRef.current.retry(row.tradeId) }] : []),
      ...(typed && column !== undefined
        ? [{ id: VARY_COMMAND, enabled: true, invoke: () => setVarying({ entity: `trade:${row.tradeId}`, field: name, title: `${row.tradeId} ${column.header}` }) }] : []),
      { id: CASE_COMMAND, enabled: true, invoke: () => void pageRef.current.openCase({ grid, view: under }) },
    ];
  }, [sources]);
  const newCommands = useMemo(() => tradeCommands('new'), [tradeCommands]);
  const existingCommands = useMemo(() => tradeCommands('existing'), [tradeCommands]);

  /** The Run the dock shows: the workspace's latest, whoever priced it. */
  const dockRun = () => view.data?.runs[0]?.runId;
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

  /**
   * A measure's button invokes `measures`, the command the line types (ADR-0016 note 28), so `undo` takes it back by the
   * same name. The button says the outcome itself, by what it shows, as a cell does: so it takes the command's sentence
   * from the door, which then raises no notice of it, and has nothing more to do with it.
   */
  const setMeasures = async (measures: readonly string[]) => {
    const reason = await door.invoke('xva.measures', { measures: measures.join(' ') }, () => undefined);
    if (reason !== undefined) workspace.notify('refused', `The measures were not changed: ${reason.sentence}`);
  };
  /**
   * B or A for every row of a grid, as one edit (ADR-0010 §6); a row's own edit made later wins over it. The caption's
   * button runs the short form a user types (`before all existing`, ADR-0016 note 29): the server says the line it
   * stands for, so the button and the line cannot differ, and `undo` names both the same.
   */
  const setSide = async (side: 'before' | 'after', value: boolean, grid: TradeGrid) => {
    const line = `${side} ${value ? 'all' : 'none'} ${grid}`;
    const ended = await door.enter(line);
    if (ended !== undefined && 'sentence' in ended) workspace.notify('refused', `${line}: ${ended.sentence}`);
  };

  /**
   * The Refresh button invokes `refresh`, the command the line types (ADR-0016 note 33): the notice is the server's
   * count of what carried, and stays while an edit is held. The door reads the grids from their first row again where
   * the source moved.
   */
  const refresh = async () => {
    setBusy('Refreshed');
    try {
      await invoked('xva.refresh', {}, 'Refresh was refused');
    } finally {
      setBusy(undefined);
    }
  };

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
  /**
   * The page's door for the commands the server reads (`set`, `reset`, `remove`, `undo`, `redo`; ADR-0016 §5): they
   * take their turn in the queue, after the edits already sent (spine 6), and the workspace is read again in that same
   * turn, since any of its rows may have moved, so this view never arrives after a later edit's.
   */
  const door = useMemo(() => commandDoor({
    workspaceId,
    app: XVA_APP,
    catalog: workspace.catalog,
    api: workspace.api,
    turns: queue,
    screen: () => screen.current?.(),
    reread: async () => {
      const fresh = await api.view(workspaceId);
      // What `refresh` read, or what `undo` put back of it, may have moved the order: the grids start over then.
      // Otherwise the rows in view are read again where they are.
      const moved = ownSource.current !== undefined && ownSource.current !== sourceOf(fresh);
      show(fresh);
      if (moved) sources.requery();
      else sources.changed();
      return fresh.revision;
    },
    notify: workspace.notify,
  }), [api, workspaceId, workspace, queue, show, sources]);
  // The tables' actions (ADR-0016 §4, note 25 of 2026-11-11): read once for the workspace, and run through the door.
  const tables = useQuery({ queryKey: ['workspace-tables', workspaceId], queryFn: () => workspace.api.tables(workspaceId), staleTime: Infinity });
  menus.current = useMemo(() => {
    // Not read yet, a table has no actions to offer; read, the server lists every table a grid of this page is bound to.
    const listed = (tableId: string) => {
      if (tables.data === undefined) return undefined;
      const table = tables.data.find((t) => t.tableId === tableId);
      if (table === undefined) throw new Error(`The server lists no table ${tableId}, which a grid of the pricer's page is bound to.`);
      return table;
    };
    return { trades: tableMenu(listed(TRADES_TABLE), door, workspace.notify), csas: tableMenu(listed(CSAS_TABLE), door, workspace.notify) };
  }, [tables.data, door, workspace]);
  const tablesUnread = tables.isError ? describe(tables.error) : undefined;
  useEffect(() => {
    if (tablesUnread !== undefined) workspace.notify('refused', `The grids' menus hold no command of their tables: ${tablesUnread}`);
  }, [tablesUnread, workspace]);
  // What a menu calls an item: the page's own words, else a table's action by its title and its line. One function for
  // the page's life, so no grid is made again for it.
  const commandLabel = useCallback((id: string) => pageLabel(id) ?? menus.current.trades.label(id) ?? menus.current.csas.label(id), []);
  /** The grids' undo and redo keys (ADR-0016 §11): the notice says what was taken back or put back, or why nothing was. */
  const takeBack = async (verb: 'undo' | 'redo') => {
    const reason = await door.invoke(`workspace.${verb}`, {});
    if (reason !== undefined) workspace.notify('refused', reason.sentence);
  };
  /** The Changes tab's removals, as `case remove` and `cases clear` make them (ADR-0016 note 28): the tab names a case
   * by its id, which no other case has, and the notice is the server's sentence of what was removed. */
  const removeCase = async (caseId: string, title: string) => {
    const reason = await door.invoke('xva.case.remove', { case: caseId });
    if (reason !== undefined) workspace.notify('refused', `${title} was not removed: ${reason.sentence}`);
  };
  const clearCases = async () => {
    const reason = await door.invoke('xva.cases.clear', {});
    if (reason !== undefined) workspace.notify('refused', `No case was removed: ${reason.sentence}`);
  };
  /** A value as written (a spread with its unit, a setting), read and checked by the server as its cell reads it, in the
   * Base; refused, the page says why and the answer is the reason. */
  const setText: SetText = (entity, field, text, title) =>
    attempt(`${title} was not changed`, (revision) => api.setChange(workspaceId, revision, entity, field, text));
  const changeActions: ChangeActions = {
    set: (change, text) => void apply(`${change.title} was not changed`, (revision) =>
      api.setChange(workspaceId, revision, change.entity, change.field, text, change.case ?? undefined)),
    remove: (change) => void queue.push({ entity: change.entity, field: change.field, resolution: 'reset', case: change.case ?? undefined, title: change.title }),
    removeCase: (caseId, title) => void removeCase(caseId, title),
    clearCases: () => void clearCases(),
    leaveOut: (caseId, title, list, trade) => void leaveOut(caseId, title, list, trade),
  };

  /** The *Add each* button (ADR-0016 note 37): `add each` as the line types it, a case per new trade with A ticked. */
  const addEach = () => invoked('xva.trades.add', {}, 'No case was made');
  /**
   * `add each`'s whole set of iterations (ADR-0016 note 37), said again with one change: made in the command's turn, of
   * the cases the page shows at the revision the command is sent at, so a case an edit still on its way makes or
   * removes is in it (spine 6). A page that shows another revision by then sends nothing and says so.
   */
  const iterationsWith = (change: (cases: readonly CaseView[]) => readonly Iteration[]) => (revision: number): CommandRequest => {
    const shown = queryClient.getQueryData<XvaWorkspaceView>(['xva', workspaceId]);
    if (shown === undefined || shown.revision !== revision)
      throw new Error(`The page shows ${shown === undefined ? 'no workspace' : `revision ${shown.revision}`}, and the command would run at revision ${revision}. Try again.`);
    return { iterations: change(shown.cases) };
  };
  // *Make case…* (ADR-0016 note 37): the trades the two grids' selections name, while its dialog is open.
  const [caseOf, setCaseOf] = useState<Iteration | undefined>(undefined);
  /**
   * Opens *Make case from the selection…* on what the two trade grids' selections name: read by the server, as the
   * line's `selected` is (ADR-0016 note 17), since it pages the grids and a position means a row only in its order.
   * From a Context Menu the grid it opened on is read as the menu has it. Refused, as where a grid's order has moved
   * since its rows were read, the page says why and opens nothing.
   */
  const openCase = async (under?: { readonly grid: TradeGrid; readonly view: LineView }) => {
    const selected = async (grid: TradeGrid) => {
      const seen = under?.grid === grid ? under.view : tradeScreen[grid]()?.view;
      if (seen === undefined) throw new Error(`The page has no grid of ${grid} trades to read a selection of.`);
      return (await workspace.api.selection(workspaceId, seen)).keys;
    };
    try {
      const [add, without] = await Promise.all([selected('new'), selected('existing')]);
      setCaseOf({ add, without });
    } catch (error) {
      workspace.notify('refused', `The selection was not read: ${describe(error)}`);
    }
  };
  /**
   * *Make case*: the dialog's case, beside the cases made, which the server keeps as they are (`keep`, note 37 as
   * corrected in review). Refused, the answer is the server's reason, for the dialog to say.
   */
  const makeCase = async (iteration: Iteration) => (await door.invoke('xva.trades.add', { iterations: [iteration], keep: true }))?.sentence;
  /**
   * The Changes tab's removal of one trade from what a case adds, or from what it takes out of After (ADR-0016 note
   * 37): the whole set said again with that change, so the case is made anew where it stood among the others.
   */
  const leaveOut = async (caseId: string, title: string, list: keyof Iteration, trade: string) => {
    const reason = await door.invoke('xva.trades.add', iterationsWith((cases) => tradeLeftOut(cases, caseId, list, trade)));
    if (reason !== undefined)
      workspace.notify('refused', `${trade} was not ${list === 'add' ? 'left out of' : 'kept in After in'} ${title}: ${reason.sentence}`);
  };
  /**
   * *Vary values…* (ADR-0010 §3): a case per value of the dialog's cell, named by its entity and field, through
   * `xva.vary` as the line's `vary` goes (ADR-0016 note 28), in the page's turn (spine 6). Answers what the server said
   * it made and skipped, for the dialog to say; refused, it throws with the server's reason.
   */
  const varyCell = async (target: VaryTarget, values: string): Promise<string> => {
    let said: string | undefined;
    const reason = await door.invoke('xva.vary', { entity: target.entity, field: target.field, values }, (sentence) => { said = sentence; });
    if (reason !== undefined) throw new Error(reason.sentence);
    if (said === undefined) throw new Error('vary was taken, and the page\'s door said nothing of what it made.');
    return said;
  };
  /**
   * The Scenarios grid's own edit (ADR-0010 §4): cells typed, pasted or cleared, after the edits already sent (spine 6).
   * The workspace's trades do not move, so the grids are not read again. Refused, it throws with the server's reason.
   */
  const editShifts = async (rows: readonly ShiftEdit[]) => {
    show(await queue.after(async (revision) => {
      const answer = await api.editShifts(workspaceId, revision, rows);
      queue.rebase(answer.revision);
      return answer;
    }));
  };
  /**
   * The Scenarios tab's gestures on a scenario by name are the commands the line types (ADR-0016 note 32): `scenario
   * include`, `exclude`, `remove`, `duplicate` and `scenarios clear`, so a box and a line cannot differ and `undo` names
   * both the same. The notice is the server's sentence; refused, the page says what was not done and why.
   * Duplicate's dialog stays open with the server's reason when it is refused, so that one throws.
   */
  const duplicateScenario = async (name: string, copy: string) => {
    const reason = await door.invoke('xva.scenario.duplicate', { name, copy });
    if (reason !== undefined) throw new Error(reason.sentence);
  };
  const scenarioActions: ScenarioActions = {
    edit: editShifts,
    include: (name, included) => void invoked(`xva.scenario.${included ? 'include' : 'exclude'}`, { name }, `${name} was not ${included ? 'included' : 'excluded'}`),
    duplicate: duplicateScenario,
    remove: (name) => void invoked('xva.scenario.remove', { name }, `${name} was not removed`),
    clear: () => void invoked('xva.scenarios.clear', {}, 'No scenario was removed'),
    notify: workspace.notify,
  };
  // *Vary values…* (ADR-0010 §3): the cell whose Context Menu opened the dialog, while it is open.
  const [varying, setVarying] = useState<VaryTarget | undefined>(undefined);
  const setCurrency = (currency: string) => void queue.push({ entity: SETTINGS_ENTITY, field: 'displayCurrency', value: currency, title: 'The display currency' });

  // The commands run what the page's own controls run, as the page stands when they run (ADR-0010 §9).
  const pageCommands = { takeBack, retry, openCase, copyQuote, download, explain, openResult, setDockFull, resultTabs, traceInView, measures: () => view.data?.measures ?? [] };
  const pageRef = useRef(pageCommands);
  pageRef.current = pageCommands;
  useEffect(() => {
    registerXvaPage(panelId, {
      downloadRun: () => pageRef.current.download(),
      copyQuote: () => void pageRef.current.copyQuote(),
      explain: (measure, path) => pageRef.current.explain(measure, path),
      openResult: (tab) => pageRef.current.openResult(tab),
      setDockFull: (full) => pageRef.current.setDockFull(full),
      resultTabs: () => pageRef.current.resultTabs(),
      traceInView: () => pageRef.current.traceInView(),
      measures: () => pageRef.current.measures(),
    });
    registerCommandDoor(panelId, door);
    return () => { registerXvaPage(panelId, undefined); registerCommandDoor(panelId, undefined); };
  }, [panelId, door]);
  // A trade grid as the line is told of it: the trades of its subset, its Selection as positions in the order its rows
  // were answered in, and its Focus by its column's name, which is the field's where the column is one. A place read for
  // that view (ADR-0016 note 21) names its row by position, since the server pages these grids: none is looked for by key.
  const goToNow = useRef(goTo);
  goToNow.current = goTo;
  const tradeScreen = useMemo(() => {
    const of = (grid: TradeGrid, handle: { readonly current: ExGridApi<Row> | null }) => (): Screen | undefined => {
      const shown = handle.current;
      if (shown === null) return undefined;
      const declared = declaredOf(grid);
      const read = shown.readView();
      return {
        view: pagedView(declared, read, sources[grid].snapshot(), sources.answered),
        put: (place) => placeFocus(declared, shown, read, place) ?? goToNow.current(grid),
        // The grid's own Sort and Filter (notes 39 to 41): its header shows the sort, and its reader is asked again.
        sort: (asked) => sortGrid(declared, sources[grid], asked),
        filter: (asked) => filterGrid(sources[grid], asked),
        // The server said the run by its first position in the order this view was read in (note 43).
        select: (asked) => selectRows(declared, shown, read, asked),
      };
    };
    return { new: of('new', newGrid), existing: of('existing', existingGrid) };
  }, [sources]);
  // The button of a filtered grid's caption takes its conditions off, as `filter off` does for the grid behind the line.
  const filterOff = useMemo(() => ({ new: () => { filterGrid(sources.new, {}); }, existing: () => { filterGrid(sources.existing, {}); } }), [sources]);
  // The grids' undo and redo keys run the same two commands; the functions are the same for the page's life, so no
  // grid is made again for them.
  const gridUndo = useMemo(() => ({ undo: () => void pageRef.current.takeBack('undo'), redo: () => void pageRef.current.takeBack('redo') }), []);

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const blocking = ws.conflicts.length + ws.lost.filter((l) => !l.acknowledged).length;

  const pricedRun = ws.runs[0]?.runId;
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
  // What the two grids' readers failed with, said once where it is the same.
  const failures = [...new Set([failure.new, failure.existing].filter((why) => why !== undefined))];
  const page = (
    <Stack gap="sm" className="qube-xva" data-section={section} data-dock={full ? 'full' : 'part'} style={{ ['--qube-xva-taken' as string]: `${taken}px` }}
      // A grid entered is the screen only where it says so below, after this: a grid of no table leaves the line none.
      onFocusCapture={(event) => { if (event.target instanceof Element && event.target.closest('.ex-grid') !== null) screen.current = undefined; }}>
      {/* The context bar (ADR-0010 §1; docs/design-guidelines.md §3, §4): one 30px Control Height, each label beside its input. */}
      <div className="qube-toolbar" role="toolbar" aria-label="Workspace">
        <ValueDate value={ws.context.valueDate} disabled={!notBusy}
          onCommit={(date) => void reacquire('Value date moved', (revision) => api.changeValueDate(workspaceId, revision, date))}
          onRefused={(sentence) => workspace.notify('refused', sentence)} />
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
                onClick={() => void setMeasures(on ? ws.selectedMeasures.filter((id) => id !== m.id) : [...ws.selectedMeasures, m.id])}>{m.title}</Button>
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
          catalogue={catalogue.isError ? { refused: describe(catalogue.error) } : catalogue.data} onGridEntered={(seen) => { screen.current = seen; }} onPlaced={() => openTab('csa')}
          tab={inputTab} onTab={(tab) => { setInputTab(tab); setInputsOpen(true); }} open={inputsOpen} onOpen={setInputsOpen} height={inputsHeight} focusRequest={focusRequest}
          changeList={changeList.data} changeActions={changeActions} scenarioActions={scenarioActions} onVary={setVarying} commandLabel={commandLabel} csaMenu={menus} />
        <VaryDialog target={varying} vary={varyCell} onClose={() => setVarying(undefined)} />
        <CaseDialog selection={caseOf} make={makeCase} onClose={() => setCaseOf(undefined)} />
        {inputsOpen && <Resizer label="Height of the input tabs" value={inputsHeight} min={48} max={640} grows="down" onChange={setInputsDragged} />}
      </div>
      {issue !== undefined && <Text size="xs" c="red" role="alert" data-testid="field-issue">{issue}</Text>}
      <div data-part="trades" className="qube-xva-part">
        <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(edit, resolution) => void queue.push({ entity: edit.entity, field: edit.field, resolution, case: edit.case ?? undefined, title: `${edit.entity} ${edit.field}` })} />
        {failures.length > 0 && <Alert color="red" variant="light" withCloseButton onClose={() => setFailure(NOT_FAILED)} title="The trades could not be read">{failures.join(' ')}</Alert>}
        {/* New trades above Existing (ADR-0010 §6): New as high as its rows, about eight at most; Existing takes the rest. */}
        <div className="qube-xva-grids">
          <TradeCaption title="New trades" count={ws.newTrades} grid="new" setSide={(side, value, grid) => void setSide(side, value, grid)} disabled={!notBusy}
            source={sources.new} counted={counted.new} onFilterOff={filterOff.new}>
            {/* The cases of `add each` (ADR-0016 note 37): one per ticked new trade, or one of the two grids' selections. */}
            <Group gap="xs" role="group" aria-label="Cases that add new trades">
              <Button size="compact-xs" variant="default" disabled={!notBusy} onClick={() => void addEach()}
                title={notBusy ? 'Make a case per new trade with A ticked, each added alone beside the Base (add each).' : WAIT}>Add each</Button>
              <Button size="compact-xs" variant="default" disabled={!notBusy} onClick={() => void openCase()}
                title={notBusy ? 'Make one case that adds the new trades selected together and takes the existing trades selected out of After.' : WAIT}>Make case…</Button>
            </Group>
          </TradeCaption>
          <div aria-label="New trades" onFocusCapture={() => { screen.current = tradeScreen.new; }}>
            <QubeGrid<Row> ref={newGrid} columns={newWidths.columns} onColumnWidthChanged={newWidths.onColumnWidthChanged} source={sources.new} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
              contextCommands={newCommands} commandLabel={commandLabel}
              density="compact" viewportWidth="stretch" viewportHeight={newHeight} pinnedColumnCount={1} locale="en-US" />
          </div>
          <TradeCaption title="Existing trades" count={ws.trades - ws.newTrades} grid="existing" setSide={(side, value, grid) => void setSide(side, value, grid)} disabled={!notBusy}
            source={sources.existing} counted={counted.existing} onFilterOff={filterOff.existing} />
          <div aria-label="Existing trades" onFocusCapture={() => { screen.current = tradeScreen.existing; }}>
            <QubeGrid<Row> ref={existingGrid} className="qube-xva-trades" columns={existingWidths.columns} onColumnWidthChanged={existingWidths.onColumnWidthChanged}
              source={sources.existing} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
              contextCommands={existingCommands} commandLabel={commandLabel}
              density="compact" viewportWidth="stretch" viewportHeight="stretch" pinnedColumnCount={1} locale="en-US" />
          </div>
        </div>
      </div>
      {/* The result dock (ADR-0010 §7) at the bottom, in view while the trades scroll: one line a case collapsed. */}
      <div ref={dock} tabIndex={-1} data-part="results" className="qube-xva-part qube-xva-dock">
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

const NOT_FAILED: Readonly<Record<TradeGrid, string | undefined>> = { new: undefined, existing: undefined };

/** What a trade grid's reader answered under the grid's conditions: how many rows they leave, of how many. */
interface Counted { readonly where: string; readonly left: number; readonly of: number }

/**
 * A trade grid's caption: its title and count, and B and A for every row of it (ADR-0010 §6). ExGrid gives a Consumer
 * no header action, so the all/none controls sit here, and are the commands `before all|none` and `after all|none`.
 * A grid filtered from the line says so here (ADR-0016 note 41): its conditions, how many rows they leave once its
 * reader has answered under them (`counted`), and the button that takes them off.
 */
function TradeCaption({ title, count, grid, setSide, disabled, source, counted, onFilterOff, children }: {
  readonly title: string;
  readonly count: number;
  readonly grid: TradeGrid;
  readonly setSide: (side: 'before' | 'after', value: boolean, grid: TradeGrid) => void;
  readonly disabled: boolean;
  readonly source: Heard;
  readonly counted: Counted | undefined;
  readonly onFilterOff: () => void;
  /** The grid's own buttons, before B and A. */
  readonly children?: ReactNode;
}) {
  const one = grid === 'new' ? 'new trade' : 'existing trade';
  const where = useWhere(source);
  const filter: FilterSaid | undefined = where === undefined ? undefined
    : counted?.where === where ? { where, counted: { left: counted.left, of: counted.of } } : { where };
  return (
    <Group gap="xs" wrap="wrap" className="qube-grid-caption">
      <Text size="sm" fw={600}>{title}</Text>
      <Text size="xs">{count}</Text>
      <GridFilterLine said={filter} grid={`the ${one}s`} onOff={onFilterOff} />
      <Group gap="md" ml="auto" wrap="wrap">
        {children}
        <Group gap="xs" role="group" aria-label={`Before and After for every ${one}`}>
        {(['before', 'after'] as const).flatMap((side) => [true, false].map((value) => (
          <Button key={`${side}.${value}`} size="compact-xs" variant="default" disabled={disabled}
            aria-label={`${value ? 'Tick' : 'Clear'} ${side === 'before' ? 'Before' : 'After'} on every ${one}`}
            onClick={() => setSide(side, value, grid)}>{side === 'before' ? 'B' : 'A'} {value ? 'all' : 'none'}</Button>
          )))}
        </Group>
      </Group>
    </Group>
  );
}

function ValueDate({ value, disabled, onCommit, onRefused }: {
  readonly value: string;
  readonly disabled: boolean;
  readonly onCommit: (date: string) => void;
  readonly onRefused: (sentence: string) => void;
}) {
  return (
    <ToolbarField label="Value date">
      {(id) => (
        <CommittedDateInput id={id} size="xs" className="qube-toolbar-input qube-toolbar-input--date" what="value date" value={value}
          disabled={disabled} onCommit={onCommit} onRefused={onRefused} />
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
