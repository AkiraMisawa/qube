import { Stack, Table, Text } from '@mantine/core';
import type { RegisteredMeasure } from './contracts';
import type { MetricCell } from './metrics';
import { useRequestRows } from './requestRows';
import { riskSections, type RiskSection } from './risks';

// The Risks tab (the owner's Risks of 2026-11-08): per measure, its credit, IR and FX
// deltas, Before, After and Diff, for the case and the market in view. A measure with no deltas says why.

export function XvaRisks({ runId, measures, register, caseId, scenario }: {
  readonly runId: string;
  /** The measures the tab draws, in its order (CVA, then FVA). */
  readonly measures: readonly string[];
  readonly register: readonly RegisteredMeasure[];
  readonly caseId: string;
  readonly scenario: string;
}) {
  const read = useRequestRows(runId, measures, register, 'risks', 'deltas');
  if (read.kind === 'pending') return <Text size="sm">Reading the deltas…</Text>;
  if (read.kind === 'none') return <Text size="sm" data-testid="xva-risks-none">{read.reason}</Text>;
  const sections = riskSections(read.rows, measures, register, caseId, scenario);
  return (
    // Two measures side by side from the panel's `lg`, not the window's (ADR-0018 §5).
    <div className="qube-xva-pair" {...(sections.length > 1 ? { 'data-pair': true } : {})} data-testid="xva-risks">
      {sections.map((s) => <Section key={s.measure} section={s} />)}
    </div>
  );
}

const SIDES = [['before', 'Before'], ['after', 'After'], ['diff', 'Diff']] as const;

/** One measure's deltas: a side priced nowhere has no column, nor has the Diff then (ADR-0010 §7). */
function Section({ section }: { readonly section: RiskSection }) {
  const heading = [section.title, section.metricTitle !== section.title ? section.metricTitle : undefined, section.currency].filter((x) => x !== undefined).join(' · ');
  if (section.reason !== undefined) {
    return (
      <Stack gap={4} data-testid="xva-risks-section" data-measure={section.measure}>
        <Text size="sm" fw={600}>{section.title}</Text>
        <Text size="sm" data-testid="xva-risks-reason">{section.reason}</Text>
      </Stack>
    );
  }
  const priced = (side: 'before' | 'after') => section.lines.some((l) => l[side].status !== 'notPriced');
  const sides = SIDES.filter(([side]) => (side === 'diff' ? priced('before') && priced('after') : priced(side)));
  return (
    <Stack gap={4} data-testid="xva-risks-section" data-measure={section.measure}>
      <Text size="sm" fw={600}>{heading}</Text>
      <div className="qube-xva-scroll">
        <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="sm" fz="sm" aria-label={`${section.title} deltas`} stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Risk class</Table.Th><Table.Th>Factor</Table.Th><Table.Th>Bump</Table.Th>
              {sides.map(([side, title]) => <Table.Th key={side} className="qube-number">{title}</Table.Th>)}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {section.lines.map((l) => (
              <Table.Tr key={l.key}>
                <Table.Td>{l.riskClass}</Table.Td>
                <Table.Th scope="row" fw={400}>{l.factor}</Table.Th>
                <Table.Td>{l.bump}</Table.Td>
                {sides.map(([side, title]) => <Delta key={side} cell={l[side]} label={`${section.title} ${l.factor} ${title}`} reason={l.reason} />)}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </div>
    </Stack>
  );
}

/** A delta, or the word for why there is none; a blank Diff where a side has no figure, never 0. */
function Delta({ cell, label, reason }: { readonly cell: MetricCell; readonly label: string; readonly reason: string | undefined }) {
  const word = cell.status !== 'priced' && cell.status !== 'pending';
  return (
    <Table.Td className={word ? 'qube-number qube-xva-result-word' : 'qube-number'} data-status={cell.status} aria-label={`${label}: ${cell.text === '' ? 'none' : cell.text}`}
      {...(cell.negative ? { 'data-negative': true } : {})}
      {...(word && reason !== undefined ? { title: reason } : {})}>{cell.text}</Table.Td>
  );
}
