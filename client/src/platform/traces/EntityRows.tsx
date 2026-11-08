import { useEffect, useMemo, useState } from 'react';
import { Alert, Group, Stack, Text } from '@mantine/core';
import { GridSource, type GridColumn } from '@ex-grid/core';
import { compactGridHeight } from '../grid/metrics';
import { WorkbenchGrid } from '../grid/WorkbenchGrid';
import { MAX_PAGE, refusalText, type TraceApi } from './api';
import type { EntityCheck, EntityRow, LeafSetKind, TraceAddress, TraceHeader } from './contracts';
import { checkLine, columnLabel, entitySides } from './entities';
import { SIDE_TITLE, unitText } from './words';

// The entities below a term (ADR-0012 §6): each entity's share on each side, the sum of its rows' contributions at
// every depth below the term, with the Diff, in the server's order (the largest change first, or with one side the
// largest share), paged by the server. Beneath, whether each side's entities add up to the term's allocation.

const figure = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const format = (v: unknown) => (typeof v === 'number' ? figure.format(v) : String(v));

export function EntityRows({ api, address, header, path, set }: {
  readonly api: TraceApi;
  readonly address: TraceAddress;
  readonly header: TraceHeader;
  /** The term the entities are below. */
  readonly path: string;
  /** The leaf set that names the entity and its contribution. */
  readonly set: LeafSetKind;
}) {
  const [failure, setFailure] = useState<string | undefined>(undefined);
  const [checks, setChecks] = useState<readonly EntityCheck[] | undefined>(undefined);
  const [total, setTotal] = useState<number | undefined>(undefined);

  const source = useMemo(() => GridSource.fetch<EntityRow>(async (query, signal) => {
    // The server orders the entities; a filter or a sort here is refused, never ignored.
    if (query.filter !== undefined) throw new Error('The entities cannot be filtered. Clear the filter to see them.');
    if (query.sorts.length > 0) throw new Error('The entities are in the order the server gives: the largest change first.');
    const answer = await api.entities(address, path, { offset: query.range.start, limit: Math.min(query.range.count, MAX_PAGE) }, signal);
    setChecks(answer.entities.checks);
    setTotal(answer.entities.total);
    return { rows: answer.entities.rows, start: answer.entities.offset, totalCount: answer.entities.total };
  }, { rowKey: (r) => `${r.leafSet}\u0000${r.entity}`, onFetchFailed: (event) => setFailure(refusalText(event.error)) }),
  [api, address, path]);
  useEffect(() => () => source.dispose(), [source]);

  const contribution = set.columns.find((c) => c.name === set.contributionField);
  const unit = contribution?.unit;
  const currency = unitText(unit, header);
  const both = header.sides.length === 2;

  const columns = useMemo((): readonly GridColumn<EntityRow>[] => {
    const titled = (title: string) => (currency === '' ? title : `${title} (${currency})`);
    const out: GridColumn<EntityRow>[] = [
      { name: 'entity', header: columnLabel(set, set.entityField) ?? 'Entity', type: 'text', width: { auto: true, minPx: 96, maxPx: 240 }, queryable: false, value: (r) => r.entity },
      ...header.sides.map((side): GridColumn<EntityRow> => ({
        name: side, header: titled(SIDE_TITLE[side]), type: 'number', width: 140, queryable: false, format, value: (r) => r[side]?.contribution,
      })),
    ];
    if (both) out.push({ name: 'diff', header: titled('Diff'), type: 'number', width: 140, queryable: false, format, value: (r) => r.diff ?? undefined });
    out.push(both
      ? { name: 'rows', header: 'Rows (Before · After)', type: 'text', width: { auto: true, minPx: 72, maxPx: 180 }, queryable: false,
          value: (r) => header.sides.map((s) => r[s]?.rows ?? 0).join(' · ') }
      : { name: 'rows', header: 'Rows', type: 'number', width: 80, queryable: false, value: (r) => r[header.sides[0]!]?.rows });
    if (both) out.push({ name: 'sides', header: 'Sides', type: 'text', width: { auto: true, minPx: 96, maxPx: 200 }, queryable: false, value: (r) => entitySides(r, header.sides) });
    return out;
  }, [set, header, both, currency]);

  const line = checks === undefined ? undefined : checkLine(checks, unit);

  return (
    <Stack gap="xs" data-testid="trace-entities" aria-label="Entities below the term">
      {failure !== undefined && <Alert color="red" variant="light" withCloseButton onClose={() => setFailure(undefined)} title="The entities could not be read">{failure}</Alert>}
      <WorkbenchGrid<EntityRow> columns={columns} source={source} density="compact" viewportWidth="stretch"
        viewportHeight={compactGridHeight(Math.min(total === undefined ? 1 : Math.max(total, 1), 8))} pinnedColumnCount={1} locale="en-US" hideColumnMenu />
      {line !== undefined && (
        <Group gap="xs" wrap="wrap">
          <Text size="xs" data-testid="trace-entities-check" {...(line.error ? { c: 'red' } : {})}>{line.text}</Text>
        </Group>
      )}
    </Stack>
  );
}
