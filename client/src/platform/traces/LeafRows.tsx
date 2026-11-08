import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import { GridSource, type GridColumn, type GridSelection } from '@ex-grid/core';
import { compactGridHeight } from '../grid/metrics';
import { WorkbenchGrid } from '../grid/WorkbenchGrid';
import { MAX_PAGE, refusalText, type TraceApi } from './api';
import type { LeafSetKind, PairedLeafRow, Side, TraceAddress, TraceHeader } from './contracts';
import { leafFormulaTex } from './formula';
import { Tex } from './Tex';
import { SIDE_TITLE } from './words';

// The leaf rows below a term (ADR-0011 §5, §6): a grid paged and sorted by the server, one side's columns at a time,
// each row saying which sides hold it and where a computed column does not equal its formula. The row at the Focus has
// its leaf set's column formulas typeset with its values.

const rowKey = (r: PairedLeafRow) => `${r.entity}\u0000${r.component ?? ''}`;

/** Which sides hold a row: the trade's membership on each side. */
export function membershipOf(row: PairedLeafRow, sides: readonly Side[]): string {
  const on = sides.filter((s) => row[s] !== undefined && row[s] !== null);
  if (sides.length === 1) return on.length === 1 ? '' : 'not on this side';
  return on.length === 2 ? 'Before and After' : on.length === 1 ? `${SIDE_TITLE[on[0]!]} only` : 'on neither side';
}

/** A row's computed columns that do not equal their formulas on a side, in words. */
export function mismatchText(row: PairedLeafRow, side: Side, set: LeafSetKind): string {
  const mismatches = side === 'before' ? row.beforeMismatches : row.afterMismatches;
  return mismatches.map((m) => `${set.columns.find((c) => c.name === m.column)?.label ?? m.column} does not reconcile: ${m.computed} by its formula, ${m.returned} returned`).join(' · ');
}

const figure = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 6 });

export function LeafRows({ api, address, header, path, set, rowCount }: {
  readonly api: TraceApi;
  readonly address: TraceAddress;
  readonly header: TraceHeader;
  /** The term the rows are below. */
  readonly path: string;
  readonly set: LeafSetKind;
  /** How many rows each side wrote, for the caption. */
  readonly rowCount: Readonly<Partial<Record<Side, number>>>;
}) {
  const [side, setSide] = useState<Side>(header.sides.at(-1)!);
  const [failure, setFailure] = useState<string | undefined>(undefined);
  const [focus, setFocus] = useState<PairedLeafRow | undefined>(undefined);

  const source = useMemo(() => GridSource.fetch<PairedLeafRow>(async (query, signal) => {
    // The server pages and sorts by one column; a filter or a second sort is refused, never ignored.
    if (query.filter !== undefined) throw new Error('The leaf rows cannot be filtered. Clear the filter to see them.');
    if (query.sorts.length > 1) throw new Error('The leaf rows sort by one column at a time.');
    const sort = query.sorts[0];
    const answer = await api.leaves(address, path, {
      offset: query.range.start, limit: Math.min(query.range.count, MAX_PAGE),
      ...(sort === undefined ? {} : { sort: sort.column, descending: sort.direction === 'descending' }),
    }, signal);
    return { rows: answer.leaves.rows, start: answer.leaves.offset, totalCount: answer.leaves.total };
  }, { rowKey, onFetchFailed: (event) => setFailure(refusalText(event.error)) }),
  [api, address, path]);
  useEffect(() => () => source.dispose(), [source]);
  useEffect(() => setFocus(undefined), [source]);

  const columns = useMemo((): readonly GridColumn<PairedLeafRow>[] => [
    ...set.columns.map((c): GridColumn<PairedLeafRow> => {
      const numeric = c.unit !== undefined && c.unit !== null;
      return {
        name: c.name, header: c.label, type: numeric ? 'number' : 'text',
        width: numeric ? 160 : { auto: true, minPx: 96, maxPx: 240 },
        value: (r) => {
          const v = r[side]?.[c.name];
          return v === null ? undefined : v;
        },
        ...(numeric ? { format: (v: unknown) => (typeof v === 'number' ? figure.format(v) : String(v)) } : {}),
      };
    }),
    { name: 'membership', header: 'Sides', type: 'text', width: { auto: true, minPx: 96, maxPx: 200 }, queryable: false, value: (r) => membershipOf(r, header.sides) },
    { name: 'mismatch', header: 'Reconciliation', type: 'text', width: { auto: true, minPx: 120, maxPx: 360 }, queryable: false, value: (r) => mismatchText(r, side, set) },
  ], [set, side, header.sides]);

  const rowsRef = useRef(source);
  rowsRef.current = source;
  const onSelectionChanged = useCallback((selection: GridSelection) => {
    if (selection.focus === undefined) return;
    const state = rowsRef.current.snapshot();
    setFocus(state.window[selection.focus.row - state.windowStart]);
  }, []);

  const computed = set.columns.filter((c) => c.tex !== undefined && c.tex !== null);
  const focusRow = focus?.[side];
  const units = (field: string) => set.columns.find((c) => c.name === field)?.unit;
  const total = rowCount[side];

  return (
    <Stack gap="xs" data-testid="trace-leaves" aria-label="Leaf rows">
      <Group gap="xs" wrap="wrap">
        <Text size="xs" fw={600}>Rows below</Text>
        <Text size="xs">{header.sides.map((s) => `${SIDE_TITLE[s]} ${rowCount[s] === undefined ? 'none written' : rowCount[s]}`).join(' · ')}</Text>
        {header.sides.length === 2 && (
          <SegmentedControl size="xs" value={side} onChange={(v) => setSide(v as Side)} aria-label="Side the rows show"
            data={header.sides.map((s) => ({ value: s, label: SIDE_TITLE[s] }))} />
        )}
      </Group>
      {failure !== undefined && <Alert color="red" variant="light" withCloseButton onClose={() => setFailure(undefined)} title="The rows could not be read">{failure}</Alert>}
      <WorkbenchGrid<PairedLeafRow> columns={columns} source={source} density="compact" viewportWidth="stretch"
        viewportHeight={compactGridHeight(Math.min(total === undefined ? 1 : Math.max(total, 1), 8))} pinnedColumnCount={1} locale="en-US" hideColumnMenu
        onSelectionChanged={onSelectionChanged} />
      {focus !== undefined && focusRow !== undefined && focusRow !== null && computed.length > 0 && (
        <Stack gap={0} data-testid="trace-leaf-formulas" aria-label={`Formulas of ${focus.entity}`}>
          <Text size="xs" c="dimmed">{focus.entity}{focus.component ? ` · ${focus.component}` : ''}, {SIDE_TITLE[side]}</Text>
          {computed.map((c) => <Tex key={c.name} tex={leafFormulaTex(c.tex!, focusRow, units, c.name)} display />)}
        </Stack>
      )}
    </Stack>
  );
}
