import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import { Alert, Badge, Button, Group, Stack, Table, Text } from '@mantine/core';
import { GridSource, type CellPosition, type GridActionEvent, type GridColumn, type GridContextMenuContext, type GridEditIntent, type GridSelection, type HeaderGroupToggle } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { Screen } from '../../platform/commands/commands';
import { commandDoor, registerCommandDoor, tableMenu, type Told } from '../../platform/commands/served';
import { useColumnWidths } from '../../platform/grid/columnWidths';
import { GridUndoContext } from '../../platform/grid/gridUndo';
import { filterGrid, heldRowOf, heldView, menuView, placeFocus, selectRows, sortGrid } from '../../platform/grid/lineView';
import { GridFilterLine } from '../../platform/grid/GridFilterLine';
import { useHeldFilter } from '../../platform/grid/heldFilter';
import { EditQueue } from '../../platform/workspace/editQueue';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerPlaces, type Place } from '../../shell/places';
import { CommittedDateInput } from '../../platform/IsoDateInput';
import type { FrameStream, RatesApi } from './api';
import type { CashflowsView, HeldEdit, LiveFrame, PeriodView, RatesWorkspaceView, Resolution } from './contracts';
import {
  FOLDED, PINNED_COLUMNS, defaultsSaid, headerTiers, lineCellMessage, lineResult, lineCellState, lineColumns, hiddenEdits, lineHeaderGroups, lineRows, openedAt, parserOf, periodCellState,
  lineFieldTitle, periodColumns, periodFieldTitle, periodParserOf, toggled, type HiddenEdits, type LineLayout, type LineRow,
} from './lines';

// The fixed Rates line pricer (docs/07, C37 to C43): one line per trade in ExGrid, its terms sparse, its
// standard cashflows generated and customised on the server. Live frames tick the figures; Price and Capture
// make Runs. The server holds every rule; this page sends what the user changed and shows what came back. Its
// commands are the server's (ADR-0016 notes 34 to 36): a button runs the line a user types, through the page's door.

export const RATES_RESULT_VIEW = 'view.rates.result';

/** The application as the server names it, and the table its line grid is bound to (ADR-0016 note 34). */
const RATES_APP = 'rates';
const LINES_TABLE = 'rates.lines';
const lineKey = (row: LineRow) => row.line;
/** The two commands that make a Run, which the page opens from their answer (note 36). */
const PRICE = 'rates.workspace.price';
const CAPTURE = 'rates.frame.capture';

/** What the grids send as they are typed in. Everything else the page does is a command through its door. */
type Edit =
  | { readonly kind: 'line'; readonly line: string; readonly field: string; readonly value: unknown }
  | { readonly kind: 'cashflow'; readonly line: string; readonly period: string; readonly field: string; readonly value: unknown }
  | { readonly kind: 'resolve'; readonly held: HeldEdit; readonly resolution: Resolution };

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

/** A market snapshot as a Run's panel is titled by it: what follows its last colon. */
const snapshotTail = (snapshotId: string) => snapshotId.slice(snapshotId.lastIndexOf(':') + 1);

/** Puts the keyboard in a grid the page drew, or says that it is not drawn. */
function focused(grid: { readonly current: { focus(): void } | null }, title: string): string | undefined {
  if (grid.current === null) return `${title} are not drawn yet.`;
  grid.current.focus();
  return undefined;
}

export function RatesWorkspacePanel({ panelId, workspaceId, api }: { readonly panelId: string; readonly workspaceId: string; readonly api: RatesApi }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const view = useQuery({ queryKey: ['rates', workspaceId], queryFn: () => api.view(workspaceId) });
  const market = useQuery({ queryKey: ['rates', 'market'], queryFn: () => api.market(), refetchInterval: 2000 });
  const grid = useRef<ExGridApi<LineRow>>(null);
  const flowsGrid = useRef<ExGridApi<PeriodView>>(null);
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
  const source = useMemo(() => GridSource.from<LineRow>([], { rowKey: lineKey }), []);
  const rowsByLine = useRef<ReadonlyMap<string, LineRow>>(new Map());
  const [shownRows, setShownRows] = useState<ReadonlyMap<string, LineRow>>(new Map());
  useEffect(() => {
    if (view.data === undefined) return;
    const rows = lineRows(view.data.lines, view.data.revision, frame, rowsByLine.current);
    rowsByLine.current = new Map(rows.map((r) => [r.line, r]));
    setShownRows(rowsByLine.current);
  }, [view.data, frame]);
  // The grid shows every line, or, filtered from the line, those the server says its conditions leave (ADR-0016 note 41).
  const everyRow = useMemo(() => [...shownRows.values()], [shownRows]);
  const heldGrid = useMemo(() => ({ api: workspace.api, workspaceId, table: LINES_TABLE, revision: shownRevision }), [workspace, workspaceId, shownRevision]);
  const { rows: gridRows, said: filter } = useHeldFilter(source, heldGrid, everyRow, lineKey);
  useEffect(() => { source.replaceAll(gridRows); }, [gridRows, source]);
  const filterOff = useCallback(() => { filterGrid(source, {}); }, [source]);

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
  // pricing the current revision from the next tick. `live start` and `live stop` are the server's commands
  // (ADR-0016 note 36), from this page, another or the AI: the page follows what the workspace then says of its
  // subscription, whoever ran them.
  const stream = useRef<{ readonly subscriptionId: string; readonly frames: FrameStream } | undefined>(undefined);
  const detach = useCallback(() => {
    stream.current?.frames.close();
    stream.current = undefined;
    setSubscription(undefined);
  }, []);
  const attach = useCallback((subscriptionId: string) => {
    if (stream.current?.subscriptionId === subscriptionId) return;
    // The workspace says another subscription runs than the one this page hears: that one has ended.
    stream.current?.frames.close();
    setSubscription(subscriptionId);
    const frames = api.frames(subscriptionId, setFrame, () => {
      // The stream this page still hears ended; one it already left says nothing of the one it hears now.
      if (stream.current?.subscriptionId !== subscriptionId) return;
      detach();
      void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId] });
    });
    stream.current = { subscriptionId, frames };
  }, [api, detach, queryClient, workspaceId]);
  // Attach to a running subscription; leave one that stopped, and show its last frame, marked as stopped.
  const liveStatus = view.data?.live;
  useEffect(() => {
    if (liveStatus === undefined) return;
    if (liveStatus.running) { attach(liveStatus.subscriptionId); return; }
    detach();
    if (liveStatus.lastFrame !== undefined) setFrame((shown) => shown ?? liveStatus.lastFrame);
  }, [liveStatus, attach, detach]);
  useEffect(() => () => { stream.current?.frames.close(); stream.current = undefined; }, []);

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

  // The columns at the widths the user dragged them to, the user's for this grid (ADR-0017 §5); the line grid hides its column menu, so a header
  // needs no room for one (ADR-0015 §4).
  const { columns, onColumnWidthChanged } = useColumnWidths('rates/lines', columnsOf(opened), { hideColumnMenu: true });

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

  // A Run a command of this page made (`price`, `capture`) opens in a panel of its own.
  const openRun = useCallback((runId: string, title: string) => {
    const resultView = workspace.catalog.views.find((v) => v.definitionId === RATES_RESULT_VIEW);
    workspace.store.getState().openPanel({ kind: 'run', title, runId, viewKey: resultView === undefined ? undefined : keyOf(resultView) });
    void queryClient.invalidateQueries({ queryKey: ['history'] });
  }, [workspace, queryClient]);

  // What the command line is told is on screen (ADR-0016 §9, note 16): the line grid while the keyboard was last in
  // it. The cashflows are a grid of no table, and leave the line none.
  const screen = useRef<(() => Screen | undefined) | undefined>(undefined);
  /**
   * The page's door (ADR-0016 §5): the commands the server reads take their turn in the queue, after the edits already
   * sent (spine 6), and the workspace is read again in that same turn. What a command says of the workspace's live
   * subscription the page follows from that reading; a Run one made is opened from its answer.
   */
  const door = useMemo(() => commandDoor({
    workspaceId,
    app: RATES_APP,
    catalog: workspace.catalog,
    api: workspace.api,
    turns: queue,
    screen: () => screen.current?.(),
    reread: async () => {
      const fresh = await api.view(workspaceId);
      show(fresh);
      void queryClient.invalidateQueries({ queryKey: ['rates', workspaceId, 'cashflows'] });
      return fresh.revision;
    },
    notify: workspace.notify,
    answered: (capabilityId: string, answer: Told) => {
      if (capabilityId !== PRICE && capabilityId !== CAPTURE) return;
      const { runId, marketSnapshotId } = answer;
      if (typeof runId !== 'string' || typeof marketSnapshotId !== 'string') throw new Error(`${capabilityId} answered without its Run and the market it is of.`);
      openRun(runId, `${capabilityId === PRICE ? 'Rates' : 'Capture'} ${snapshotTail(marketSnapshotId)}`);
    },
  }), [api, workspaceId, workspace, queue, show, queryClient, openRun]);

  /** A button's line through the door, as a user types it (ADR-0016 §5): a refusal is a notice that begins with the line. */
  const enter = async (line: string) => {
    const ended = await door.enter(line);
    if (ended !== undefined && 'sentence' in ended) workspace.notify('refused', `${line}: ${ended.sentence}`);
  };
  /** Price prices on the market this page shows, named, as Capture names the frame it drew (docs/07 §3); typed, `price` says the market it read. */
  const price = async () => {
    const snapshot = market.data?.snapshotId;
    if (snapshot === undefined) { workspace.notify('refused', 'price: No market snapshot has been published yet; Price waits for one.'); return; }
    setBusy('Pricing');
    try {
      const reason = await door.invoke(PRICE, { marketSnapshotId: snapshot });
      if (reason !== undefined) workspace.notify('refused', `price: ${reason.sentence}`);
    } finally {
      setBusy(undefined);
    }
  };
  /** Capture saves the frame this page drew, named whole, whatever the subscription shows by the time it runs (docs/07 §3). */
  const capture = async (drawn: LiveFrame) => {
    setBusy('Capturing');
    try {
      const reason = await door.invoke(CAPTURE, {
        subscriptionId: drawn.subscriptionId, subscriptionRevision: drawn.subscriptionRevision, inputRevision: drawn.inputRevision, marketSnapshotId: drawn.market.snapshotId,
      });
      if (reason !== undefined) workspace.notify('refused', `capture: ${reason.sentence}`);
    } finally {
      setBusy(undefined);
    }
  };
  /** The grids' undo and redo keys (ADR-0016 §11): the notice says what was taken back or put back, or why nothing was. */
  const takeBack = async (verb: 'undo' | 'redo') => {
    const reason = await door.invoke(`workspace.${verb}`, {});
    if (reason !== undefined) workspace.notify('refused', reason.sentence);
  };
  const takeBackNow = useRef(takeBack);
  takeBackNow.current = takeBack;
  // The same two functions for the page's life, so no grid is made again for them.
  const gridUndo = useMemo(() => ({ undo: () => void takeBackNow.current('undo'), redo: () => void takeBackNow.current('redo') }), []);

  // The lines' actions (ADR-0016 §4, note 25): read once for the workspace, and run through the door. Removing a line
  // is one of them, in the Context Menu and not a button beside Flows (ADR-0015 §5).
  const tables = useQuery({ queryKey: ['workspace-tables', workspaceId], queryFn: () => workspace.api.tables(workspaceId), staleTime: Infinity });
  const menu = useMemo(() => {
    // Not read yet, the table has no actions to offer; read, the server lists the table this page's grid is bound to.
    const lines = tables.data?.find((t) => t.tableId === LINES_TABLE);
    if (tables.data !== undefined && lines === undefined) throw new Error(`The server lists no table ${LINES_TABLE}, which the Rates page's line grid is bound to.`);
    return tableMenu(lines, door, workspace.notify);
  }, [tables.data, door, workspace]);
  const menuNow = useRef(menu);
  menuNow.current = menu;
  const tablesUnread = tables.isError ? describe(tables.error) : undefined;
  useEffect(() => {
    if (tablesUnread !== undefined) workspace.notify('refused', `The grid's menu holds no command of its table: ${tablesUnread}`);
  }, [tablesUnread, workspace]);
  // The line grid as the line is told of it (note 16): the page holds every line, so its rows are named by their keys,
  // and its columns by their names, which are the fields' where a column is one.
  const columnsNow = useRef(columns);
  columnsNow.current = columns;
  // The grid sorts by none of them: a term is shown as typed (`10y`, `5mm`), which the grid would order as text.
  const declared = useCallback(() => ({
    table: LINES_TABLE, columns: columnsNow.current.map((c) => c.name), unsorted: columnsNow.current.filter((c) => c.queryable === false).map((c) => c.name),
  }), []);
  const contextCommands = useCallback((context: GridContextMenuContext<LineRow>) =>
    [...menuNow.current.commands(menuView((read) => heldView(declared(), read, source.snapshot(), lineKey), context, context.row.line))], [declared, source]);
  const commandLabel = useCallback((id: string) => menuNow.current.label(id), []);
  // A place read for that view (note 21) is the row of its key among the rows the grid then held, and so are the rows
  // a line selects (note 43): whether they are next to one another is said of the order the grid held them in.
  const lineScreen = useCallback((): Screen | undefined => {
    const shown = grid.current;
    if (shown === null) return undefined;
    const named = declared();
    const read = shown.readView();
    const held = source.snapshot();
    const rowOf = heldRowOf(held, lineKey);
    return {
      view: heldView(named, read, held, lineKey),
      put: (place) => placeFocus(named, shown, read, place, rowOf) ?? focused(grid, 'The lines'),
      // Which lines conditions leave is the server's to say (note 41); a sort is refused by the column's name (note 40).
      sort: (asked) => sortGrid(named, source, asked),
      filter: (asked) => filterGrid(source, asked),
      select: (asked) => selectRows(named, shown, read, asked, rowOf),
    };
  }, [declared, source]);

  // The page's places (ADR-0016 §9, note 20): its lines, and the cashflows of a line while they are open.
  const flowsOf = detail !== undefined && view.data?.lines.some((l) => l.line === detail) === true ? detail : undefined;
  const places = (): readonly Place[] => {
    // While the workspace is being read, or could not be, the page draws no part to go to.
    if (view.isPending || view.isError) return [];
    return [
      { word: 'lines', title: 'the lines', grid: true, go: () => focused(grid, 'The lines') },
      ...(flowsOf === undefined ? [] : [{ word: 'flows', title: `the cashflows of ${flowsOf}`, grid: true, go: () => focused(flowsGrid, 'The cashflows') }]),
    ];
  };
  const placesNow = useRef(places);
  placesNow.current = places;
  useEffect(() => {
    registerPlaces(panelId, () => placesNow.current());
    registerCommandDoor(panelId, door);
    return () => { registerPlaces(panelId, undefined); registerCommandDoor(panelId, undefined); };
  }, [panelId, door]);

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

  if (view.isPending) return <Text c="dimmed">Loading workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Workspace">{view.error.message}</Alert>;
  const ws = view.data;
  const defaults = defaultsSaid(ws.defaults);
  const groups = groupsOf(opened, hiddenEdits(ws.lines));
  // The rows under a header of its tiers and the column headings, each a row high, so an open leg's extra tier takes no
  // row from view (ADR-0015 §2).
  const header = (headerTiers(opened) + 1) * 28;
  const height = 8 + header + Math.min(Math.max(ws.lines.length, 3) * 28, 356);

  const page = (
    // A grid entered is the screen only where it says so below, after this: a grid of no table leaves the line none.
    <Stack gap="sm" onFocusCapture={(event) => { if (event.target instanceof Element && event.target.closest('.ex-grid') !== null) screen.current = undefined; }}>
      <Group gap="sm" wrap="wrap" align="flex-end">
        <CommittedDateInput label="Valuation date" what="valuation date" size="xs" w={150} value={ws.valuationDate} disabled={busy !== undefined}
          onCommit={(d) => void changeValuationDate(d)} onRefused={(sentence) => workspace.notify('refused', sentence)} />
        {/* Each button runs the line a user types (ADR-0016 note 36), so the two cannot differ. */}
        <Button size="xs" variant="default" onClick={() => void enter('add line irs')}>Add IRS</Button>
        <Button size="xs" variant="default" onClick={() => void enter('add line ccs')}>Add CCS</Button>
        <Button size="xs" onClick={() => void price()} loading={busy === 'Pricing'} disabled={busy !== undefined && busy !== 'Pricing'}>Price</Button>
        <Button size="xs" color={subscription === undefined ? 'teal' : 'red'} variant="light" data-testid="live-toggle"
          onClick={() => void enter(subscription === undefined ? 'live start' : 'live stop')}>
          {subscription !== undefined ? 'Stop live' : ws.live !== undefined && !ws.live.running ? 'Resume live' : 'Start live'}
        </Button>
        <Button size="xs" variant="default" disabled={subscription === undefined || frame === undefined} loading={busy === 'Capturing'}
          title={subscription === undefined ? 'Capture saves a live frame: start live first.' : frame === undefined ? 'No live frame has arrived yet.' : undefined}
          onClick={() => { if (frame !== undefined) void capture(frame); }}>Capture</Button>
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
        {defaults !== undefined && <Text size="xs" c="dimmed" data-testid="line-defaults">{defaults}</Text>}
      </Group>
      <HeldEdits conflicts={ws.conflicts} lost={ws.lost} resolve={(held, resolution) => void queue.push({ kind: 'resolve', held, resolution })} />
      <ResultStrip row={(focusLine === undefined ? undefined : shownRows.get(focusLine)) ?? (shownRows.size === 1 ? [...shownRows.values()][0] : undefined)} />
      <GridFilterLine said={filter} grid="the lines" onOff={filterOff} />
      <div aria-label="Lines" onFocusCapture={() => { screen.current = lineScreen; }}>
        <QubeGrid<LineRow> ref={grid} columns={columns} onColumnWidthChanged={onColumnWidthChanged} headerGroups={groups} source={source}
          onEdit={onEdit} onAction={onAction}
          cellState={lineCellState} cellMessage={lineCellMessage} density="compact" viewportWidth="stretch" viewportHeight={height}
          pinnedColumnCount={PINNED_COLUMNS} locale="en-US" onHeaderGroupToggled={onGroupToggled} onSelectionChanged={onSelectionChanged}
          hideColumnMenu onCellDoubleClick={onCellDoubleClick} contextCommands={contextCommands} commandLabel={commandLabel} />
      </div>
      {detail !== undefined && ws.lines.some((l) => l.line === detail) && (
        <Cashflows panelId={panelId} workspaceId={workspaceId} line={detail} revision={ws.revision} api={api} gridRef={flowsGrid} onClose={() => setDetail(undefined)}
          edit={(period, field, value) => void queue.push({ kind: 'cashflow', line: detail, period, field, value })} />
      )}
      {ws.runs.length > 0 && <Text size="xs" c="dimmed">Runs: {ws.runs.map((r) => `${r.runId.slice(-8)} (r${r.revision})`).join(', ')}</Text>}
    </Stack>
  );
  return <GridUndoContext.Provider value={gridUndo}>{page}</GridUndoContext.Provider>;
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

/** A line's cashflows: its generated periods with the user's edits on them, and its notional exchanges. */
function Cashflows({ panelId, workspaceId, line, revision, api, gridRef, edit, onClose }: {
  readonly panelId: string;
  readonly workspaceId: string;
  readonly line: string;
  readonly revision: number;
  readonly api: RatesApi;
  /** The grid's handle, by which the page puts the keyboard in it (`go flows`). */
  readonly gridRef: RefObject<ExGridApi<PeriodView> | null>;
  readonly edit: (period: string, field: string, value: unknown) => void;
  readonly onClose: () => void;
}) {
  const flows = useQuery({ queryKey: ['rates', workspaceId, 'cashflows', line, revision], queryFn: () => api.cashflows(workspaceId, line) });
  const source = useMemo(() => GridSource.from<PeriodView>([], { rowKey: (p) => p.entity }), []);
  useEffect(() => { if (flows.data !== undefined) source.replaceAll(flows.data.periods); }, [flows.data, source]);
  const widths = useColumnWidths('rates/cashflows', periodColumns);

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
        <QubeGrid<PeriodView> ref={gridRef} columns={widths.columns} onColumnWidthChanged={widths.onColumnWidthChanged} source={source} onEdit={onEdit}
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

