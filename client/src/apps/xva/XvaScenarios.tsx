import { useCallback, useEffect, useMemo, useRef, useState, type Ref } from 'react';
import { Button, Checkbox, Group, Modal, Stack, Table, Text, TextInput } from '@mantine/core';
import { GridSource, type CellState, type GridClearIntent, type GridColumn, type GridEditIntent, type GridPasteIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { WorkbenchGrid } from '../../platform/grid/WorkbenchGrid';
import type { ScenarioView, ShiftEdit, ShiftRow } from './contracts';
import { clearedEdits, pastedEdits, shiftGridRows, type ShiftColumn, type ShiftGridRow } from './scenarios';

// The Scenarios input tab (ADR-0010 §4 and its note of 2026-11-09): every shift in one grid, rows sharing a name being
// one scenario, typed or pasted many rows at a time onto the blank rows it ends in; beside it the scenarios with their
// shift counts and an include box, Duplicate and Remove. The server reads every cell as its column reads it and says
// what a row lacks; nothing here decides what a value means.

/** What the tab asks the page to do; each goes after the edits already sent (spine 6), and a refusal throws its reason. */
export interface ScenarioActions {
  readonly edit: (rows: readonly ShiftEdit[]) => Promise<void>;
  readonly include: (name: string, included: boolean) => void;
  readonly duplicate: (name: string, copy: string) => Promise<void>;
  readonly remove: (name: string) => void;
  /** Says why a paste or a clear wrote nothing, where the grid's own refusal says only that it did not. */
  readonly notify: (message: string) => void;
}

// A field the server left out is a Blank cell, as the grid takes `undefined`.
const text = (pick: (s: ShiftRow) => string | null | undefined) => (row: ShiftGridRow) => {
  const value = row.shift === undefined ? undefined : pick(row.shift);
  return value === null ? undefined : value;
};
const said = (v: string | null | undefined): v is string => v !== undefined && v !== null;
const column = (name: ShiftColumn | 'issue', header: string, width: number, value: (row: ShiftGridRow) => unknown, editable = true): GridColumn<ShiftGridRow> =>
  ({ name, header, type: 'text', width, value, editable, queryable: false });

/** Made once (ExGrid ADR-0003), in the order a block is pasted in: the order of `SHIFT_COLUMNS`, then the row's issue. */
const COLUMNS: readonly GridColumn<ShiftGridRow>[] = [
  column('scenario', 'Scenario', 120, text((s) => s.scenario)),
  column('category', 'Category', 96, text((s) => s.category)),
  column('index', 'Index', 120, text((s) => s.index)),
  column('factor', 'Risk factor', 112, text((s) => s.factor)),
  column('type', 'Shift type', 104, text((s) => s.type)),
  column('size', 'Size', 88, text((s) => s.sizeText)),
  column('description', 'Description', 200, text((s) => s.description)),
  column('issue', 'Issue', 320, text((s) => s.issue), false),
];

// A row that cannot be priced marks the cell its issue is of, and says it there and in its Issue.
const issueAt = (row: ShiftGridRow, col: GridColumn<ShiftGridRow>): string | undefined =>
  row.shift === undefined || !said(row.shift.issue) || (col.name !== row.shift.issueColumn && col.name !== 'issue') ? undefined : row.shift.issue;
const cellState = (row: ShiftGridRow, col: GridColumn<ShiftGridRow>): CellState => (issueAt(row, col) === undefined ? 'normal' : 'error');
const cellMessage = (row: ShiftGridRow, col: GridColumn<ShiftGridRow>): string | undefined => {
  const issue = issueAt(row, col);
  return issue === undefined || col.name === 'issue' ? undefined : `Row ${row.position}: ${issue}.`;
};

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

export function XvaScenarios({ shifts, scenarios, actions, height, gridRef, commandLabel }: {
  readonly shifts: readonly ShiftRow[];
  readonly scenarios: readonly ScenarioView[];
  readonly actions: ScenarioActions;
  /** The tab's height in pixels, which the grid fills. */
  readonly height: number;
  /** The grid, for `open scenarios` to put the keyboard in. */
  readonly gridRef: Ref<ExGridApi<ShiftGridRow>>;
  readonly commandLabel: (id: string) => string | undefined;
}) {
  const rows = useMemo(() => shiftGridRows(shifts), [shifts]);
  const source = useMemo(() => GridSource.from<ShiftGridRow>([], { rowKey: (row) => row.key }), []);
  useEffect(() => { source.replaceAll(rows); }, [rows, source]);
  const actionsNow = useRef(actions);
  actionsNow.current = actions;

  const onEdit = useCallback(async (intent: GridEditIntent<ShiftGridRow>) => {
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    try {
      await actionsNow.current.edit([{ id: intent.row.shift?.id ?? null, row: intent.row.position, cells: { [intent.column]: intent.value } }]);
    } catch (error) {
      intent.refuse(describe(error));
    }
  }, []);

  // A paste and a clear name positions of the order the grid drew; one taken against another order is refused, never
  // written onto other rows (ExGrid ADR-0011). The grid's order is the source's own: its columns are not queryable.
  const positioned = (version: number) => {
    const snapshot = source.snapshot();
    return snapshot.rowSequenceVersion === version ? snapshot.window : undefined;
  };
  const onPaste = useCallback(async (intent: GridPasteIntent) => {
    const drawn = positioned(intent.rowSequenceVersion);
    if (drawn === undefined) { intent.refuse(); actionsNow.current.notify('The grid moved under the paste, so nothing was pasted. Paste again.'); return; }
    const edits = pastedEdits(drawn, intent.plan.targets, (cell) => intent.valueFor(cell));
    if (edits.length === 0) return;
    try {
      await actionsNow.current.edit(edits);
    } catch (error) {
      intent.refuse();
      actionsNow.current.notify(`Nothing was pasted: ${describe(error)}`);
    }
  }, [source]);
  const onClear = useCallback((intent: GridClearIntent) => {
    const drawn = positioned(intent.rowSequenceVersion);
    if (drawn === undefined) { actionsNow.current.notify('The grid moved under Delete, so nothing was cleared. Select the cells again.'); return; }
    const edits = clearedEdits(drawn, intent.targets);
    if (edits.length > 0) actionsNow.current.edit(edits).catch((error: unknown) => actionsNow.current.notify(`Nothing was cleared: ${describe(error)}`));
  }, [source]);

  const [duplicating, setDuplicating] = useState<string | undefined>(undefined);
  const included = scenarios.filter((s) => s.included).length;

  return (
    <div className="qwb-xva-scenarios">
      <div className="qwb-xva-scenarios-grid" aria-label="Shifts">
        <WorkbenchGrid<ShiftGridRow> ref={gridRef} columns={COLUMNS} source={source} onEdit={onEdit} onPaste={onPaste} onClear={onClear} pasteMaySpill
          cellState={cellState} cellMessage={cellMessage} commandLabel={commandLabel} hideColumnMenu showSelectionSummary={false}
          density="compact" viewportWidth="stretch" viewportHeight={height} pinnedColumnCount={1} locale="en-US" />
      </div>
      <Stack gap={4} className="qwb-xva-scenarios-list">
        <Text size="xs" data-testid="scenarios-summary">
          {scenarios.length === 0 ? 'No scenario: Price prices the base market alone. Type or paste shifts into the grid, or use scenario.'
            : `${scenarios.length} scenario(s), ${included} included: Price prices the base market and ${included} more.`}
        </Text>
        {scenarios.length > 0 && (
          <Table withTableBorder withColumnBorders verticalSpacing={2} horizontalSpacing="xs" fz="sm" aria-label="Scenarios">
            <Table.Thead>
              <Table.Tr><Table.Th>Price</Table.Th><Table.Th>Scenario</Table.Th><Table.Th className="qwb-number">Shifts</Table.Th><Table.Th aria-label="Actions" /></Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {scenarios.map((s) => (
                <Table.Tr key={s.name} data-included={s.included}>
                  <Table.Td>
                    <Checkbox size="xs" checked={s.included} aria-label={`Include ${s.name}`} onChange={(e) => actions.include(s.name, e.currentTarget.checked)} />
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{s.name}</Text>
                    {said(s.issue) && <Text size="xs" c="red" data-testid="scenario-issue">{s.issue}</Text>}
                  </Table.Td>
                  <Table.Td className="qwb-number">{s.shifts}</Table.Td>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      <Button size="compact-xs" variant="default" onClick={() => setDuplicating(s.name)} aria-label={`Duplicate ${s.name}`}>Duplicate</Button>
                      <Button size="compact-xs" variant="default" color="red" onClick={() => actions.remove(s.name)} aria-label={`Remove ${s.name}`}>Remove</Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Stack>
      <DuplicateDialog name={duplicating} taken={scenarios.map((s) => s.name)} duplicate={actions.duplicate} onClose={() => setDuplicating(undefined)} />
    </div>
  );
}

/** *Duplicate*: the new name, typed; the server refuses one a scenario has, and the dialog says why and stays open. */
function DuplicateDialog({ name, taken, duplicate, onClose }: {
  readonly name: string | undefined;
  readonly taken: readonly string[];
  readonly duplicate: (name: string, copy: string) => Promise<void>;
  readonly onClose: () => void;
}) {
  const [copy, setCopy] = useState('');
  const [said, setSaid] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (name === undefined) return;
    // A free name to start from: the scenario's with the first number no scenario has.
    let n = 2;
    while (taken.some((t) => t.toLowerCase() === `${name}${n}`.toLowerCase())) n += 1;
    setCopy(`${name}${n}`);
    setSaid(undefined);
  // Only a new dialog starts the name again; the list moving under it keeps the typing.
  }, [name]);
  const submit = async () => {
    if (name === undefined) return;
    setBusy(true);
    try {
      await duplicate(name, copy);
      onClose();
    } catch (error) {
      setSaid(describe(error));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal opened={name !== undefined} onClose={onClose} title={`Duplicate ${name ?? ''}`} trapFocus transitionProps={{ duration: 0 }}>
      <form onSubmit={(e) => { e.preventDefault(); void submit(); }}>
        <Stack gap="sm">
          <TextInput data-autofocus label="New name" value={copy} onChange={(e) => setCopy(e.currentTarget.value)} error={said} />
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>Cancel</Button>
            <Button type="submit" loading={busy}>Duplicate</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
