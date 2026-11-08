import { useCallback, useEffect, useId, useMemo, useRef, type Ref } from 'react';
import { Button, Group, Table, Tabs, Text } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { WorkbenchGrid } from '../../platform/grid/WorkbenchGrid';
import type { EffectiveCsa, XvaWorkspaceView } from './contracts';
import { csaFields, parseRecovery } from './editing';
import { CommitField } from './fields';
import { partHeight } from './layout';
import { changes, differs, originalMessage } from './original';

// The shared input tabs (ADR-0010 §1, §8): Netting and CSA. The server holds every term and every rule; these send what
// the user changed and show what the server answers, FVA's Imperfect column included (§5), which the page never decides.

const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
const money = (v: unknown) => amount.format(v as number);

type CsaRow = EffectiveCsa;

/** FVA's standing on a CSA, with its reason, as the server decided it. */
export function standing(row: { readonly membership?: Readonly<Record<string, { readonly in: boolean; readonly reason: string }>> }, measure: string): string | undefined {
  const m = row.membership?.[measure];
  return m === undefined ? undefined : `${m.in ? 'Imperfect' : 'Perfect'} · ${m.reason}`;
}

const term = (name: keyof CsaRow & string, header: string, type: GridColumn<CsaRow>['type'], width: number, extra: Partial<GridColumn<CsaRow>> = {}): GridColumn<CsaRow> => ({
  name, header, type, width, value: (row) => row[name] ?? undefined, ...extra,
});

/** Made once (ExGrid ADR-0003): the CSA's general terms, then each party's, then FVA's derived column (ADR-0010 §8). */
const csaColumns: readonly GridColumn<CsaRow>[] = [
  term('csaId', 'CSA', 'text', 88),
  term('type', 'Type', 'text', 84),
  term('currency', 'Ccy', 'text', 80, { editable: true }),
  term('curve', 'Curve', 'text', 104, { editable: true }),
  term('fvaCurve', 'FVA curve', 'text', 112, { editable: true }),
  term('frequency', 'Frequency', 'text', 124, { editable: true }),
  term('exchange', 'Exchange', 'boolean', 116, { editable: true }),
  term('oneWay', 'One-way', 'boolean', 108, { editable: true }),
  term('counterpartyThreshold', 'Cpty threshold', 'number', 156, { editable: true, format: money }),
  term('counterpartyMta', 'Cpty MTA', 'number', 128, { editable: true, format: money }),
  term('counterpartyCurrency', 'Cpty ccy', 'text', 112, { editable: true }),
  term('selfThreshold', 'Self threshold', 'number', 156, { editable: true, format: money }),
  term('selfMta', 'Self MTA', 'number', 128, { editable: true, format: money }),
  term('selfCurrency', 'Self ccy', 'text', 108, { editable: true }),
  { name: 'fva', header: 'FVA imperfect', type: 'text', width: 360, value: (row) => standing(row, 'fva'), format: (v) => String(v) },
];

// A term that differs from the source's is marked and described (ADR-0010 §2); one set back to it is not.
const csaCellState = (row: CsaRow, col: GridColumn<CsaRow>): CellState => (differs(row, col.name) ? 'modified' : 'normal');
const csaCellMessage = (row: CsaRow, col: GridColumn<CsaRow>): string | undefined => originalMessage(row, col.name, row[col.name as keyof CsaRow]);

/** The input tabs every page has (ADR-0010 §1); the tabs a selected measure registers join them as they arrive. */
export type InputTab = 'netting' | 'csa';

/**
 * The tabs, one open at a time and collapsible to their tab row (ADR-0010 §1). The page keeps which tab is open,
 * whether it is open and its height, so that a command opens a tab as a press does.
 */
export function XvaInputs({ ws, push, onIssue, tab, onTab, open, onOpen, height, focusRequest }: {
  readonly ws: XvaWorkspaceView;
  readonly push: (entity: string, field: string, value: unknown) => void;
  readonly onIssue: (message: string | undefined) => void;
  readonly tab: InputTab;
  /** A tab pressed, which opens the tabs too. */
  readonly onTab: (tab: InputTab) => void;
  readonly open: boolean;
  readonly onOpen: (open: boolean) => void;
  /** The open tab's height in pixels, which the handle under it sets. */
  readonly height: number;
  /** Moves each time a command opens a tab, which puts the keyboard in it (ADR-0010 §9); 0 until one has. */
  readonly focusRequest: number;
}) {
  // The keyboard lands where the tab is worked: the CVA recovery on Netting, the terms grid on CSA. The tab's content
  // mounts in the same commit as the command's change, so its refs are set when this runs.
  const bodyId = useId();
  const recovery = useRef<HTMLInputElement>(null);
  const csaGrid = useRef<ExGridApi<CsaRow>>(null);
  useEffect(() => {
    if (focusRequest === 0) return;
    if (tab === 'netting') recovery.current?.focus();
    else csaGrid.current?.focus();
  // Only a command's request moves the keyboard; a tab pressed keeps it where the press left it.
  }, [focusRequest]);

  // Each tab counts the values in it that differ from Original (ADR-0010 §1, §2).
  const csaChanges = changes(ws.csas);
  const nettingChanges = changes([ws.credit]);
  const count = (n: number) => (n === 0 ? '' : ` ●${n}`);

  return (
    <Tabs value={tab} onChange={(value) => { if (value === 'netting' || value === 'csa') onTab(value); }} keepMounted={false}
      className="qwb-xva-inputs" data-open={open}>
      <Group gap="xs" wrap="nowrap" className="qwb-xva-inputs-row">
        <Tabs.List className="qwb-xva-inputs-tabs" aria-label="Inputs">
          <Tabs.Tab value="netting">Netting{count(nettingChanges)}</Tabs.Tab>
          <Tabs.Tab value="csa">CSA{count(csaChanges)}</Tabs.Tab>
        </Tabs.List>
        <Button size="compact-xs" variant="subtle" ml="auto" aria-expanded={open} aria-controls={bodyId}
          onClick={() => onOpen(!open)}>{open ? 'Hide inputs' : 'Show inputs'}</Button>
      </Group>
      {open && (
        <div id={bodyId} className="qwb-xva-inputs-body" data-testid="xva-inputs-body" style={partHeight(height)}>
          <Tabs.Panel value="netting" pt="xs">
            <div className="qwb-xva-scroll">
              <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="sm" fz="sm" aria-label="Netting" className="qwb-xva-netting">
                <Table.Thead>
                  <Table.Tr><Table.Th>Main</Table.Th><Table.Th>Netting set</Table.Th><Table.Th>Agreement</Table.Th><Table.Th>Enforceable</Table.Th><Table.Th>CVA recovery (CVA)</Table.Th></Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  <Table.Tr>
                    <Table.Td>Yes</Table.Td>
                    <Table.Td>{ws.agreement.nettingSetId}</Table.Td>
                    <Table.Td>{ws.agreement.maId}</Table.Td>
                    <Table.Td>{ws.agreement.enforceable ? 'Yes' : 'No'}</Table.Td>
                    <Table.Td>
                      <CommitField label="CVA recovery" placeholder={`empty: CDS ${ws.credit.cdsRecovery}`}
                        value={ws.credit.cvaRecovery == null ? '' : String(ws.credit.cvaRecovery)} modified={differs(ws.credit, 'cvaRecovery')} description={originalMessage(ws.credit, 'cvaRecovery', ws.credit.cvaRecovery)}
                        parse={parseRecovery} onIssue={onIssue} inputRef={recovery}
                        onCommit={(value) => push(`netting:${ws.agreement.nettingSetId}`, 'cvaRecovery', value)} />
                    </Table.Td>
                  </Table.Tr>
                </Table.Tbody>
              </Table>
            </div>
          </Tabs.Panel>
          <Tabs.Panel value="csa" pt="xs">
            {ws.csas.length === 0
              ? <Text size="sm">{ws.agreement.maId} has no CSA: its trades are uncollateralised ({ws.agreement.noCsa}).</Text>
              : <CsaGrid csas={ws.csas} push={push} height={height} gridRef={csaGrid} />}
          </Tabs.Panel>
        </div>
      )}
    </Tabs>
  );
}

/** The CSA terms, as high as their rows within the tab's height (the panel's top padding, 10px, is outside it). */
function CsaGrid({ csas, push, height, gridRef }: {
  readonly csas: readonly EffectiveCsa[];
  readonly push: (entity: string, field: string, value: unknown) => void;
  readonly height: number;
  readonly gridRef: Ref<ExGridApi<CsaRow>>;
}) {
  const source = useMemo(() => GridSource.from<CsaRow>([], { rowKey: (row) => row.csaId }), []);
  useEffect(() => { source.replaceAll(csas); }, [csas, source]);

  const onEdit = useCallback((intent: GridEditIntent<CsaRow>) => {
    const parse = csaFields[intent.column];
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    push(`csa:${intent.row.csaId}`, intent.column, parsed.value);
  }, [push]);

  return (
    <div aria-label="CSA terms">
      <WorkbenchGrid<CsaRow> ref={gridRef} columns={csaColumns} source={source} onEdit={onEdit} cellState={csaCellState} cellMessage={csaCellMessage}
        density="compact" viewportWidth="stretch" viewportHeight={Math.max(76, Math.min(48 + csas.length * 28, height - 10))} pinnedColumnCount={1} locale="en-US" />
    </div>
  );
}
