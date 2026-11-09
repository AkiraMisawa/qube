import { useId, type ReactNode } from 'react';
import { Alert, Anchor, Badge, Button, Group, NativeSelect, Stack, Table, Tabs, Text } from '@mantine/core';
import type { TypedDataRef } from '../../platform/contracts';
import { archiveUrl } from '../../platform/runs';
import { BASE_CASE } from '../../platform/traces/open';
import { partHeight } from './layout';
import { useDatasets, useRun } from '../../platform/workspace/WorkspaceProvider';
import type { RegisteredMeasure } from './contracts';
import { conversionOf, dockTabs, METRICS_TAB, unpricedReasons, type MeasureTab } from './dock';
import { BASE_MARKET, headlines, inMarket, marketsOf, metricsGrid, metricsTable, pricedMeasures, reasons, tradeCounts, type MetricCell, type MetricLine, type ModuleResult } from './metrics';

// The latest Price's Metrics, on the workspace page itself (ADR-0010 §7): a row per case, per priced measure Before,
// After and Diff. They are read from the Run's retained results, never recomputed here, and say what they are:
// simulated, of which revision, and whether the workspace has moved on since. Each module's figures show as its
// request answers, and a failed request leaves only its own measures unavailable (§10). Collapsed, the dock is its
// header and one line a case with each measure's headline Diff (§7). A Run that priced scenarios shows one market at a
// time, the base market first, chosen with the scenario selector; the Metrics, the line and the tabs follow it (§4).

export function XvaResult({ runId, pricedRevision, revision, register, selected, onOpen, onCopyQuote, onTrace, open, onOpenChange, height, tab, onTab, full, onFull, traceCase, onTraceCase, scenario, onScenario, renderTab }: {
  readonly runId: string;
  /** The workspace revision the Run froze; unknown until the workspace lists the Run. */
  readonly pricedRevision: number | undefined;
  readonly revision: number;
  /** The registered measures, for their titles and metrics; what was priced is the Run's. */
  readonly register: readonly RegisteredMeasure[];
  /** The measures selected now: a measure's own tab is shown while it is selected and the Run priced it (ADR-0010 §7). */
  readonly selected: readonly string[];
  readonly onOpen: () => void;
  /** Copy quote (ADR-0010 §7), which the server reads from the Run alone, of the market in view (§4). */
  readonly onCopyQuote: (scenario: string) => void;
  /** Opens a measure's trace for a case under a market (ADR-0011 §6), from a Metrics cell of a measure that keeps one: at
   * the term its metric names, or at the root (ADR-0010 §7's note of 2026-11-08). */
  readonly onTrace: (measure: string, caseId: string, scenario: string, term: string | undefined) => void;
  /** The Metrics shown, or only a line a case. */
  readonly open: boolean;
  readonly onOpenChange: (open: boolean) => void;
  /** The most the open Metrics take in pixels before they scroll, which the dock's handle sets. */
  readonly height: number;
  /** The tab asked for, and choosing one: Metrics or a tab a measure the Run priced registers (ADR-0010 §7). */
  readonly tab: string;
  readonly onTab: (tab: string) => void;
  /** Whether the dock is widened over the trade grids (owner, 2026-11-08), and widening or restoring it. */
  readonly full: boolean;
  readonly onFull: (full: boolean) => void;
  /** The case a measure's own tab draws, chosen with the dock's case selector, the Base by default (ADR-0010 §7). */
  readonly traceCase: string;
  readonly onTraceCase: (caseId: string) => void;
  /** The market in view, chosen with the dock's scenario selector, the base market by default (ADR-0010 §4). */
  readonly scenario: string;
  readonly onScenario: (scenario: string) => void;
  /**
   * What a measure's own tab draws while it is shown, such as its trace, under what the page says of every figure in it
   * (ADR-0012 §7), for the case and the market in view: those chosen where this Run priced them, else the Base and the
   * base market.
   */
  readonly renderTab: (tab: MeasureTab, caption: string | undefined, caseId: string, scenario: string) => ReactNode;
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
  // A tab asked for that this Run has not, such as a measure it did not price, shows Metrics.
  const tabs = dockTabs(register, priced, selected);
  const shown = tabs.find((t) => t.id === tab) ?? METRICS_TAB;

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
  const all = priced === undefined ? undefined : metricsTable(register, priced, results);
  // The markets the Run priced, the base market first (ADR-0010 §4); one another Run priced is not this one's.
  const markets = all === undefined ? [BASE_MARKET] : marketsOf(all);
  const market = markets.find((m) => m.key === scenario) ?? BASE_MARKET;
  const table = all === undefined ? undefined : inMarket(all, market.key);
  const grid = table === undefined ? undefined : metricsGrid(table);
  // A measure priced on no side has no trace to draw: its tab says why, as the Metrics do (ADR-0012 §1).
  const unpriced = shown.view === 'metrics' ? undefined : unpricedReasons(table, shown.measure);
  const simulated = datasets.some((d) => d.data?.provenance.executionMode === 'simulated');
  const counts = table === undefined ? [] : tradeCounts(table);
  const behind = pricedRevision !== undefined && pricedRevision < revision;
  const sides = [
    ...(table?.sides.before === true ? [['before', 'Before'] as const] : []),
    ...(table?.sides.after === true ? [['after', 'After'] as const] : []),
    ...(table?.sides.before === true && table.sides.after ? [['diff', 'Diff'] as const] : []),
  ];
  const lines = table === undefined ? [] : headlines(register, table);
  // The Run's cases, for a measure's tab to draw one of (ADR-0010 §7): a case another Run made is not this one's.
  const cases = (grid?.rows ?? []).filter((r) => r.key !== '');
  const inView = cases.some((c) => c.key === traceCase) ? traceCase : BASE_CASE;
  // Copy quote is a quoted measure's (§7, §11: XVA's, from CVA), offered while the Run priced one.
  const quoted = register.filter((m) => m.quoted);
  const quotes = priced !== undefined && quoted.some((m) => priced.includes(m.id));
  const quotedNames = quoted.map((m) => m.title).join(' or ');
  // The currency the Run's figures are in, which it froze (§1, §7); the screen's setting may have moved since.
  const currencies = [...new Set((table?.lines ?? []).flatMap((l) => (l.currency === undefined ? [] : [l.currency])))];
  const said = table === undefined ? [] : reasons(table);
  // What a measure's figures are computed on, said with them once per measure (`Spot IM · SIMM PARAMETERS DEMO`).
  const bases = [...new Map((table?.lines ?? []).flatMap((l) => (l.basis === undefined ? []
    : [[l.measure, { measure: l.measure, title: l.measureTitle, basis: l.basis }] as const]))).values()];

  return (
    <Stack gap="xs" className="qwb-xva-result" data-testid="xva-result" aria-label="Latest price">
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
        {bases.map((b) => (
          <Badge key={b.measure} variant="light" color="yellow" data-testid="xva-result-basis"
            title={`${b.title}'s figures are computed with ${b.basis}, not a calibration of record.`}>{b.title} · {b.basis}</Badge>
        ))}
        {currencies.length > 0 && (
          <Badge variant="light" color="gray" data-testid="xva-result-currency" title="The currency this Run's figures are in.">{currencies.join(' · ')}</Badge>
        )}
        {markets.length > 1 && (
          <NativeSelect size="xs" aria-label="Scenario" data-testid="xva-result-scenario" value={market.key}
            title="The market the Metrics, the line a case and the tabs show: the base market, or a scenario this Run priced."
            data={markets.map((m) => ({ value: m.key, label: m.title }))} onChange={(e) => onScenario(e.currentTarget.value)} />
        )}
        {open && shown.view !== 'metrics' && cases.length > 1 && (
          <NativeSelect size="xs" aria-label="Case" data-testid="xva-result-case" value={inView} title={`The case the ${shown.title} tab draws.`}
            data={cases.map((c) => ({ value: c.key, label: c.title }))} onChange={(e) => onTraceCase(e.currentTarget.value)} />
        )}
        {counts.length > 0 && (
          <Text size="xs" data-testid="xva-result-trades" title="Trades each measure priced, Before → After">Trades: {counts.join(' · ')}</Text>
        )}
        {/* A Run's actions (ADR-0010 §7): each once the Run has finished, Copy quote while it priced CVA. */}
        <Group gap="xs" ml="auto">
          <Button size="compact-xs" variant="default" onClick={() => onCopyQuote(market.key)} disabled={status === 'running' || !quotes}
            title={!quotes ? `This Run did not price ${quotedNames}, so it has no quote.`
              : market.key === BASE_MARKET.key ? 'Copy what each case adds, as HTML and text, for a quote.'
                : `Copy what each case adds under ${market.title}, as HTML and text; the quote names the scenario.`}>Copy quote</Button>
          {status === 'running'
            ? <Button size="compact-xs" variant="default" disabled>Download</Button>
            : <Button size="compact-xs" variant="default" component="a" href={archiveUrl(runId)} download={`${runId}.zip`}>Download</Button>}
          <Button size="compact-xs" variant="default" onClick={onOpen}>Open run</Button>
          {open && (
            <Button size="compact-xs" variant="default" visibleFrom="sm" onClick={() => onFull(!full)} data-testid="xva-result-full"
              title={full ? 'Put the dock back to the height its handle sets, under the trade grids.' : 'Widen the dock over the input tabs and the trade grids.'}>
              {full ? 'Restore height' : 'Full height'}
            </Button>
          )}
        </Group>
      </Group>
      {open && tabs.length > 1 && (
        <Tabs value={shown.id} onChange={(value) => { if (value !== null) onTab(value); }} keepMounted={false}>
          <Tabs.List aria-label="Results">
            {tabs.map((t) => <Tabs.Tab key={t.id} value={t.id}>{t.title}</Tabs.Tab>)}
          </Tabs.List>
        </Tabs>
      )}
      {status === 'failed' && <Alert color="red" variant="light" title={run.data?.error?.code ?? 'Failed'}>{run.data?.error?.message}</Alert>}
      {!open && lines.length > 0 && (
        <Stack gap={0} data-testid="xva-result-lines" aria-label="Headline per case">
          {lines.map((l) => <Text key={l.key} size="xs" className="qwb-number-text">{l.text}</Text>)}
        </Stack>
      )}
      {open && shown.view !== 'metrics' && (
        <div id={bodyId} className="qwb-xva-result-tab" data-testid="xva-result-tab" data-full={full} style={partHeight(height)}>
          {status === 'running'
            ? <Text size="sm">The {shown.title} tab draws once the Run has finished.</Text>
            : unpriced !== undefined
              ? <Stack gap={0} data-testid="xva-result-tab-unpriced">{unpriced.map((r) => <Text key={r.text} size="sm" {...(r.failed ? { c: 'red' } : {})}>{r.text}</Text>)}</Stack>
              : renderTab(shown, conversionOf(table, shown.measure), inView, market.key)}
        </div>
      )}
      {open && shown.view === 'metrics' && (
        <Stack gap="xs" id={bodyId} className="qwb-xva-result-body" data-testid="xva-result-body" data-full={full} style={partHeight(height)}>
          {priced === undefined && (
            <Text size="xs">This Run was priced before measures were registered. Open it to see its figures, or Price again.</Text>
          )}
          {table?.note !== undefined && <Text size="xs" data-testid="xva-result-note">{table.note}</Text>}
          {grid !== undefined && grid.rows.length > 0 && (
            <div className="qwb-xva-result-table">
              <Table withTableBorder withColumnBorders verticalSpacing="xs" horizontalSpacing="sm" fz="sm" aria-label="Metrics">
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
                        // A measure that keeps a trace opens it from each of its figures of a case, once the Run has finished
                        // (ADR-0010 §7); a line of no case is one whose module gave no rows, and has no trace to open.
                        const caseId = line?.case;
                        const traced = status !== 'running' && caseId !== undefined && register.find((m) => m.id === line!.measure)?.traced === true;
                        return <Figure key={`${c.key}.${side}`} cell={line?.[side]} line={line} label={`${c.metricTitle} ${title}`}
                          {...(traced ? { onTrace: () => onTrace(line!.measure, caseId, market.key, line!.term) } : {})} />;
                      }))}
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </div>
          )}
          {said.map((r) => (
            <Text key={r.key} size="xs" {...(r.failed ? { c: 'red' } : {})} data-testid="xva-result-reason">{r.text}</Text>
          ))}
        </Stack>
      )}
    </Stack>
  );
}

/**
 * One figure, or the word for why there is none; a blank where no Diff exists, never 0. A figure of a measure that
 * keeps a trace is a link to it: why there is a figure, or why there is none, is the trace's to say. A side's note, such
 * as spot IM's governing regime, stands before the figure, so that the figures stay aligned on their right edge.
 */
function Figure({ cell, line, label, onTrace }: {
  readonly cell: MetricCell | undefined; readonly line: MetricLine | undefined; readonly label: string; readonly onTrace?: () => void;
}) {
  if (cell === undefined) return <Table.Td className="qwb-number" aria-label={`${label}: not asked`} />;
  const word = cell.status !== 'priced' && cell.status !== 'pending';
  const noted = cell.note === undefined ? '' : `${cell.note} `;
  return (
    <Table.Td className={word ? 'qwb-number qwb-xva-result-word' : 'qwb-number'} data-status={cell.status}
      {...(line?.reason === undefined ? {} : { title: line.reason })}>
      {cell.note !== undefined && <Text span size="xs" className="qwb-xva-result-cell-note" data-testid="xva-result-cell-note">{cell.note}</Text>}
      {onTrace === undefined || cell.status === 'pending' || cell.status === 'notPriced'
        ? cell.text
        : <Anchor component="button" size="sm" className="qwb-xva-result-trace" onClick={onTrace} data-testid="xva-result-trace"
          aria-label={`${label} ${noted}${cell.text}: open its trace`}>{cell.text}</Anchor>}
    </Table.Td>
  );
}
