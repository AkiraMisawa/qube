import { useCallback, useEffect, useMemo, useRef, type RefObject } from 'react';
import { Alert, Badge, Code, Group, SegmentedControl, Select, Stack, Text, Title } from '@mantine/core';
import { GridSource, type GridColumn, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { commandDoor, registerCommandDoor } from '../../platform/commands/served';
import { formatAmount, formatMillions, toneOf } from '../../platform/grid/amounts';
import { useColumnWidths } from '../../platform/grid/columnWidths';
import { GridUndoContext } from '../../platform/grid/gridUndo';
import { keysInOrder, useKeptGrid } from '../../platform/grid/keptGrid';
import { compactGridHeight } from '../../platform/grid/metrics';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import { SM, usePanelWidth } from '../../platform/panelPage';
import { panelKey, usePanelState } from '../../platform/panelState';
import { EditQueue } from '../../platform/workspace/editQueue';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { registerPlaces, type Place } from '../../shell/places';
import type { PkApi } from './api';
import type { PkPosition, PkView, Mark, PositionParallel, PositionRisk } from './contracts';
import { hedgeRows, marksSaid, metricsOf, moveRows, riskInputOf, riskInputs, riskKey, tenorOf, xvaRows, type HedgeRow, type MoveRow, type XvaRow } from './rows';
import './pk.css';

// The Position Keeper (ADR-0019): the book an engine computed, read from its result set, with what-if hedges and moves the user
// holds beside it. The server holds every figure and every mark (`WHAT-IF`, `SIMULATED`, `FIRST ORDER`); this page
// shows what it answered and sends what the user typed. A hedge is made, changed and removed from the command line
// (`new irs`, `set hedge`, `remove hedge`), through the page's door; a move is typed in its cell, which runs `set`.

/** The application as the server names it (ADR-0016 note 27). */
const PK_APP = 'pk';

/** What the query of a Position Keeper workspace is kept under: every command that moves the workspace reads it again. */
export const pkQueryKey = (workspaceId: string) => ['pk', workspaceId] as const;

/** A Position Keeper panel's title: the valuation date of the book it shows. */
export const pkPanelTitle = (of: { readonly valuationDate: string }) => `PK ${of.valuationDate}`;

/** What the page's amounts are written in: whole units, or millions with two decimals. Rates, moves and terms are not amounts. */
type Scale = 'units' | 'millions';
const SCALE = panelKey<Scale>('pk.scale');
const written = (scale: Scale) => (scale === 'millions' ? formatMillions : formatAmount);
const text = (value: unknown) => String(value);

/** The page's columns, written in the scale chosen. */
function columnsIn(scale: Scale, metrics: readonly string[]) {
  const format = written(scale);
  // A sensitivity, a hedge's value and a profit and loss change sign with the position: a negative one is painted as a loss.
  const figure = { type: 'number' as const, format, tone: toneOf(format), width: 150 };
  // An adjustment is a cost, negative for every counterparty: painted red it would say nothing, so it takes no tone
  // and red stays for the figures whose sign is news. A standard error is a size, and takes none either.
  const plain = { type: 'number' as const, format };

  const xva: GridColumn<XvaRow>[] = [
    { name: 'counterparty', header: 'Counterparty', type: 'text', width: 110, value: (c) => c.counterparty, format: text },
    { name: 'legalEntity', header: 'Legal entity', type: 'text', width: 120, value: (c) => c.legalEntity, format: text },
    ...metrics.flatMap((metric): GridColumn<XvaRow>[] => [
      { name: metric, header: metric, ...plain, width: 130, value: (c) => c.byMetric[metric]?.value },
      { name: `${metric}.stdError`, header: `± ${metric}`, ...plain, width: 100, value: (c) => c.byMetric[metric]?.stdError },
    ]),
    { name: 'total', header: 'Total', ...plain, width: 130, value: (c) => c.total },
  ];

  const term = (name: string, header: string, width: number): GridColumn<HedgeRow> =>
    ({ name, header, type: 'text', width, queryable: false, value: (h) => h.terms[name], format: text });
  const hedges: GridColumn<HedgeRow>[] = [
    { name: 'hedge', header: 'Hedge', type: 'text', width: 70, value: (h) => h.hedge, format: text },
    term('product', 'Product', 80), term('currency', 'Currency', 84), term('direction', 'Direction', 90), term('notional', 'Notional', 90),
    term('end', 'End', 80), term('rate', 'Rate', 70),
    { name: 'value', header: 'Value', ...figure, value: (h) => h.value },
    { name: 'provider', header: 'Risk computed by', type: 'text', width: 200, value: (h) => h.provider, format: text },
    { name: 'marks', header: 'Marks', type: 'text', width: 190, value: (h) => marksSaid(h.marks), format: text },
  ];

  const parallel: GridColumn<PositionParallel>[] = [
    { name: 'kind', header: 'Kind', type: 'text', width: 120, value: (r) => r.kind, format: text },
    { name: 'input', header: 'Input', type: 'text', width: 130, value: (r) => r.input, format: text },
    { name: 'step', header: 'Per', type: 'text', width: 64, value: (r) => r.step, format: text },
    { name: 'book', header: 'Book', ...figure, value: (r) => r.book },
    { name: 'whatIf', header: 'What-if', ...figure, value: (r) => r.whatIf },
    { name: 'net', header: 'Net', ...figure, value: (r) => r.net },
    { name: 'marks', header: 'Marks', type: 'text', width: 190, value: (r) => marksSaid(r.marks), format: text },
  ];

  // A line of a kind with a declared unit is written per step (a basis point, a unit of the quote). One without is
  // written as the engine wrote it, and its Per says so: no step is assumed for it.
  const risk: GridColumn<PositionRisk>[] = [
    { name: 'kind', header: 'Kind', type: 'text', width: 120, value: (r) => r.kind, format: text },
    { name: 'input', header: 'Input', type: 'text', width: 130, value: (r) => r.input, format: text },
    { name: 'tenor', header: 'Tenor', type: 'text', width: 80, value: tenorOf, format: text },
    { name: 'step', header: 'Per', type: 'text', width: 96, value: (r) => r.step ?? AS_WRITTEN, format: text },
    { name: 'book', header: 'Book', ...figure, value: (r) => (r.step === undefined ? r.book : r.bookStep) },
    { name: 'whatIf', header: 'What-if', ...figure, value: (r) => (r.step === undefined ? r.whatIf : r.whatIfStep) },
    { name: 'net', header: 'Net', ...figure, value: (r) => (r.step === undefined ? r.net : r.netStep) },
    { name: 'marks', header: 'Marks', type: 'text', width: 190, value: (r) => marksSaid(r.marks), format: text },
  ];

  const moves: GridColumn<MoveRow>[] = [
    { name: 'kind', header: 'Kind', type: 'text', width: 120, value: (m) => m.kind, format: text },
    { name: 'input', header: 'Input', type: 'text', width: 130, value: (m) => m.input, format: text },
    { name: 'typedIn', header: 'Plain number in', type: 'text', width: 124, value: (m) => m.typedIn, format: text },
    // The move as typed, which the server reads: the grid orders none of it.
    { name: MOVE, header: 'Move', type: 'text', width: 84, editable: true, queryable: false, value: (m) => m.move, format: text },
    { name: 'book', header: 'Book P&L', ...figure, value: (m) => m.pnl?.book },
    { name: 'whatIf', header: 'What-if P&L', ...figure, value: (m) => m.pnl?.whatIf },
    { name: 'net', header: 'Net P&L', ...figure, value: (m) => m.pnl?.net },
    { name: 'marks', header: 'Marks', type: 'text', width: 250, value: (m) => (m.pnl === undefined ? undefined : marksSaid(m.pnl.marks)), format: text },
  ];
  return { xva, hedges, parallel, risk, moves };
}

/** What a risk line's Per says where the result set declares no unit for its kind: the figure is the engine's, unscaled. */
const AS_WRITTEN = 'as written';

/** The column a move is typed in: the one cell of the page that is written. */
const MOVE = 'move';

/** A curve's ladder is seen whole: the risk grid grows to this many rows before it scrolls. */
const RISK_ROWS = 25;
const MOVES_ROWS = 12;
const PARALLEL_ROWS = 10;
const VALUES_ROWS = 12;

/** The risk lines shown, kept by the panel while it is hidden (ADR-0006 note 10): one kind and input, or every line. */
const RISK_OF = panelKey<string | null>('pk.riskOf');

/** A figure outside a grid, written in the page's scale; a loss in red where the figure's sign is news (`toned`). */
function Amount({ label, value, scale, toned = true }: { readonly label: string; readonly value: number; readonly scale: Scale; readonly toned?: boolean }) {
  const shown = written(scale)(value);
  return (
    <Text size="sm" span>
      {label} <span className="qube-number" {...(toned && shown.startsWith('-') ? { 'data-negative': true } : {})}>{shown}</span>
    </Text>
  );
}

/** What each mark says of a figure, for a pointer and a screen reader. */
const MARK_SAYS: Readonly<Record<Mark, string>> = {
  'WHAT-IF': 'A hypothetical hedge is in this figure: it is not a position.',
  SIMULATED: 'A figure in this is not the engine\'s: the what-if hedges\' risk comes from a simulated provider.',
  'FIRST ORDER': 'Predicted from the sensitivities times the moves, to first order: not a revaluation.',
};

function Marks({ marks }: { readonly marks: readonly Mark[] }) {
  return <>{marks.map((mark) => <Badge key={mark} variant="light" color="yellow" title={MARK_SAYS[mark]}>{mark}</Badge>)}</>;
}

/** The marks of the predicted total. A total answered without them is a defect said as one: it is never drawn as unmarked, which would read as the engine's own figure. */
function totalMarks(p: PkPosition): readonly Mark[] {
  if (p.pnlTotalMarks === undefined) throw new Error('The position answered a predicted total without its marks.');
  return p.pnlTotalMarks;
}

/** One grid of the page under its label: read-only unless it is given `onEdit`. */
function PkGrid<Row>({ name, label, rows, rowKey, columns, most, pinned, gridRef, onEdit }: {
  /** The grid's name among the user's widths and what the panel keeps (`pk/values`). */
  readonly name: string;
  readonly label: string;
  readonly rows: readonly Row[];
  readonly rowKey: (row: Row) => string;
  readonly columns: readonly GridColumn<Row>[];
  /** The rows the grid grows to before it scrolls. */
  readonly most: number;
  readonly pinned: number;
  readonly gridRef: RefObject<ExGridApi<Row> | null>;
  readonly onEdit?: (intent: GridEditIntent<Row>) => Promise<void>;
}) {
  const source = useMemo(() => GridSource.from<Row>([], { rowKey }), [rowKey]);
  useEffect(() => { source.replaceAll(rows); }, [source, rows]);
  const widths = useColumnWidths(name, columns);
  // On a phone's width pinned columns would leave the figures no room to scroll into: none is pinned there.
  const narrow = usePanelWidth() < SM;
  useKeptGrid(name, source, gridRef, widths.columns, keysInOrder(rowKey));
  return (
    <div aria-label={label}>
      <QubeGrid<Row> ref={gridRef} columns={widths.columns} onColumnWidthChanged={widths.onColumnWidthChanged} source={source}
        {...(onEdit === undefined ? {} : { onEdit })}
        density="compact" viewportWidth="stretch" viewportHeight={compactGridHeight(Math.max(1, Math.min(rows.length, most)))} pinnedColumnCount={narrow ? 0 : pinned} locale="en-US" />
    </div>
  );
}

const xvaKey = (c: XvaRow) => c.counterparty;
const parallelKey = (l: PositionParallel) => `${l.kind}/${l.input}`;
const hedgeKey = (h: HedgeRow) => h.hedge;
const moveKey = (m: MoveRow) => m.key;

export function PkWorkspacePanel({ panelId, workspaceId, api }: { readonly panelId: string; readonly workspaceId: string; readonly api: PkApi }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const view = useQuery({ queryKey: pkQueryKey(workspaceId), queryFn: () => api.view(workspaceId) });
  const show = useCallback((next: PkView) => queryClient.setQueryData(pkQueryKey(workspaceId), next), [queryClient, workspaceId]);

  // The page sends no edit of its own kind: every write is a command through its door, which takes its turn here.
  const turns = useMemo(() => new EditQueue<never, { readonly revision: number }>(0,
    () => { throw new Error('The Position Keeper page sends no edit but through its door.'); }, () => undefined, () => undefined), []);
  const shownRevision = view.data?.position.revision;
  useEffect(() => { if (shownRevision !== undefined) turns.shown(shownRevision); }, [shownRevision, turns]);

  const door = useMemo(() => commandDoor({
    workspaceId,
    app: PK_APP,
    catalog: workspace.catalog,
    api: workspace.api,
    turns,
    reread: async () => {
      const fresh = await api.view(workspaceId);
      show(fresh);
      return fresh.position.revision;
    },
    notify: workspace.notify,
  }), [api, workspaceId, workspace, turns, show]);

  // The grids' undo and redo keys (ADR-0016 §11) run the workspace's two commands.
  const gridUndo = useMemo(() => {
    const takeBack = async (verb: 'undo' | 'redo') => {
      const reason = await door.invoke(`workspace.${verb}`, {});
      if (reason !== undefined) workspace.notify('refused', reason.sentence);
    };
    return { undo: () => void takeBack('undo'), redo: () => void takeBack('redo') };
  }, [door, workspace]);

  const valuesGrid = useRef<ExGridApi<XvaRow>>(null);
  const parallelGrid = useRef<ExGridApi<PositionParallel>>(null);
  const hedgesGrid = useRef<ExGridApi<HedgeRow>>(null);
  const riskGrid = useRef<ExGridApi<PositionRisk>>(null);
  const movesGrid = useRef<ExGridApi<MoveRow>>(null);

  const [riskOf, setRiskOf] = usePanelState(RISK_OF, null);
  const [scale, setScale] = usePanelState(SCALE, 'units');
  const position = view.data?.position;
  const metrics = useMemo(() => (position === undefined ? [] : metricsOf(position)), [position]);
  const columns = useMemo(() => columnsIn(scale, metrics), [scale, metrics]);
  const xva = useMemo(() => (position === undefined ? [] : xvaRows(position)), [position]);
  const hedges = useMemo(() => (view.data === undefined ? [] : hedgeRows(view.data)), [view.data]);
  const moves = useMemo(() => (view.data === undefined ? [] : moveRows(view.data)), [view.data]);
  const inputs = useMemo(() => riskInputs(position?.risk ?? []), [position]);
  // An input kept from before that this book has no risk on narrows to nothing: every line is shown instead, and the control says so.
  const narrowed = riskOf !== null && inputs.includes(riskOf) ? riskOf : null;
  const risk = useMemo(() => (position?.risk ?? []).filter((line) => narrowed === null || riskInputOf(line) === narrowed), [position, narrowed]);
  const parallel = useMemo(() => (position?.parallel ?? []).filter((line) => narrowed === null || riskInputOf(line) === narrowed), [position, narrowed]);

  // The page's places (ADR-0016 §9): each grid it draws, by one word.
  const places = (): readonly Place[] => {
    if (view.data === undefined) return [];
    const grid = (word: string, title: string, ref: RefObject<{ focus(): void } | null>): Place => ({
      word, title, grid: true,
      go: () => { if (ref.current === null) return `${title} is not drawn.`; ref.current.focus(); return undefined; },
    });
    return [
      grid('values', 'the values', valuesGrid),
      ...(hedges.length === 0 ? [] : [grid('hedges', 'the what-if hedges', hedgesGrid)]),
      grid('parallel', 'the parallel risk', parallelGrid),
      grid('risk', 'the risk', riskGrid),
      grid('moves', 'the moves', movesGrid),
    ];
  };
  const placesNow = useRef(places);
  placesNow.current = places;
  useEffect(() => {
    registerPlaces(panelId, () => placesNow.current());
    registerCommandDoor(panelId, door);
    return () => { registerPlaces(panelId, undefined); registerCommandDoor(panelId, undefined); };
  }, [panelId, door]);

  // A move typed in its cell is the line `set move <row> move <typed>`, and emptied, `reset`: the server reads what
  // was typed, and its refusal is said at the cell, with nothing written.
  const onMoveEdit = useCallback(async (intent: GridEditIntent<MoveRow>) => {
    if (intent.column !== MOVE) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const typed = intent.value.trim();
    const rows = `move ${intent.row.key}`;
    const reason = typed === '' ? await door.invoke('entity.reset', { rows, field: MOVE }) : await door.invoke('entity.set', { rows, set: `${MOVE} ${typed}` });
    if (reason !== undefined && !intent.signal.aborted) intent.refuse(reason.sentence);
  }, [door]);

  if (view.isPending) return <Text c="dimmed">Loading the Position Keeper workspace…</Text>;
  if (view.isError) return <Alert color="red" title="Position Keeper workspace">{view.error.message}</Alert>;
  const p: PkPosition = view.data.position;

  return (
    <GridUndoContext.Provider value={gridUndo}>
      <Stack gap="sm" className="qube-pk" data-testid="pk" data-workspace={workspaceId} data-revision={p.revision}>
        <Group gap="xs" wrap="wrap" data-testid="pk-identity" aria-label="The book shown">
          <Badge variant="light" color="gray">engine {p.engine}</Badge>
          <Badge variant="light" color="gray" title={p.engineCommit}>commit {p.engineCommit.slice(0, 7)}</Badge>
          <Badge variant="light" color="gray">valuation date {p.valuationDate}</Badge>
          <Badge variant="light" color="gray">result set {p.resultSet}</Badge>
          <Text size="xs" c="dimmed">input set: {p.inputSet}</Text>
          <SegmentedControl size="xs" aria-label="Amounts in" value={scale} onChange={(next) => setScale(next as Scale)}
            data={[{ value: 'units', label: p.currency ?? 'Units' }, { value: 'millions', label: p.currency === undefined ? 'Millions' : `${p.currency} mm` }]} />
        </Group>

        <section>
          <Title order={3} size="sm">XVA by counterparty</Title>
          <Group gap="md" wrap="wrap" data-testid="pk-totals">
            <Text size="xs" c="dimmed">Total of {p.counterparties.length} counterparties (no standard error: they share paths)</Text>
            {p.totals.map((total) => <Amount key={total.metric} label={total.metric} value={total.value} scale={scale} toned={false} />)}
          </Group>
          <PkGrid name="pk/xva" label="XVA by counterparty" rows={xva} rowKey={xvaKey} columns={columns.xva} most={VALUES_ROWS} pinned={1} gridRef={valuesGrid} />
        </section>

        <section>
          <Title order={3} size="sm">What-if hedges</Title>
          {/* Not dimmed: the lines to type are read, and code on a dimmed line falls below 4.5:1 (docs/design-guidelines.md §7). */}
          <Text size="xs">
            Hypothetical, never a position. On the command line: <Code>new irs 5y jpy 1bn pay 1.2%</Code>, <Code>set hedge H1 notional 2bn</Code>, <Code>remove hedge H1</Code>.
          </Text>
          {hedges.length === 0
            ? <Text size="sm" data-testid="pk-no-hedges">No what-if hedge: every figure below is the engine&apos;s.</Text>
            : <PkGrid name="pk/hedges" label="What-if hedges" rows={hedges} rowKey={hedgeKey} columns={columns.hedges} most={8} pinned={1} gridRef={hedgesGrid} />}
        </section>

        <section>
          <Group gap="sm" wrap="wrap" align="flex-end">
            <Title order={3} size="sm">Risk</Title>
            {/* 240px: the longest kind and input of a book (`zero_rate USD-XCCY-JPY`) with the control's two marks. */}
            <Select size="xs" w={240} label="Risk of" searchable clearable placeholder="Every kind and input" value={narrowed} onChange={setRiskOf} data={[...inputs]} />
            <Text size="xs" c="dimmed">First-order sensitivities of the adjustments, summed over counterparties and metrics: per basis point of a rate, per unit of a quote.</Text>
          </Group>
          <Text size="xs" c="dimmed">Each input moved by one step at every tenor:</Text>
          <PkGrid name="pk/parallel" label="Parallel risk" rows={parallel} rowKey={parallelKey} columns={columns.parallel} most={PARALLEL_ROWS} pinned={2} gridRef={parallelGrid} />
          <Text size="xs" c="dimmed">By tenor:</Text>
          <PkGrid name="pk/risk" label="Risk" rows={risk} rowKey={riskKey} columns={columns.risk} most={RISK_ROWS} pinned={3} gridRef={riskGrid} />
        </section>

        <section>
          <Title order={3} size="sm">Moves and predicted P&amp;L</Title>
          <Group gap="md" wrap="wrap" data-testid="pk-pnl-total" aria-live="polite">
            {p.pnlTotalBook === undefined || p.pnlTotalNet === undefined
              ? <Text size="sm">No move is held: type one under Move. A plain number is absolute, in basis points for a rate and in the quote&apos;s own units otherwise; a number with % is relative, of the input&apos;s level.</Text>
              : (
                <>
                  <Text size="sm" fw={600}>Predicted P&amp;L</Text>
                  <Amount label="Book" value={p.pnlTotalBook} scale={scale} />
                  {p.pnlTotalWhatIf !== undefined && <Amount label="What-if" value={p.pnlTotalWhatIf} scale={scale} />}
                  <Amount label="Net" value={p.pnlTotalNet} scale={scale} />
                  <Marks marks={totalMarks(p)} />
                </>
              )}
          </Group>
          <PkGrid name="pk/moves" label="Moves" rows={moves} rowKey={moveKey} columns={columns.moves} most={MOVES_ROWS} pinned={2} gridRef={movesGrid} onEdit={onMoveEdit} />
        </section>
      </Stack>
    </GridUndoContext.Provider>
  );
}

/** The page of a Position Keeper workspace that could not be made (ADR-0019 §4): why, in the server's words, and no grid. */
export function PkUnavailable({ reason }: { readonly reason: string }) {
  return (
    <Alert color="yellow" title="The Position Keeper workspace was not made" data-testid="pk-unavailable">
      <Text size="sm">{reason}</Text>
      <Text size="sm" mt="xs">Nothing stands in for a book that could not be read. Once the result set is there, run <Code>new pk</Code> again.</Text>
    </Alert>
  );
}
