import { Button, Group, Text } from '@mantine/core';

/**
 * What a grid filtered from the line says of its filter (ADR-0016 note 41 of 2026-11-11): the conditions as typed, and
 * how many rows they leave of how many the grid has with none, once its reader has answered under them. `unread` is
 * said in their place where the conditions could not be read.
 */
export interface FilterSaid {
  readonly where: string;
  readonly counted?: { readonly left: number; readonly of: number };
  readonly unread?: string;
}

/**
 * The line a filtered grid carries beside its title: that it is filtered, by what, to how many rows, and the button
 * that takes the filter off, which is the pointer's `filter off`. Nothing where the grid has no filter from the line.
 */
export function GridFilterLine({ said, grid, onOff }: {
  readonly said: FilterSaid | undefined;
  /** What the grid's rows are called, for the button's name (`the readings`). */
  readonly grid: string;
  readonly onOff: () => void;
}) {
  if (said === undefined) return null;
  const { where, counted, unread } = said;
  return (
    <Group gap="xs" wrap="wrap" className="qube-grid-filter" data-testid="grid-filter">
      <Text size="xs" role="status" {...(unread === undefined ? {} : { c: 'red' })}>
        Filter: {where}{counted === undefined ? '' : ` · ${counted.left} of ${counted.of}`}{unread === undefined ? '' : ` · ${unread}`}
      </Text>
      <Button size="compact-xs" variant="default" aria-label={`Take the filter off ${grid}`} title="Show every row again (filter off)." onClick={onOff}>Filter off</Button>
    </Group>
  );
}
