import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import './xva.css';
import { Alert, Badge, Button, Group, Modal, SegmentedControl, Select, Stack, Table, Text, TextInput, useMantineTheme } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridContextMenuContext, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { WorkbenchGrid } from '../../platform/grid/WorkbenchGrid';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../platform/api';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerGridFocus } from '../../shell/focus';
import type { XvaApi } from './api';
import type { EffectiveTrade, HeldEdit, RegisteredColumn, RegisteredMeasure, Resolution, ScopeDrop, XvaWorkspaceView } from './contracts';
import { registerXvaPage } from './commands';
import { XvaResult } from './XvaResult';
import { BASE_CASE, BASE_SCENARIO, openTrace } from '../../platform/traces/open';
import { EditQueue, MEASURES_ENTITY, SETTINGS_ENTITY, tradeFields, type PendingEdit } from './editing';
import { ToolbarField } from './fields';
import { XvaInputs, type InputTab } from './XvaInputs';
import { compactGridHeight, GRID_FRAME, pixels, Resizer } from './layout';
import { declaring, membershipColumn } from './membership';
import { priceLabel } from './metrics';
import { differs, originalMessage } from './original';
import { copyRich, downloadRun } from '../../platform/runs';
import { cellMessage as moduleCellMessage, cellRefusal, cellState as moduleCellState, failed, moduleColumns, parseCellNumber } from './cells';

// The fixed XVA workspace (docs/03, ADR-0003): the server holds the trades and every rule; this page shows
// the effective rows a page at a time and sends what the user changed. ExGrid draws; it decides nothing.

export const XVA_RESULT_VIEW = 'view.xva.result';

const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
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
  column('notional', 'Notional', 'number', 168, { editable: true, format: (v) => amount.format(v as number) }),
  column('maturity', 'Maturity', 'text', 112),
  column('fixedRate', 'Fixed rate', 'number', 112, { editable: true, format: (v) => rate.format(v as number) }),
  column('schedule', 'Schedule', 'text', 104, { editable: true }),
];
const assignment: readonly GridColumn<Row>[] = [
  column('csa', 'CSA', 'text', 120, { editable: true }),
  column('curve', 'Curve', 'text', 112, { editable: true }),
  column('before', 'Before', 'boolean', 88, { editable: true }),
  column('after', 'After', 'boolean', 80, { editable: true }),
];
// The selected modules' columns stand between B and A and these (ADR-0010 §6, §10): on the New grid, XVA's PV01,
// Margin and PV Adj; then a membership column per selected measure that declares one, such as FVA's.
const derived: readonly GridColumn<Row>[] = [
  { name: 'issues', header: 'Issues', type: 'text', width: 260, value: (row) => (row.issues.length === 0 ? undefined : row.issues.join(' ')), format: text },
];
const existingTerms: readonly GridColumn<Row>[] = [
  ...leading,
  column('lastFixing', 'Last fixing', 'number', 116, { format: (v) => rate.format(v as number) }),
  // The books' PV, the source's and read-only (ADR-0010 §6): the PV an adjustment uses.
  column('officialPv', 'Official PV', 'number', 152, { format: (v) => amount.format(v as number) }),
  ...assignment,
];

export type TradeGrid = 'new' | 'existing';

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
/** The open tab's height as its content: Netting's row, the sentence that says there is no CSA, or the CSA grid in its
 * frame under the panel's top gap (`gap`, the theme's `xs`). */
const inputsFit = (tab: InputTab, csas: number, gap: number) =>
  (tab === 'netting' ? 84 : csas === 0 ? 40 : gap + GRID_FRAME + compactGridHeight(Math.min(csas, CSA_ROWS)));

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
  return originalMessage(row, col.name, row[col.name as keyof Row]);
}

/** The failed cell's action (ADR-0010 §6): ask again for that trade's results. */
const RETRY = 'xva.retry';
const commandLabel = (id: string) => (id === RETRY ? 'Ask again' : undefined);

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

export function XvaWorkspacePanel({ panelId, workspaceId, api }: { readonly panelId: string; readonly workspaceId: string; readonly api: XvaApi }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const theme = useMantineTheme();
  // While a module's cell on a new trade waits for its result, such as PV01, the view is read again until none does.
  const view = useQuery({
    queryKey: ['xva', workspaceId], queryFn: () => api.view(workspaceId),
    refetchInterval: (query) => (query.state.data !== undefined && query.state.data.pendingCells > 0 ? 500 : false),
  });
  const registrations = useQuery({ queryKey: ['xva', 'registrations'], queryFn: () => api.registrations(), staleTime: Infinity });
  const newGrid = useRef<ExGridApi<Row>>(null);
  // The grid the keyboard was last in: `before all` and `after all` act on it, Existing until one was.
  const lastGrid = useRef<TradeGrid>('existing');
  const [busy, setBusy] = useState<string | undefined>(undefined);
  const [confirm, setConfirm] = useState<{ readonly registration: string; readonly drop: ScopeDrop } | undefined>(undefined);
  const [failure, setFailure] = useState<string | undefined>(undefined);
  const [issue, setIssue] = useState<string | undefined>(undefined);

  // One source per grid, each paged and sorted on the server (ADR-0010 §6); an edit may move a row in either.
  const sources = useMemo(() => {
    const of = (grid: TradeGrid) => GridSource.fetch<Row>(async (query, signal) => {
      // Filtering is not offered by the server yet; a filter set anyway is refused, never ignored.
      if (query.filter !== undefined) throw new Error('This grid cannot filter yet. Clear the filter to see the trades.');
      const page = await api.rows(workspaceId, { start: query.range.start, count: query.range.count, sorts: query.sorts, grid }, signal);
      return { rows: page.rows, start: page.start, totalCount: page.totalCount, orderToken: page.orderToken };
    }, { rowKey: (row) => row.tradeId, onFetchFailed: (event) => setFailure(describe(event.error)) });
    const grids = { new: of('new'), existing: of('existing') };
    return {
      ...grids,
      changed: () => { grids.new.notifyChanged([]); grids.existing.notifyChanged([]); },
      requery: () => { grids.new.requery(); grids.existing.requery(); },
      dispose: () => { grids.new.dispose(); grids.existing.dispose(); },
    };
  }, [api, workspaceId]);
  useEffect(() => () => sources.dispose(), [sources]);

  const show = useCallback((next: XvaWorkspaceView) => queryClient.setQueryData(['xva', workspaceId], next), [queryClient, workspaceId]);

  const queue = useMemo(() => new EditQueue(0,
    (revision, edit: PendingEdit) => edit.measures !== undefined
      ? api.measures(workspaceId, revision, edit.measures)
      : edit.resolution === undefined
        ? api.edit(workspaceId, revision, edit.entity, edit.field, edit.value)
        : api.resolve(workspaceId, revision, edit.entity, edit.field, edit.resolution),
    (next) => { show(next); sources.changed(); },
    (edit, error) => {
      workspace.notify(`${edit.entity} ${edit.field} was not changed: ${describe(error)}`);
      void api.view(workspaceId).then((fresh) => { queue.rebase(fresh.revision); show(fresh); sources.changed(); });
    }), [api, workspaceId, show, sources, workspace]);
  // The queue follows the revision on screen: the one first read, and any read since that someone else moved
  // (the AI, another page), so the next edit is not refused for a change the user can already see.
  const shownRevision = view.data?.revision;
  useEffect(() => { if (shownRevision !== undefined) queue.shown(shownRevision); }, [shownRevision, queue]);

  // The selected modules' trade columns (ADR-0010 §10), made again only when they change (ExGrid ADR-0003).
  const tradeColumns = view.data?.tradeColumns;
  const columnsKey = tradeColumns === undefined ? '' : tradeColumns.map((c) => c.field).join('|');
  const registered = useRef<readonly RegisteredColumn[]>([]);
  registered.current = tradeColumns ?? [];
  // A membership column per selected measure that declares one (ADR-0010 §10), on both grids.
  const declared = view.data === undefined ? [] : declaring(view.data.measures, view.data.selectedMeasures);
  const declaredKey = declared.map((m) => m.id).join('|');
  const declaredNow = useRef<readonly RegisteredMeasure[]>([]);
  declaredNow.current = declared;
  const membership = useMemo(() => declaredNow.current.map((m) => membershipColumn<Row>(m)), [declaredKey]);
  const newColumns = useMemo<readonly GridColumn<Row>[]>(() => [...leading, ...assignment, ...moduleColumns(registered.current), ...membership, ...derived], [columnsKey, membership]);
  const existingColumns = useMemo<readonly GridColumn<Row>[]>(() => [...existingTerms, ...membership, ...derived], [membership]);

  // The layout (ADR-0010 §1): the input tabs open, or collapsed to their row, at a height the user drags; the dock the
  // same at the bottom; and below `sm` the part in view.
  const [section, setSection] = useState<Section>('trades');
  const [inputTab, setInputTab] = useState<InputTab>('netting');
  const [inputsOpen, setInputsOpen] = useState(true);
  // The input tabs are as high as the open tab's content until the handle is dragged; from then on, as high as it set.
  const [inputsDragged, setInputsDragged] = useState<number | undefined>(undefined);
  const [dockOpen, setDockOpen] = useState(true);
  const [dockHeight, setDockHeight] = useState(DOCK_HEIGHT);
  const [focusRequest, setFocusRequest] = useState(0);
  /** `open netting`, `open csa`: the tab open and in view, with the keyboard in it (ADR-0010 §9). */
  const openTab = (tab: InputTab) => { setInputTab(tab); setInputsOpen(true); setSection('inputs'); setFocusRequest((n) => n + 1); };

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
      void queue.push({ entity: `trade:${intent.row.tradeId}`, field: intent.column, value: typed.value });
      return;
    }
    const parse = tradeFields[intent.column];
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    void queue.push({ entity: `trade:${intent.row.tradeId}`, field: intent.column, value: parsed.value });
  }, [queue]);

  useEffect(() => {
    registerGridFocus(panelId, () => newGrid.current?.focus());
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
      workspace.notify(`${label}: ${carried.carried} edit(s) carried, ${carried.conflicted} in conflict, ${carried.lost} lost.`);
    } catch (error) {
      workspace.notify(`${label} failed: ${describe(error)}`);
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
      void queryClient.invalidateQueries({ queryKey: ['xva', workspaceId] });
      void queryClient.invalidateQueries({ queryKey: ['history'] });
    } catch (error) {
      const targets = error instanceof ApiError && error.envelope.issues.length > 0 ? ` (${error.envelope.issues.map((i) => i.path).join(', ')})` : '';
      workspace.notify(`Price was refused: ${describe(error)}${targets}`);
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
      workspace.notify(asked === 0 ? 'No result had failed, so nothing was asked again.' : `Asked again for ${asked} result(s).`);
    } catch (error) {
      workspace.notify(`Nothing was asked again: ${describe(error)}`);
    }
  };
  const contextCommands = useCallback((context: GridContextMenuContext<Row>) =>
    failed(context.row, context.column) ? [{ id: RETRY, enabled: true, invoke: () => void pageRef.current.retry(context.row.tradeId) }] : [], []);

  /** The Run the dock shows: the one priced here last, else the workspace's latest. */
  const dockRun = () => priced ?? view.data?.runs[0]?.runId;
  const copyQuote = async () => {
    const runId = dockRun();
    if (runId === undefined) { workspace.notify('Nothing is priced yet, so there is no quote to copy.'); return; }
    try {
      const quote = await api.quote(runId);
      await copyRich(quote.text, quote.html);
      workspace.notify(`The quote of ${runId} is on the clipboard.`);
    } catch (error) {
      workspace.notify(`The quote was not copied: ${describe(error)}`);
    }
  };
  const download = () => {
    const runId = dockRun();
    if (runId === undefined) { workspace.notify('Nothing is priced yet, so there is no Run to download.'); return; }
    downloadRun(runId);
  };

  /** Opens a measure's trace for a case of the dock's Run, at a term or its root (ADR-0011 §6, §7). */
  const explain = (measure: string, path?: string, caseId: string = BASE_CASE) => {
    const runId = dockRun();
    if (runId === undefined) { workspace.notify('Nothing is priced yet, so there is no trace to explain.'); return; }
    const registered = view.data?.measures.find((m) => m.id === measure);
    const traced = (view.data?.measures ?? []).filter((m) => m.traced).map((m) => m.id);
    if (registered === undefined) { workspace.notify(`${measure} is not a measure of this page. The measures that keep a trace: ${traced.join(', ') || 'none'}.`); return; }
    if (!registered.traced) { workspace.notify(`${registered.title} keeps no trace to explain. The measures that do: ${traced.join(', ') || 'none'}.`); return; }
    openTrace(workspace.store.getState(), { runId, case: caseId, scenario: BASE_SCENARIO, measure }, `${registered.title} · ${view.data?.registration.key ?? ''}`.trim(), path);
  };

  const setMeasures = (measures: readonly string[]) => void queue.push({ entity: MEASURES_ENTITY, field: 'measures', measures });
  /** B or A for every row of a grid, as one edit (ADR-0010 §6); a row's own edit made later wins over it. */
  const setSide = (side: 'before' | 'after', value: boolean, grid: TradeGrid = lastGrid.current) =>
    void queue.push({ entity: `grid:${grid}`, field: side, value });

  const refresh = () => reacquire('Refreshed', (revision) => api.refresh(workspaceId, revision));
  const setCurrency = (currency: string) => void queue.push({ entity: SETTINGS_ENTITY, field: 'displayCurrency', value: currency });

  // The commands run what the page's own controls run, as the page stands when they run (ADR-0010 §9).
  const pageRef = useRef({ price, setMeasures, setSide, retry, copyQuote, download, refresh, setCurrency, openTab, explain, measures: () => view.data?.measures ?? [], currencies: () => view.data?.currencies ?? [] });
  pageRef.current = { price, setMeasures, setSide, retry, copyQuote, download, refresh, setCurrency, openTab, explain, measures: () => view.data?.measures ?? [], currencies: () => view.data?.currencies ?? [] };
  useEffect(() => {
    registerXvaPage(panelId, {
      price: () => void pageRef.current.price(),
      setMeasures: (measures) => pageRef.current.setMeasures(measures),
      setSide: (side, value, grid) => pageRef.current.setSide(side, value, grid),
      retry: () => void pageRef.current.retry(),
      downloadRun: () => pageRef.current.download(),
      copyQuote: () => void pageRef.current.copyQuote(),
      refresh: () => void pageRef.current.refresh(),
      setCurrency: (currency) => pageRef.current.setCurrency(currency),
      openTab: (tab) => pageRef.current.openTab(tab),
      explain: (measure, path) => pageRef.current.explain(measure, path),
      measures: () => pageRef.current.measures(),
      currencies: () => pageRef.current.currencies(),
    });
    return () => registerXvaPage(panelId, undefined);
  }, [panelId]);

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const blocking = ws.conflicts.length + ws.lost.filter((l) => !l.acknowledged).length;

  const pricedRun = priced ?? ws.runs[0]?.runId;
  const pricedRevision = ws.runs.find((r) => r.runId === pricedRun)?.revision;
  const notBusy = busy === undefined;

  const enforceable = ws.agreement.enforceable ? 'enforceable' : 'not enforceable';
  const inputsHeight = inputsDragged ?? inputsFit(inputTab, ws.csas.length, pixels(theme.spacing.xs));
  const newHeight = compactGridHeight(Math.max(1, Math.min(ws.newTrades, 8)));
  // What the parts other than Existing take, from the heights the page set (never measured): Existing has the rest.
  const taken = TAKEN + (inputsOpen ? inputsHeight + RESIZER : 0) + newHeight
    + (pricedRun === undefined ? 24 : dockOpen ? dockHeight + RESIZER + 32 : 48);
  return (
    <Stack gap="sm" className="qwb-xva" data-section={section} style={{ ['--qwb-xva-taken' as string]: `${taken}px` }}>
      {/* The context bar (ADR-0010 §1; docs/design-guidelines.md §3, §4): one 30px Control Height, each label beside its input. */}
      <div className="qwb-toolbar" role="toolbar" aria-label="Workspace">
        <ValueDate value={ws.context.valueDate} disabled={!notBusy}
          onCommit={(date) => void reacquire('Value date moved', (revision) => api.changeValueDate(workspaceId, revision, date))} />
        <ToolbarField label="Counterparty" wide>
          {(id) => (
            <Select id={id} size="xs" className="qwb-toolbar-input qwb-toolbar-input--wide" allowDeselect={false} searchable value={ws.registration.key}
              data={(registrations.data ?? [ws.registration]).map((r) => ({ value: r.key, label: `${r.code} | ${r.legalEntity} | ${r.name}` }))}
              onChange={(value) => void chooseCounterparty(value)} disabled={!notBusy} comboboxProps={{ withinPortal: true }} />
          )}
        </ToolbarField>
        <div className="qwb-toolbar-field" data-testid="netting-set">
          <Text size="xs" c="dimmed" className="qwb-toolbar-label">Netting set</Text>
          <Badge variant="light" color={ws.agreement.enforceable ? 'gray' : 'yellow'} size="lg" radius="sm"
            title={`${ws.agreement.maId}, the main netting set, is ${enforceable} (the source's).`}>{ws.agreement.nettingSetId} · {enforceable}</Badge>
        </div>
        <ToolbarField label="Currency">
          {(id) => (
            <Select id={id} size="xs" className="qwb-toolbar-input qwb-toolbar-input--currency" allowDeselect={false} value={ws.displayCurrency}
              data={ws.currencies} onChange={(value) => { if (value !== null) setCurrency(value); }} disabled={!notBusy} comboboxProps={{ withinPortal: true }} />
          )}
        </ToolbarField>
        <Button size="xs" variant="default" onClick={() => void refresh()} loading={busy === 'Refreshed'} disabled={!notBusy && busy !== 'Refreshed'}
          title="Acquire the trades again for this value date; your edits carry or are held.">Refresh</Button>
        <div className="qwb-toolbar-field" role="group" aria-label="Measures">
          <Text size="xs" c="dimmed" className="qwb-toolbar-label" aria-hidden>Measures</Text>
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
          {...(blocking > 0 ? { title: `${blocking} held edit(s) on priced trades must be resolved first.` } : {})}>{priceLabel(ws.measures, ws.selectedMeasures, 1, { before: ws.inBefore > 0, after: ws.inAfter > 0 })}</Button>
      </div>
      {/* The status line: what the figures stand on, and SIMULATED while a provider is. */}
      <Group gap="xs" wrap="wrap" className="qwb-xva-status">
        <Text size="xs" data-testid="workspace-summary">{ws.trades} trades · {ws.overrides} edit(s) · revision {ws.revision} · {ws.sourceVersion}</Text>
        <Text size="xs">{ws.agreement.maId} · {ws.agreement.collateralised ? `main CSA ${ws.agreement.mainCsaId}` : ws.agreement.noCsa}</Text>
        {ws.simulated.length > 0 && (
          <Badge variant="light" color="yellow" data-testid="simulated" title={`Figures from a simulated provider: ${ws.simulated.join(', ')}. They are not a model of anything.`}>SIMULATED</Badge>
        )}
      </Group>
      {/* Below `sm` the screen is three tabs (ADR-0010 §1), with the context bar above them. */}
      <SegmentedControl hiddenFrom="sm" size="xs" fullWidth aria-label="Part of the page" value={section} onChange={(v) => setSection(v as Section)}
        data={[{ value: 'inputs', label: 'Inputs' }, { value: 'trades', label: 'Trades' }, { value: 'results', label: 'Results' }]} />
      <div data-part="inputs" className="qwb-xva-part">
        <XvaInputs ws={ws} onIssue={setIssue} push={(entity, field, value) => void queue.push({ entity, field, value })}
          tab={inputTab} onTab={(tab) => { setInputTab(tab); setInputsOpen(true); }} open={inputsOpen} onOpen={setInputsOpen} height={inputsHeight} focusRequest={focusRequest} />
        {inputsOpen && <Resizer label="Height of the input tabs" value={inputsHeight} min={48} max={640} grows="down" onChange={setInputsDragged} />}
      </div>
      {issue !== undefined && <Text size="xs" c="red" role="alert" data-testid="field-issue">{issue}</Text>}
      <div data-part="trades" className="qwb-xva-part">
        <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(edit, resolution) => void queue.push({ entity: edit.entity, field: edit.field, resolution })} />
        {failure !== undefined && <Alert color="red" variant="light" withCloseButton onClose={() => setFailure(undefined)} title="The trades could not be read">{failure}</Alert>}
        {/* New trades above Existing (ADR-0010 §6): New as high as its rows, about eight at most; Existing takes the rest. */}
        <div className="qwb-xva-grids">
          <TradeCaption title="New trades" count={ws.newTrades} grid="new" setSide={setSide} disabled={!notBusy} />
          <div aria-label="New trades" onFocusCapture={() => { lastGrid.current = 'new'; }}>
            <WorkbenchGrid<Row> ref={newGrid} columns={newColumns} source={sources.new} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
              contextCommands={contextCommands} commandLabel={commandLabel}
              density="compact" viewportWidth="stretch" viewportHeight={newHeight} pinnedColumnCount={1} locale="en-US" />
          </div>
          <TradeCaption title="Existing trades" count={ws.trades - ws.newTrades} grid="existing" setSide={setSide} disabled={!notBusy} />
          <div aria-label="Existing trades" onFocusCapture={() => { lastGrid.current = 'existing'; }}>
            <WorkbenchGrid<Row> className="qwb-xva-trades" columns={existingColumns} source={sources.existing} onEdit={onEdit} cellState={cellState} cellMessage={cellMessage}
              density="compact" viewportWidth="stretch" viewportHeight="stretch" pinnedColumnCount={1} locale="en-US" />
          </div>
        </div>
      </div>
      {/* The result dock (ADR-0010 §7) at the bottom, in view while the trades scroll: one line a case collapsed. */}
      <div data-part="results" className="qwb-xva-part qwb-xva-dock">
        {pricedRun === undefined
          ? <Text size="sm" data-testid="xva-result-none">Nothing is priced yet. Price shows its figures here.</Text>
          : (
            <>
              {dockOpen && <Resizer label="Height of the result dock" value={dockHeight} min={48} max={560} grows="up" onChange={setDockHeight} />}
              <XvaResult runId={pricedRun} pricedRevision={pricedRevision} revision={ws.revision} register={ws.measures} onOpen={() => openRun(pricedRun)}
                onCopyQuote={() => void copyQuote()} onTrace={(measure, caseId) => explain(measure, undefined, caseId)}
                open={dockOpen} onOpenChange={setDockOpen} height={dockHeight} />
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
    <Group gap="xs" wrap="wrap" className="qwb-grid-caption">
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
        <TextInput id={id} type="date" size="xs" className="qwb-toolbar-input qwb-toolbar-input--date" value={draft} disabled={disabled}
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
