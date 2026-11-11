import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import { actionColumn, GridSource, type GridActionEvent, type GridColumn } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import { useQuery } from '@tanstack/react-query';
import { call } from '../../platform/api';
import { amountTone, formatAmount } from '../../platform/grid/amounts';
import { useColumnWidths } from '../../platform/grid/columnWidths';
import type { DatasetColumnType } from '../../platform/contracts';
import { viewsFor } from '../../platform/views/binding';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import { keyOf } from '../../platform/workspace/workspaceStore';
import { registerGridFocus } from '../../shell/places';
import { downloadRun } from '../../platform/runs';
import type { Notify } from '../../platform/notices';

// The Pricing DB (docs/05 §7): every Run whatever started it, newest first, and one domain's figures when
// that domain is active. The server pages it; the grid shows it and neither sorts nor filters it.

type Row = Readonly<Record<string, unknown>>;
interface Column { readonly field: string; readonly type: DatasetColumnType; readonly nullable: boolean }
interface HistoryCatalog { readonly columns: readonly Column[]; readonly domains: readonly { readonly domain: string; readonly title: string; readonly columns: readonly Column[] }[] }
interface HistoryPage { readonly rows: readonly Row[]; readonly start: number; readonly totalCount: number; readonly orderToken: string }

const ALL = 'all';
const OPEN = 'open';
const DOWNLOAD = 'download';

const label = (field: string) => field.replace(/([A-Z])/g, ' $1').replace(/^./, (c) => c.toUpperCase());

function gridColumn(column: Column): GridColumn<Row> {
  const numeric = column.type === 'number' || column.type === 'integer';
  return {
    name: column.field,
    header: label(column.field),
    type: numeric ? 'number' : 'text',
    // The server orders the history; the grid offers no sort or filter of its own over it.
    queryable: false,
    value: (row) => row[column.field] ?? undefined,
    // A domain's figures (CVA, FVA) are its only `number` columns; counts are `integer`.
    format: column.type === 'number' ? formatAmount : (v) => String(v),
    ...(column.type === 'number' ? { tone: amountTone } : {}),
    width: column.field === 'runId' ? 270 : column.field === 'acceptedAt' ? 190 : numeric ? 110 : 110,
  };
}

export function PricingDbPanel({ panelId }: { readonly panelId: string }) {
  const workspace = useWorkspaceContext();
  const catalog = useQuery({ queryKey: ['history', 'catalog'], queryFn: () => call<HistoryCatalog>('/api/history/catalog'), staleTime: Infinity });
  const [domain, setDomain] = useState(ALL);
  if (catalog.isPending) return <Text c="dimmed">Loading the Pricing DB…</Text>;
  if (catalog.isError) return <Alert color="red" title="Pricing DB">{catalog.error.message}</Alert>;
  const columns = domain === ALL ? catalog.data.columns : catalog.data.domains.find((d) => d.domain === domain)?.columns ?? catalog.data.columns;
  return (
    <Stack gap="sm">
      <Group gap="sm" wrap="wrap">
        <SegmentedControl size="xs" aria-label="Domain" value={domain} onChange={setDomain}
          data={[{ value: ALL, label: 'All Runs' }, ...catalog.data.domains.map((d) => ({ value: d.domain, label: d.title }))]} />
        <Text size="xs" c="dimmed">{domain === ALL ? 'Every Run, whatever started it.' : 'Runs of this domain, with their figures. Empty means not available.'}</Text>
      </Group>
      <HistoryGrid key={domain} panelId={panelId} domain={domain === ALL ? null : domain} columns={columns} notify={workspace.notify} />
    </Stack>
  );
}

function HistoryGrid({ panelId, domain, columns, notify }: {
  readonly panelId: string;
  readonly domain: string | null;
  readonly columns: readonly Column[];
  readonly notify: Notify;
}) {
  const workspace = useWorkspaceContext();
  const grid = useRef<ExGridApi<Row>>(null);
  const [failure, setFailure] = useState<string | undefined>(undefined);
  const [total, setTotal] = useState<number | undefined>(undefined);
  const source = useMemo(() => GridSource.fetch<Row>(async (query, signal) => {
    const page = await call<HistoryPage>('/api/history/rows', {
      method: 'POST', body: JSON.stringify({ domain, start: query.range.start, count: query.range.count }), signal,
    });
    setTotal(page.totalCount);
    return page;
  }, { rowKey: (row) => row.runId as string, onFetchFailed: (e) => setFailure(e.error instanceof Error ? e.error.message : String(e.error)) }), [domain]);
  useEffect(() => () => source.dispose(), [source]);
  // Runs are added and finish while the page is open: the source reads its Window again, gathered.
  useEffect(() => {
    const timer = setInterval(() => source.notifyChanged(), 2000);
    return () => clearInterval(timer);
  }, [source]);
  useEffect(() => {
    registerGridFocus(panelId, () => grid.current?.focus());
    return () => registerGridFocus(panelId, undefined);
  }, [panelId]);

  const gridColumns = useMemo(() => [
    // Download is a Run's action, here as on the page that priced it (ADR-0010 §7).
    actionColumn<Row>('Actions', [{ name: OPEN, label: 'Open' }, { name: DOWNLOAD, label: 'Download' }], { width: 176 }),
    ...columns.map(gridColumn),
  ], [columns]);
  const widths = useColumnWidths(`history/${domain ?? ALL}`, gridColumns);

  const onAction = useCallback((event: GridActionEvent<Row>) => {
    const runId = event.row.runId as string;
    if (event.action === DOWNLOAD) { downloadRun(runId); return; }
    void workspace.api.run(runId).then((run) => {
      const view = viewsFor(run.workflow, workspace.catalog.views)[0];
      workspace.store.getState().openPanel({ kind: 'run', title: run.workflow.title, runId, viewKey: view === undefined ? undefined : keyOf(view) });
    }, (error: unknown) => notify('refused', `Run ${runId} could not be opened: ${error instanceof Error ? error.message : String(error)}`));
  }, [workspace, notify]);

  return (
    <Stack gap="xs">
      {failure !== undefined && <Alert color="red" variant="light" title="The history could not be read" withCloseButton onClose={() => setFailure(undefined)}>{failure}</Alert>}
      <div aria-label="Pricing DB" data-total={total}>
        <QubeGrid<Row> ref={grid} columns={widths.columns} onColumnWidthChanged={widths.onColumnWidthChanged} source={source} onAction={onAction}
          density="compact" viewportWidth="stretch" viewportHeight={460} pinnedColumnCount={2} locale="en-US" />
      </div>
    </Stack>
  );
}
