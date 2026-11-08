import { useId } from 'react';
import { Alert, Badge, Button, Group, Stack, Table, Text } from '@mantine/core';
import type { TypedDataRef } from '../../platform/contracts';
import { archiveUrl } from '../../platform/runs';
import { partHeight } from './layout';
import { useDatasets, useRun } from '../../platform/workspace/WorkspaceProvider';
import type { RegisteredMeasure } from './contracts';
import { headlines, metricsGrid, metricsTable, pricedMeasures, tradeCounts, type MetricCell, type MetricLine, type ModuleResult } from './metrics';

// The latest Price's Metrics, on the workspace page itself (ADR-0010 §7): a row per case, per priced measure Before,
// After and Diff. They are read from the Run's retained results, never recomputed here, and say what they are:
// simulated, of which revision, and whether the workspace has moved on since. Each module's figures show as its
// request answers, and a failed request leaves only its own measures unavailable (§10). Collapsed, the dock is its
// header and one line a case with each measure's headline Diff (§7).

export function XvaResult({ runId, pricedRevision, revision, register, onOpen, onCopyQuote, open, onOpenChange, height }: {
  readonly runId: string;
  /** The workspace revision the Run froze; unknown until the workspace lists the Run. */
  readonly pricedRevision: number | undefined;
  readonly revision: number;
  /** The registered measures, for their titles and metrics; what was priced is the Run's. */
  readonly register: readonly RegisteredMeasure[];
  readonly onOpen: () => void;
  /** Copy quote (ADR-0010 §7), which the server reads from the Run alone. */
  readonly onCopyQuote: () => void;
  /** The Metrics shown, or only a line a case. */
  readonly open: boolean;
  readonly onOpenChange: (open: boolean) => void;
  /** The most the open Metrics take in pixels before they scroll, which the dock's handle sets. */
  readonly height: number;
}) {
  const bodyId = useId();
  const run = useRun(runId);
  const steps = run.data?.workflow.steps ?? [];
  const outputs = run.data?.outputs ?? {};
  const references = steps.flatMap((s) => (outputs[s.stepId] === undefined ? [] : [outputs[s.stepId] as TypedDataRef]));
  const datasets = useDatasets(references);

  if (run.isError) return <Alert color="red" variant="light" title="Result">{run.error.message}</Alert>;
  const status = run.data?.status ?? 'running';
  const priced = run.data === undefined ? [] : pricedMeasures(run.data.input);

  // Each module's answer, by its step: rows once read, unavailable once it failed, absent while it runs.
  const results: Record<string, ModuleResult> = {};
  for (const step of steps) {
    const reference = outputs[step.stepId];
    if (reference === undefined) {
      if (status === 'running') continue;
      const failed = run.data?.invocations.find((i) => i.stepId === step.stepId && i.status !== 'succeeded');
      results[step.stepId] = { kind: 'unavailable', reason: failed?.error?.message ?? run.data?.error?.message ?? `${step.stepId} gave no result.` };
      continue;
    }
    const read = datasets[references.indexOf(reference)];
    if (read?.isError === true) results[step.stepId] = { kind: 'unavailable', reason: `The result could not be read: ${read.error.message}` };
    else if (read?.data !== undefined) results[step.stepId] = { kind: 'rows', rows: read.data.rows };
  }
  const table = priced === undefined ? undefined : metricsTable(register, priced, results);
  const grid = table === undefined ? undefined : metricsGrid(table);
  const simulated = datasets.some((d) => d.data?.provenance.executionMode === 'simulated');
  const counts = table === undefined ? [] : tradeCounts(table);
  const behind = pricedRevision !== undefined && pricedRevision < revision;
  const sides = [
    ...(table?.sides.before === true ? [['before', 'Before'] as const] : []),
    ...(table?.sides.after === true ? [['after', 'After'] as const] : []),
    ...(table?.sides.before === true && table.sides.after ? [['diff', 'Diff'] as const] : []),
  ];
  const lines = table === undefined ? [] : headlines(register, table);
  const reasons = [...new Map((table?.lines ?? []).filter((l) => l.reason !== undefined)
    .map((l) => [`${l.measure}:${l.reason}`, l] as const)).values()];

  return (
    <Stack gap={4} className="qwb-xva-result" data-testid="xva-result" aria-label="Latest price">
      <Group gap="xs" wrap="wrap">
        <Button size="compact-xs" variant="subtle" aria-expanded={open} aria-controls={bodyId} onClick={() => onOpenChange(!open)}
          title={open ? 'Collapse the result to a line a case.' : 'Show the Metrics.'}>{open ? 'Collapse' : 'Expand'}</Button>
        <Text size="sm" fw={600}>Latest price</Text>
        <Badge variant="light" color={status === 'succeeded' ? 'teal' : status === 'running' ? 'blue' : status === 'failed' ? 'red' : 'yellow'}
          data-testid="xva-result-status">{status}</Badge>
        {simulated && <Badge variant="light" color="yellow">simulated</Badge>}
        {pricedRevision !== undefined && (
          <Badge variant="light" color={behind ? 'yellow' : 'gray'} data-testid="xva-result-revision"
            title={behind ? 'The workspace changed after this Price. Price again to see its figures.' : 'The figures are of the workspace as it stands.'}>
            {behind ? `revision ${pricedRevision} · workspace now ${revision}` : `revision ${pricedRevision}`}
          </Badge>
        )}
        {counts.length > 0 && (
          <Text size="xs" data-testid="xva-result-trades" title="Trades each measure priced, Before → After">Trades: {counts.join(' · ')}</Text>
        )}
        {/* A Run's actions (ADR-0010 §7): each once the Run has finished, Copy quote while it priced CVA. */}
        <Group gap={4} ml="auto">
          <Button size="compact-xs" variant="default" onClick={onCopyQuote} disabled={status === 'running' || priced?.includes('cva') !== true}
            title={priced?.includes('cva') === true ? 'Copy the CVA Charge and PV each case adds, as HTML and text.' : 'This Run did not price CVA, so it has no quote.'}>Copy quote</Button>
          {status === 'running'
            ? <Button size="compact-xs" variant="default" disabled>Download</Button>
            : <Button size="compact-xs" variant="default" component="a" href={archiveUrl(runId)} download={`${runId}.zip`}>Download</Button>}
          <Button size="compact-xs" variant="default" onClick={onOpen}>Open run</Button>
        </Group>
      </Group>
      {status === 'failed' && <Alert color="red" variant="light" title={run.data?.error?.code ?? 'Failed'}>{run.data?.error?.message}</Alert>}
      {!open && lines.length > 0 && (
        <Stack gap={0} data-testid="xva-result-lines" aria-label="Headline per case">
          {lines.map((l) => <Text key={l.key} size="xs" className="qwb-number-text">{l.text}</Text>)}
        </Stack>
      )}
      {open && (
        <Stack gap={4} id={bodyId} className="qwb-xva-result-body" data-testid="xva-result-body" style={partHeight(height)}>
          {priced === undefined && (
            <Text size="xs" c="dimmed">This Run was priced before measures were registered. Open it to see its figures, or Price again.</Text>
          )}
          {table?.note !== undefined && <Text size="xs" c="dimmed" data-testid="xva-result-note">{table.note}</Text>}
          {grid !== undefined && grid.rows.length > 0 && (
            <div className="qwb-xva-result-table">
              <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="sm" fz="sm" aria-label="Metrics">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th rowSpan={2}>Case</Table.Th>
                    {grid.columns.map((c) => <Table.Th key={c.key} colSpan={sides.length} className="qwb-xva-result-group">{c.metricTitle}</Table.Th>)}
                  </Table.Tr>
                  <Table.Tr>
                    {grid.columns.flatMap((c) => sides.map(([side, title]) => (
                      <Table.Th key={`${c.key}.${side}`} className="qwb-number" aria-label={`${c.metricTitle} ${title}`}>{title}</Table.Th>
                    )))}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {grid.rows.map((row) => (
                    <Table.Tr key={row.key}>
                      <Table.Th scope="row">{row.title}</Table.Th>
                      {grid.columns.flatMap((c) => sides.map(([side, title]) => {
                        const line = row.cells[c.key];
                        return <Figure key={`${c.key}.${side}`} cell={line?.[side]} line={line} label={`${c.metricTitle} ${title}`} />;
                      }))}
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </div>
          )}
          {reasons.map((l) => (
            <Text key={`${l.measure}:${l.reason}`} size="xs" {...(l.after.status === 'refused' ? {} : { c: 'red' })} data-testid="xva-result-reason">
              {l.measureTitle} {l.after.status === 'refused' ? 'refused' : 'unavailable'}: {l.reason}
            </Text>
          ))}
        </Stack>
      )}
    </Stack>
  );
}

/** One figure, or the word for why there is none; a blank where no Diff exists, never 0. */
function Figure({ cell, line, label }: { readonly cell: MetricCell | undefined; readonly line: MetricLine | undefined; readonly label: string }) {
  if (cell === undefined) return <Table.Td className="qwb-number" aria-label={`${label}: not asked`} />;
  const word = cell.status !== 'priced' && cell.status !== 'pending';
  return (
    <Table.Td className={word ? 'qwb-number qwb-xva-result-word' : 'qwb-number'} data-status={cell.status}
      {...(line?.reason === undefined ? {} : { title: line.reason })}>
      {cell.text}
    </Table.Td>
  );
}
