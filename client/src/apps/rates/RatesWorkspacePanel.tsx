import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Button, Group, Stack, Table, Text, TextInput } from '@mantine/core';
import { GridSource, type CellPosition, type GridActionEvent, type GridColumn, type GridContextMenuContext, type GridEditIntent, type GridSelection, type HeaderGroupToggle } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../platform/api';
import { useColumnWidths } from '../../platform/grid/columnWidths';
import { EditQueue } from '../../platform/workspace/editQueue';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerGridFocus } from '../../shell/places';
import type { FrameStream, RatesApi } from './api';
import { registerRatesCommands } from './commands';
import type { CashflowsView, HeldEdit, LiveFrame, PeriodView, RatesWorkspaceView, Resolution } from './contracts';
import {
  FOLDED, PINNED_COLUMNS, headerTiers, lineCellMessage, lineResult, lineCellState, lineColumns, hiddenEdits, lineHeaderGroups, lineRows, openedAt, parserOf, periodCellState,
  lineFieldTitle, periodColumns, periodFieldTitle, periodParserOf, toggled, type HiddenEdits, type LineLayout, type LineRow,
} from './lines';

// The fixed Rates line pricer (docs/07, C37 to C43): one line per trade in ExGrid, its terms sparse, its
// standard cashflows generated and customised on the server. Live frames tick the figures; Price and Capture
// make Runs. The server holds every rule; this page sends what the user changed and shows what came back.

export const RATES_RESULT_VIEW = 'view.rates.result';

type Edit =
  | { readonly kind: 'line'; readonly line: string; readonly field: string; readonly value: unknown }
  | { readonly kind: 'cashflow'; readonly line: string; readonly period: string; readonly field: string; readonly value: unknown }
  | { readonly kind: 'resolve'; readonly held: HeldEdit; readonly resolution: Resolution }
  | { readonly kind: 'add'; readonly product: 'irs' | 'ccs' }
  | { readonly kind: 'new'; readonly product: string; readonly terms: string }
  | { readonly kind: 'defaults'; readonly defaults: RatesWorkspaceView['defaults'] }
  | { readonly kind: 'remove'; readonly line: string };

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

/**
 * What did not happen when an edit is refused (ADR-0006's note 2 of 2026-11-11), in the screen's words, before the
 * server's reason: a field by its column's header, never by its key or by the edit's kind.
 */
function notDone(edit: Edit): string {
  switch (edit.kind) {
    case 'line': return `${edit.line} ${lineFieldTitle(edit.field)} was not changed`;
    case 'cashflow': return `${edit.line} cashflow ${edit.period} ${periodFieldTitle(edit.field)} was not changed`;
    // The list of held edits names one by its entity and its field, so its refusal does.
    case 'resolve': return `${edit.held.entity} ${edit.held.field} was not resolved`;
    case 'add': return 'The line was not added';
    case 'new': return 'The line was not created';
    case 'defaults': return 'The defaults were not set';
    case 'remove': return `${edit.line} was not removed`;
  }
}

/** Each column set made once: a new column is a new declaration, and every row paints again (ExGrid ADR-0003). */
const columnSets = new Map<string, ReturnType<typeof lineColumns>>();
const groupSets = new Map<string, ReturnType<typeof lineHeaderGroups>>();
const keyOfLayout = (layout: LineLayout) => `${layout.trade}/${layout.leg1}/${layout.leg2}`;
function columnsOf(layout: LineLayout) {
  let columns = columnSets.get(keyOfLayout(layout));
  if (columns === undefined) columnSets.set(keyOfLayout(layout), columns = lineColumns(layout));
  return columns;
}
function groupsOf(layout: LineLayout, hides: HiddenEdits) {
  const key = `${keyOfLayout(layout)}/${Object.values(hides).join('/')}`;
  let groups = groupSets.get(key);
  if (groups === undefined) groupSets.set(key, groups = lineHeaderGroups(layout, hides));
  return groups;
}

/** What each page opened, kept while the application runs, so a page left and come back to is as it was (ADR-0015 §7). */
const openedByPanel = new Map<string, LineLayout>();
const commandLabel = (id: string) => (id === REMOVE_LINE ? 'Remove line' : undefined);
const REMOVE_LINE = 'remove-line';

export function RatesWorkspacePanel({ panelId, workspaceId, api }: { readonly panelId: string; readonly workspaceId: string; readonly api: RatesApi }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const view = useQuery({ queryKey: ['rates', workspaceId], queryFn: () => api.view(workspaceId) });
  const market = useQuery({ queryKey: ['rates', 'market'], queryFn: () => api.market(), refetchInterval: 2000 });
  const products = useQuery({ queryKey: ['products', 'rates'], queryFn: () => api.products(), staleTime: Infinity });
  const grid = useRef<ExGridApi<LineRow>>(null);
  const [opened, setOpened] = useState<LineLayout>(() => openedByPanel.get(panelId) ?? FOLDED);
  useEffect(() => { openedByPanel.set(panelId, opened); }, [panelId, opened]);
  const [frame, setFrame] = useState<LiveFrame | undefined>(undefined);
  const [subscription, setSubscription] = useState<string | undefined>(undefined);
  const [detail, setDetail] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState<string | undefined>(undefined);

  const show = useCallback((next: RatesWorkspaceView) => queryClient.setQueryData(['rates', workspaceId], next), [queryClient, workspaceId]);

  const queue = useMemo(() => new EditQueue<Edit, RatesWorkspaceView>(0,
    async (revision, edit) => {
      switch (edit.kind) {
        case 'line': return api.editLine(workspaceId, revision, edit.line, edit.field, edit.value);
        case 'cashflow': return api.editCashflow(workspaceId, revision, edit.line, edit.period, edit.field, edit.value);
        case 'resolve': return api.resolve(workspaceId, revision, edit.held.scope, edit.held.entity, edit.held.field, edit.resolution);
        case 'add': return (await api.addLine(workspaceId, revision, edit.product)).workspace;
        case 'new': return (await api.newLine(workspaceId, revision, edit.product, edit.terms)).workspace;
        case 'defaults': return api.changeDefaults(workspaceId, revision, edit.defaults);
        case 'remove': return api.removeLine(workspaceId, revision, edit.line);
      }
    },
    (next) => { show(next); void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId, 'cashflows'] }); },
    (edit, error) => {
      workspace.notify('refused', `${notDone(edit)}: ${describe(error)}`);
      void api.view(workspaceId).then((fresh) => { queue.rebase(fresh.revision); show(fresh); });
    }), [api, workspaceId, show, workspace, queryClient]);
  // The queue follows the revision on screen: the one first read, and any read since that someone else moved
  // (the AI, another page), so the next edit is not refused for a change the user can already see.
  const shownRevision = view.data?.revision;
  useEffect(() => { if (shownRevision !== undefined) queue.shown(shownRevision); }, [shownRevision, queue]);

  // The rows live in a source keyed by line, so a tick replaces only the rows whose figures moved.
  const source = useMemo(() => GridSource.from<LineRow>([], { rowKey: (row) => row.line }), []);
  const rowsByLine = useRef<ReadonlyMap<string, LineRow>>(new Map());
  const [shownRows, setShownRows] = useState<ReadonlyMap<string, LineRow>>(new Map());
  useEffect(() => {
    if (view.data === undefined) return;
    const rows = lineRows(view.data.lines, view.data.revision, frame, rowsByLine.current);
    rowsByLine.current = new Map(rows.map((r) => [r.line, r]));
    setShownRows(rowsByLine.current);
    source.replaceAll(rows);
  }, [view.data, frame, source]);

  // The line the grid's Focus is on, by its key, so its result shows above the grid wherever the grid has scrolled.
  const [focusLine, setFocusLine] = useState<string | undefined>(undefined);
  const onSelectionChanged = useCallback((selection: GridSelection) => {
    if (selection.focus === undefined) { setFocusLine(undefined); return; }
    const state = source.snapshot();
    setFocusLine(state.window[selection.focus.row - state.windowStart]?.line);
  }, [source]);

  // Live (owner, 2026-10-27): the workspace's one subscription runs on the server; this page attaches to it
  // while it is open and only detaches when it goes. The server stops it once nobody has watched it for the
  // idle limit; Stop stops it for everyone. A stopped subscription is never resumed: Start makes a new one,
  // pricing the current revision from the next tick.
  const stream = useRef<FrameStream | undefined>(undefined);
  const detach = useCallback(() => {
    stream.current?.close();
    stream.current = undefined;
    setSubscription(undefined);
  }, []);
  const attach = useCallback((subscriptionId: string) => {
    if (stream.current !== undefined) return;
    setSubscription(subscriptionId);
    stream.current = api.frames(subscriptionId, setFrame, () => {
      detach();
      void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId] });
    });
  }, [api, detach, queryClient, workspaceId]);
  const stopLive = useCallback(async (id: string | undefined) => {
    detach();
    if (id !== undefined) await api.stopLive(id);
    void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId] });
  }, [api, detach, queryClient, workspaceId]);
  const startLive = useCallback(async () => {
    const { subscriptionId } = await api.startLive(workspaceId);
    attach(subscriptionId);
  }, [api, workspaceId, attach]);
  // On opening: attach to a running subscription, or show the last frame of a stopped one, marked as stopped.
  const liveStatus = view.data?.live;
  useEffect(() => {
    if (liveStatus === undefined) return;
    if (liveStatus.running) attach(liveStatus.subscriptionId);
    else if (liveStatus.lastFrame !== undefined) setFrame((shown) => shown ?? liveStatus.lastFrame);
  }, [liveStatus, attach]);
  useEffect(() => () => { stream.current?.close(); stream.current = undefined; }, []);

  const onEdit = useCallback((intent: GridEditIntent<LineRow>) => {
    const parse = parserOf(intent.column);
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    void queue.push({ kind: 'line', line: intent.row.line, field: intent.column, value: parsed.value });
  }, [queue]);

  // The trade's and each leg's Header Group draw ExGrid's toggle; the page holds what is open and answers with the
  // columns (ExGrid ADR-0400, ADR-0015 §2).
  const onGroupToggled = useCallback((toggle: HeaderGroupToggle) => setOpened((layout) => toggled(layout, toggle)), []);

  // The columns at the widths the user dragged them to, kept per panel; the line grid hides its column menu, so a header
  // needs no room for one (ADR-0015 §4).
  const { columns, onColumnWidthChanged } = useColumnWidths(panelId, 'lines', columnsOf(opened), { hideColumnMenu: true });

  // A double click on a folded column opens what it folds (ADR-0015 §1).
  const onCellDoubleClick = useCallback((cell: CellPosition) => {
    const column = columns[cell.column];
    if (column === undefined) throw new Error(`The grid reported a double click on column ${cell.column}, which it was not given.`);
    setOpened((layout) => openedAt(layout, column.name) ?? layout);
  }, [columns]);

  const onAction = useCallback((event: GridActionEvent<LineRow>) => {
    if (event.action === 'cashflows') setDetail(event.row.line);
    else throw new Error(`The grid reported an action it was not given: ${event.action}.`);
  }, []);

  // Removing a line is in its context menu, not a button beside Flows (ADR-0015 §5).
  const contextCommands = useCallback((context: GridContextMenuContext<LineRow>) => [{
    id: REMOVE_LINE, enabled: true, invoke: () => {
      setDetail((shown) => (shown === context.row.line ? undefined : shown));
      void queue.push({ kind: 'remove', line: context.row.line });
    },
  }], [queue]);

  const price = async () => {
    const snapshot = market.data?.snapshotId;
    if (snapshot === undefined) { workspace.notify('refused', 'No market snapshot has been published yet; Price waits for one.'); return; }
    setBusy('Pricing');
    try {
      // Price prices the edits already sent, once they have landed (spine 6).
      const key = crypto.randomUUID();
      const run = await queue.after((revision) => api.price(workspaceId, revision, key, snapshot));
      openRun(run.runId, `Rates ${snapshot.split(':').at(-1) ?? ''}`);
    } catch (error) {
      workspace.notify('refused', `Price was refused: ${describe(error)}${targetsOf(error)}`);
    } finally {
      setBusy(undefined);
    }
  };

  const capture = async () => {
    if (subscription === undefined || frame === undefined) { workspace.notify('refused', 'Capture needs a live frame on screen.'); return; }
    setBusy('Capturing');
    try {
      const run = await api.capture(subscription, frame, crypto.randomUUID());
      openRun(run.runId, `Capture ${frame.market.snapshotId.split(':').at(-1) ?? ''}`);
    } catch (error) {
      workspace.notify('refused', `Capture was refused: ${describe(error)}`);
    } finally {
      setBusy(undefined);
    }
  };

  const openRun = (runId: string, title: string) => {
    const resultView = workspace.catalog.views.find((v) => v.definitionId === RATES_RESULT_VIEW);
    workspace.store.getState().openPanel({ kind: 'run', title, runId, viewKey: resultView === undefined ? undefined : keyOf(resultView) });
    void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId] });
    void queryClient.invalidateQueries({ queryKey: ['history'] });
  };

  const changeValuationDate = async (date: string) => {
    setBusy('Moving the valuation date');
    try {
      const { workspace: next, carried } = await queue.after(async (revision) => {
        const answer = await api.changeValuationDate(workspaceId, revision, date);
        queue.rebase(answer.workspace.revision);
        return answer;
      });
      show(next);
      // Edits in conflict or lost wait for the user's decision: the notice that counts them does not leave by itself.
      workspace.notify('done', `Valuation date moved: ${carried.carried} cashflow edit(s) carried, ${carried.conflicted} in conflict, ${carried.lost} lost.`,
        { stays: carried.conflicted + carried.lost > 0 });
    } catch (error) {
      workspace.notify('refused', `The valuation date was not moved: ${describe(error)}`);
    } finally {
      setBusy(undefined);
    }
  };

  // The page's commands run the same functions as its buttons.
  const current = { price, startLive, stopLive: () => stopLive(subscription), live: subscription !== undefined, capture, queue, products: products.data ?? [] };
  const commands = useRef(current);
  commands.current = current;
  useEffect(() => {
    registerRatesCommands(panelId, {
      price: () => void commands.current.price(),
      startLive: () => {
        if (commands.current.live) workspace.notify('done', 'Live is running already.');
        else void commands.current.startLive();
      },
      stopLive: () => {
        if (!commands.current.live) workspace.notify('done', 'Live is not running.');
        else void commands.current.stopLive();
      },
      capture: () => void commands.current.capture(),
      addLine: (product) => void commands.current.queue.push({ kind: 'add', product }),
      newLine: (product, terms) => void commands.current.queue.push({ kind: 'new', product, terms }),
      setDefaults: (defaults) => {
        const shown = queryClient.getQueryData<RatesWorkspaceView>(['rates', workspaceId])?.defaults;
        if (shown === undefined) { workspace.notify('refused', 'The workspace has not loaded; its defaults were not set.'); return; }
        void commands.current.queue.push({ kind: 'defaults', defaults: { ...shown, ...defaults } });
      },
      products: () => commands.current.products,
    });
    registerGridFocus(panelId, () => grid.current?.focus());
    return () => { registerRatesCommands(panelId, undefined); registerGridFocus(panelId, undefined); };
  }, [panelId, workspace, queryClient, workspaceId]);

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const groups = groupsOf(opened, hiddenEdits(ws.lines));
  // The rows under a header of its tiers and the column headings, each a row high, so an open leg's extra tier takes no
  // row from view (ADR-0015 §2).
  const header = (headerTiers(opened) + 1) * 28;
  const height = 8 + header + Math.min(Math.max(ws.lines.length, 3) * 28, 356);

  return (
    <Stack gap="sm">
      <Group gap="sm" wrap="wrap" align="flex-end">
        <ValuationDate value={ws.valuationDate} disabled={busy !== undefined} onCommit={(d) => void changeValuationDate(d)} />
        <Button size="xs" variant="default" onClick={() => void queue.push({ kind: 'add', product: 'irs' })}>Add IRS</Button>
        <Button size="xs" variant="default" onClick={() => void queue.push({ kind: 'add', product: 'ccs' })}>Add CCS</Button>
        <Button size="xs" onClick={() => void price()} loading={busy === 'Pricing'} disabled={busy !== undefined && busy !== 'Pricing'}>Price</Button>
        <Button size="xs" color={subscription === undefined ? 'teal' : 'red'} variant="light" data-testid="live-toggle"
          onClick={() => void (subscription === undefined ? startLive() : stopLive(subscription))}>
          {subscription !== undefined ? 'Stop live' : ws.live !== undefined && !ws.live.running ? 'Resume live' : 'Start live'}
        </Button>
        <Button size="xs" variant="default" disabled={subscription === undefined || frame === undefined} loading={busy === 'Capturing'}
          onClick={() => void capture()}>Capture</Button>
      </Group>
      <Group gap="xs" wrap="wrap">
        <Badge variant="light" color="gray" data-testid="market">
          {market.data === undefined ? 'no market yet' : `market ${market.data.origin} #${market.data.sequence}`}
        </Badge>
        {frame !== undefined && (
          <Badge variant="light" color={frame.inputRevision < ws.revision ? 'yellow' : 'teal'} data-testid="live-frame">
            {subscription === undefined ? 'last frame' : 'live'} · r{frame.inputRevision} · #{frame.market.sequence} · {frame.calculationMode}
          </Badge>
        )}
        {subscription === undefined && ws.live !== undefined && !ws.live.running && (
          <Badge variant="light" color="gray" data-testid="live-stopped">
            {ws.live.stoppedBy === 'idle' ? `live stopped: nobody watched it for ${idleText(ws.live.idleLimit)}`
              : ws.live.stoppedFrom === 'ai' ? 'live stopped by the AI' : 'live stopped'}
            {ws.live.stoppedAt === undefined ? '' : ` at ${new Date(ws.live.stoppedAt).toLocaleTimeString()}`}
          </Badge>
        )}
        <Text size="xs" c="dimmed" data-testid="workspace-summary">{ws.lines.length} line(s) · revision {ws.revision}</Text>
      </Group>
      <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(held, resolution) => void queue.push({ kind: 'resolve', held, resolution })} />
      <ResultStrip row={(focusLine === undefined ? undefined : shownRows.get(focusLine)) ?? (shownRows.size === 1 ? [...shownRows.values()][0] : undefined)} />
      <div aria-label="Lines">
        <QubeGrid<LineRow> ref={grid} columns={columns} onColumnWidthChanged={onColumnWidthChanged} headerGroups={groups} source={source}
          onEdit={onEdit} onAction={onAction}
          cellState={lineCellState} cellMessage={lineCellMessage} density="compact" viewportWidth="stretch" viewportHeight={height}
          pinnedColumnCount={PINNED_COLUMNS} locale="en-US" onHeaderGroupToggled={onGroupToggled} onSelectionChanged={onSelectionChanged}
          hideColumnMenu onCellDoubleClick={onCellDoubleClick} contextCommands={contextCommands} commandLabel={commandLabel} />
      </div>
      {detail !== undefined && ws.lines.some((l) => l.line === detail) && (
        <Cashflows panelId={panelId} workspaceId={workspaceId} line={detail} revision={ws.revision} api={api} onClose={() => setDetail(undefined)}
          edit={(period, field, value) => void queue.push({ kind: 'cashflow', line: detail, period, field, value })} />
      )}
      {ws.runs.length > 0 && <Text size="xs" c="dimmed">Runs: {ws.runs.map((r) => `${r.runId.slice(-8)} (r${r.revision})`).join(', ')}</Text>}
    </Stack>
  );
}

/**
 * The focused line's result above the grid (owner, 2026-11-04): the result columns stand after the inputs, and are off
 * screen while a leg is open or the AI pane narrows the grid (ADR-0015), so what was calculated for the line in hand is
 * shown here whatever the grid scrolled to.
 */
function ResultStrip({ row }: { readonly row: LineRow | undefined }) {
  if (row === undefined) return <Text size="xs" c="dimmed" data-testid="line-result">Select a line to see its result here.</Text>;
  const r = lineResult(row);
  const color = r.status === 'priced' ? (r.stale ? 'yellow' : 'teal') : r.status === 'none' ? 'gray' : 'red';
  return (
    <Group gap="sm" wrap="wrap" data-testid="line-result" aria-live="polite" aria-label={`Result of ${r.line}`}>
      <Text size="sm" fw={600}>{r.line}</Text>
      <Badge variant="light" color={color} data-testid="line-result-status">
        {r.status === 'none' ? 'no figures yet' : r.status === 'notPriced' ? 'not priced' : r.status}{r.stale ? ' · older revision' : ''}
      </Badge>
      {r.pv !== undefined && <Text size="sm" data-testid="line-result-pv">PV {r.pv}</Text>}
      {r.par !== undefined && <Text size="sm" data-testid="line-result-par">{r.parLabel} {r.par}</Text>}
      {r.issues !== undefined && <Text size="xs" c="red" data-testid="line-result-issues">{r.issues}</Text>}
    </Group>
  );
}

/** A .NET TimeSpan (`hh:mm:ss`) as minutes or seconds. */
function idleText(span: string): string {
  const [h, m, s] = span.split(':').map(Number) as [number, number, number];
  const seconds = h * 3600 + m * 60 + s;
  return seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`;
}

const targetsOf = (error: unknown) =>
  error instanceof ApiError && error.envelope.issues.length > 0 ? ` (${error.envelope.issues.map((i) => i.path).join(', ')})` : '';

/** A line's cashflows: its generated periods with the user's edits on them, and its notional exchanges. */
function Cashflows({ panelId, workspaceId, line, revision, api, edit, onClose }: {
  readonly panelId: string;
  readonly workspaceId: string;
  readonly line: string;
  readonly revision: number;
  readonly api: RatesApi;
  readonly edit: (period: string, field: string, value: unknown) => void;
  readonly onClose: () => void;
}) {
  const flows = useQuery({ queryKey: ['rates', workspaceId, 'cashflows', line, revision], queryFn: () => api.cashflows(workspaceId, line) });
  const source = useMemo(() => GridSource.from<PeriodView>([], { rowKey: (p) => p.entity }), []);
  useEffect(() => { if (flows.data !== undefined) source.replaceAll(flows.data.periods); }, [flows.data, source]);
  const widths = useColumnWidths(panelId, 'cashflows', periodColumns);

  const onEdit = useCallback((intent: GridEditIntent<PeriodView>) => {
    const parse = periodParserOf(intent.column);
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    edit(intent.row.entity, intent.column, parsed.value);
  }, [edit]);

  const cellMessage = useCallback((p: PeriodView, c: GridColumn<PeriodView>) => (p.userFields.includes(c.name) ? 'Set by you; carried while this period exists.' : undefined), []);

  return (
    <Stack gap={6} data-testid="cashflows">
      <Group justify="space-between">
        <Text size="sm" fw={600}>Cashflows of {line}</Text>
        <Button size="compact-xs" variant="default" onClick={onClose}>Close</Button>
      </Group>
      {flows.isError && <Alert color="red" variant="light">{flows.error.message}</Alert>}
      {flows.data !== undefined && flows.data.periods.length === 0 && (
        <Text size="xs" c="dimmed">This line generates no schedule until its terms resolve; see its issues.</Text>
      )}
      <div aria-label={`Cashflows of ${line}`}>
        <QubeGrid<PeriodView> columns={widths.columns} onColumnWidthChanged={widths.onColumnWidthChanged} source={source} onEdit={onEdit}
          cellState={periodCellState} cellMessage={cellMessage}
          density="compact" viewportWidth="stretch" viewportHeight={Math.min(64 + (flows.data?.periods.length ?? 3) * 28, 320)} locale="en-US" />
      </div>
      {flows.data !== undefined && flows.data.exchanges.length > 0 && <Exchanges flows={flows.data} />}
    </Stack>
  );
}

function Exchanges({ flows }: { readonly flows: CashflowsView }) {
  const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
  return (
    <Table withTableBorder={false} fz="xs">
      <Table.Thead><Table.Tr><Table.Th>Leg</Table.Th><Table.Th>Exchange</Table.Th><Table.Th>Payment</Table.Th><Table.Th>Amount</Table.Th></Table.Tr></Table.Thead>
      <Table.Tbody>
        {flows.exchanges.map((e) => (
          <Table.Tr key={`${e.leg}-${e.kind}`}>
            <Table.Td>{e.leg}</Table.Td><Table.Td>{e.kind}</Table.Td><Table.Td>{e.paymentDate}</Table.Td>
            <Table.Td>{amount.format(e.amount)} {e.currency}</Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

function ValuationDate({ value, disabled, onCommit }: { readonly value: string; readonly disabled: boolean; readonly onCommit: (date: string) => void }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => { if (draft !== value && /^\d{4}-\d{2}-\d{2}$/.test(draft)) onCommit(draft); };
  return (
    <TextInput label="Valuation date" type="date" size="xs" w={150} value={draft} disabled={disabled}
      onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
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
    <Alert color="yellow" variant="light" title={`${conflicts.length} conflict(s), ${lost.length} lost cashflow edit(s)`} data-testid="held-edits">
      <Stack gap={4}>
        {conflicts.map((c) => (
          <Group key={`${c.entity}.${c.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{c.entity} {c.field}</Text>
            <Text size="xs">yours {value(c.value)}, schedule now {Object.entries(c.source ?? {}).map(([f, v]) => `${f} ${value(v)}`).join(', ')}</Text>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'keep')}>Keep mine</Button>
            <Button size="compact-xs" variant="default" onClick={() => resolve(c, 'takeSource')}>Take schedule</Button>
          </Group>
        ))}
        {lost.map((l) => (
          <Group key={`${l.entity}.${l.field}`} gap="xs" wrap="wrap">
            <Text size="xs" ff="monospace">{l.entity} {l.field}</Text>
            <Text size="xs">{value(l.value)}: the period is not in the new schedule{l.acknowledged ? ' (seen)' : ''}</Text>
            {!l.acknowledged && <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'acknowledge')}>Seen</Button>}
            <Button size="compact-xs" variant="default" onClick={() => resolve(l, 'reset')}>Discard</Button>
          </Group>
        ))}
      </Stack>
    </Alert>
  );
}

