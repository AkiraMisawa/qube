import { useMemo, useRef } from 'react';
import '@ex-grid/chart/styles.css';
import '@ex-grid/chart-mantine/styles.css';
import { Alert, NativeSelect, Stack, Table, Text } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { ChartDefinition, tenor } from '@ex-grid/chart';
import { ExChart } from '@ex-grid/chart-react';
import { mantineChartChrome } from '@ex-grid/chart-mantine';
import type { RegisteredMeasure } from './contracts';
import { chartSeries, profilesOf, profileTable, type ProfileTable, type ProfileValue } from './profiles';
import { useRequestRows } from './requestRows';
import { formatAmount, isLoss } from '../../platform/grid/amounts';
import { panelKey, usePanelState } from '../../platform/panelState';

// The Profile tab (the owner's Profile of 2026-11-08): the profiles the Run returned
// for the case and the market in view, chosen one at a time, as a table of its dates and a chart of the same points,
// Before and After, each series shown or hidden from the chart's legend. ExChart draws the chart (ex-grid's
// `@ex-grid/chart-react`, in Mantine's Chrome); its width is the one the browser reports for its box, never read here.

const years = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
const CHART_HEIGHT = 280;

// The profile chosen and the series hidden from the legend, kept by the panel while it is hidden (ADR-0006 note 10).
const PICKED = panelKey<string | undefined>('xva.profile.picked');
const HIDDEN = panelKey<readonly string[]>('xva.profile.hidden');

export function XvaProfile({ runId, measures, register, caseId, scenario }: {
  readonly runId: string;
  /** The measures the tab draws (CVA, FVA, KVA): the requests that price them return the profiles. */
  readonly measures: readonly string[];
  readonly register: readonly RegisteredMeasure[];
  readonly caseId: string;
  readonly scenario: string;
}) {
  const read = useRequestRows(runId, measures, register, 'profiles', 'profiles');
  const [picked, setPicked] = usePanelState(PICKED, undefined);
  if (read.kind === 'pending') return <Text size="sm">Reading the profiles…</Text>;
  if (read.kind === 'none') return <Text size="sm" data-testid="xva-profile-none">{read.reason}</Text>;
  const profiles = profilesOf(read.rows, caseId, scenario);
  const profile = profiles.find((p) => p.id === picked) ?? profiles[0];
  if (profile === undefined) {
    return (
      <Text size="sm" data-testid="xva-profile-none">
        This Run returned no profile for this case and market: the exposure profile comes with CVA or FVA priced and not refused, the capital profile with KVA.
      </Text>
    );
  }
  const table = profileTable(read.rows, profile, caseId, scenario);
  return (
    <Stack gap="xs" data-testid="xva-profile">
      <div className="qube-toolbar" role="group" aria-label="Profile">
        <NativeSelect size="xs" aria-label="Profile" className="qube-toolbar-input" value={profile.id} data-testid="xva-profile-choice"
          data={profiles.map((p) => ({ value: p.id, label: p.title }))} onChange={(e) => setPicked(e.currentTarget.value)} />
        <Text size="xs" data-testid="xva-profile-returned">
          Returned: {profiles.map((p) => p.title).join(', ')}{profile.currency === undefined ? '' : ` · ${profile.title} in ${profile.currency}`}
        </Text>
      </div>
      {/* Side by side from the panel's `lg`, not the window's (ADR-0018 §5). */}
      <div className="qube-xva-pair" data-pair>
        <ProfileChart table={table} />
        <ProfileGrid table={table} />
      </div>
    </Stack>
  );
}

const SIDES = [['before', 'Before'], ['after', 'After'], ['diff', 'Diff']] as const;

/** An exposure as every amount is shown (C48): grouped in threes, in whole units, red when negative; blank where none. */
function Figure({ value }: { readonly value: ProfileValue }) {
  if (value === undefined) return <Table.Td className="qube-number" />;
  return <Table.Td className="qube-number" {...(isLoss(value) ? { 'data-negative': true } : {})}>{formatAmount(value)}</Table.Td>;
}

/** The profile's dates, each series' sides: a side with no point at a date, past its last maturity, is blank, never 0. */
function ProfileGrid({ table }: { readonly table: ProfileTable }) {
  const sides = SIDES.filter(([side]) => (side === 'diff' ? table.sides.before && table.sides.after : table.sides[side]));
  return (
    <div className="qube-xva-scroll" data-testid="xva-profile-table">
      <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="sm" fz="sm" aria-label={`${table.profile.title} profile`}>
        <Table.Thead>
          <Table.Tr>
            <Table.Th rowSpan={2}>Date</Table.Th>
            <Table.Th rowSpan={2} className="qube-number">Years</Table.Th>
            {table.series.map((s) => <Table.Th key={s.id} colSpan={sides.length} className="qube-xva-result-group">{s.title}</Table.Th>)}
          </Table.Tr>
          <Table.Tr>
            {table.series.flatMap((s) => sides.map(([side, title]) => <Table.Th key={`${s.id}.${side}`} className="qube-number" aria-label={`${s.title} ${title}`}>{title}</Table.Th>))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {table.rows.map((r) => (
            <Table.Tr key={r.years}>
              <Table.Th scope="row" fw={400} className="qube-nowrap">{r.date}</Table.Th>
              <Table.Td className="qube-number">{years.format(r.years)}</Table.Td>
              {table.series.flatMap((s) => sides.map(([side]) => (
                <Figure key={`${s.id}.${side}`} value={r.values[s.id]?.[side]} />
              )))}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </div>
  );
}

/**
 * The profile's series as lines over time in years, a colour a series, After solid and Before dashed: a line style is a
 * second channel, so a side is never told by colour alone (ex-grid ADR-0256): a line-style key takes the next style the
 * first time a series names it, solid first, and After is named first. A series is hidden or shown from the legend. A definition ExChart refuses is said in its words, never drawn as something else.
 */
function ProfileChart({ table }: { readonly table: ProfileTable }) {
  const { ref, width } = useElementSize();
  const [hidden, setHidden] = usePanelState(HIDDEN, []);
  // The definition is made again only when the points change: every render hands ExChart the same one (ADR-0252).
  const series = chartSeries(table);
  const pointsKey = JSON.stringify(series);
  const seriesNow = useRef(series);
  seriesNow.current = series;
  const drawn = useMemo((): { readonly definition: ChartDefinition } | { readonly refused: string } => {
    try {
      return {
        definition: new ChartDefinition({
          axis: { values: 'numbers', title: 'Years', format: (years) => (years === 0 ? '0' : tenor(years)) },
          series: seriesNow.current.map((s) => ({
            id: s.id, name: s.name, kind: 'level' as const, points: s.points, keys: { colour: s.series, lineStyle: s.side }, formatCode: '#,##0',
          })),
        }),
      };
    } catch (error) {
      return { refused: error instanceof Error ? error.message : String(error) };
    }
  }, [pointsKey]);
  return (
    <div ref={ref} className="qube-xva-profile-chart" data-testid="xva-profile-chart">
      {'refused' in drawn
        ? <Alert color="red" variant="light" title="The chart was not drawn">{drawn.refused}</Alert>
        : width > 0 && (
          <ExChart definition={drawn.definition} width={Math.floor(width)} height={CHART_HEIGHT} density="compact" chrome={mantineChartChrome}
            title={`${table.profile.title} profile`} fileName={`${table.profile.id}-profile`} legend={{ position: 'bottom' }}
            hiddenSeries={hidden} onSeriesVisibilityChanged={(change) => setHidden((now) => (change.hidden ? [...now, change.key] : now.filter((k) => k !== change.key)))} />
        )}
    </div>
  );
}
