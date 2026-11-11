import { useCallback, useEffect, useMemo, useRef } from 'react';
import { actionColumn, type GridActionEvent, type GridColumn } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { QubeGrid } from './QubeGrid';
import type { Dataset, ViewAction, ViewColumn } from '../contracts';
import { amountTone, formatAmount } from './amounts';
import { useColumnWidths } from './columnWidths';

// The adapter between a declarative grid component and ExGrid (ADR-0003 §3). ExGrid draws, scrolls,
// selects and copies; it decides nothing. Rows keep their identity, so the grid compares them cheaply.

type Row = Readonly<Record<string, unknown>>;

export const ACTIONS_COLUMN = 'Actions';

const text = (value: unknown): string => (value === null || value === undefined ? '' : String(value));

/**
 * Maps a declared column onto an ExGrid column. A null number stays blank; it is never shown as 0. A `currency`
 * column is an amount of money, as its view declares it, never guessed from its title.
 */
export function gridColumnOf(column: ViewColumn, dataset: Dataset): GridColumn<Row> {
  const declared = dataset.columns.find((c) => c.field === column.field);
  if (declared === undefined) throw new Error(`Dataset "${dataset.title}" has no field ${column.field}.`);
  const numeric = (column.format === 'number' || column.format === 'currency') && (declared.type === 'number' || declared.type === 'integer');
  const amount = numeric && column.format === 'currency';
  return {
    name: column.field,
    header: column.label,
    type: numeric ? 'number' : 'text',
    value: (row: Row) => row[column.field] ?? undefined,
    format: amount ? formatAmount : text,
    ...(amount ? { tone: amountTone } : {}),
    width: numeric ? 110 : column.format === 'status' ? 110 : 140,
  };
}

export function DatasetGrid({ grid: gridKey, dataset, columns, actions, onAction, registerFocus, label }: {
  /** Names this grid among its panel's, for the widths the user drags. */
  readonly grid: string;
  readonly dataset: Dataset;
  readonly columns: readonly ViewColumn[];
  readonly actions: readonly ViewAction[];
  readonly onAction: (action: ViewAction, row: Row) => void;
  readonly registerFocus?: (focus: (() => void) | undefined) => void;
  readonly label: string;
}) {
  const grid = useRef<ExGridApi<Row>>(null);

  const gridColumns = useMemo(() => {
    const mapped = columns.map((c) => gridColumnOf(c, dataset));
    return actions.length === 0
      ? mapped
      : [...mapped, actionColumn<Row>(ACTIONS_COLUMN, actions.map((a) => ({ name: a.actionId, label: a.label })), { width: 260 })];
  }, [columns, actions, dataset]);
  const widths = useColumnWidths(`view/${gridKey}`, gridColumns);

  const handleAction = useCallback((event: GridActionEvent<Row>) => {
    const action = actions.find((a) => a.actionId === event.action);
    if (action === undefined) throw new Error(`The grid reported an action it was not given: ${event.action}.`);
    onAction(action, event.row);
  }, [actions, onAction]);

  useEffect(() => {
    registerFocus?.(() => grid.current?.focus());
    return () => registerFocus?.(undefined);
  }, [registerFocus]);

  const height = Math.min(36 + dataset.rows.length * 28, 480);

  return (
    <div aria-label={label} data-rows={dataset.rows.length}>
      <QubeGrid<Row>
        ref={grid}
        columns={widths.columns}
        onColumnWidthChanged={widths.onColumnWidthChanged}
        rows={dataset.rows}
        onAction={handleAction}
        density="compact"
        viewportWidth="stretch"
        viewportHeight={height}
        locale="en-US"
      />
    </div>
  );
}
