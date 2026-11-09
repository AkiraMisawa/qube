import { useCallback, useEffect, useId, useMemo, useRef, type Ref } from 'react';
import { Button, Group, Table, Tabs, Text, useMantineTheme } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridContextMenuContext, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { WorkbenchGrid } from '../../platform/grid/WorkbenchGrid';
import type { Agreement, ChangesList, EffectiveCsa, RegisteredMeasure, XvaWorkspaceView } from './contracts';
import { XvaChanges, type ChangeActions } from './XvaChanges';
import { VARY_COMMAND, type VaryTarget } from './VaryDialog';
import { csaFields, parseRecovery } from './editing';
import { CommitField } from './fields';
import { compactGridHeight, GRID_FRAME, partHeight, pixels } from './layout';
import { declaring, membershipColumn } from './membership';
import { changes, differs, originalMessage } from './original';

// The shared input tabs (ADR-0010 §1, §8): Netting and CSA. The server holds every term and every rule; these send what
// the user changed and show what the server answers. What a measure adds to them is its registration's (§10): a field
// it needs, such as CVA's recovery, shows while the measure is selected, and a rule that reads CSAs, such as FVA's
// imperfect-CSA rule (§5), has its column on the CSA grid, which the page never decides.

const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
const money = (v: unknown) => amount.format(v as number);

type CsaRow = EffectiveCsa;

const term = (name: keyof CsaRow & string, header: string, type: GridColumn<CsaRow>['type'], width: number, extra: Partial<GridColumn<CsaRow>> = {}): GridColumn<CsaRow> => ({
  name, header, type, width, value: (row) => row[name] ?? undefined, ...extra,
});

const csaId = term('csaId', 'CSA', 'text', 88);
const csaType = term('type', 'Type', 'text', 84);
/** Made once (ExGrid ADR-0003): the CSA's general terms after its agreement, then each party's (ADR-0010 §8). */
const csaTerms: readonly GridColumn<CsaRow>[] = [
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
];

/**
 * The CSA grid's columns (ADR-0010 §8): Main (the CSA a new trade takes), the ID and type, the master agreement, the
 * terms, and a column per selected measure whose rule reads CSAs. Main and the agreement are the source's, read-only.
 */
const csaColumnsOf = (agreement: Agreement, reading: readonly RegisteredMeasure[]): readonly GridColumn<CsaRow>[] => [
  { name: 'main', header: 'Main', type: 'text', width: 80, value: (row) => (row.csaId === agreement.mainCsaId ? 'Yes' : 'No'), format: (v) => String(v) },
  csaId, csaType,
  { name: 'ma', header: 'MA', type: 'text', width: 96, value: () => agreement.maId, format: (v) => String(v) },
  ...csaTerms,
  ...reading.map((m) => membershipColumn<CsaRow>(m)),
];

// A term that differs from the source's is marked and described (ADR-0010 §2); one set back to it is not.
const csaCellState = (row: CsaRow, col: GridColumn<CsaRow>): CellState => (differs(row, col.name) ? 'modified' : 'normal');
const csaCellMessage = (row: CsaRow, col: GridColumn<CsaRow>): string | undefined => originalMessage(row, col.name, row[col.name as keyof CsaRow]);

/** The input tabs every page has (ADR-0010 §1), those a selected measure registers, spot IM's IM tab (ADR-0012 §3), and
 * Changes, last, which lists every change and case (ADR-0010 §3's note of 2026-11-08). */
export type InputTab = 'netting' | 'csa' | 'im' | 'changes';

/** The Changes tab, last in the row. */
export const CHANGES_TAB = 'changes' satisfies InputTab;

/** Spot IM's own tab, as its input names it (ADR-0012 §3). */
export const IM_TAB = 'im' satisfies InputTab;

/** Each tab's title, as its tab and the page's words name it. */
export const TAB_TITLES: Readonly<Record<InputTab, string>> = { netting: 'Netting', csa: 'CSA', im: 'IM', changes: 'Changes' };

/** Whether a tab is shown: Netting, CSA and Changes always, a measure's own while a selected measure needs it (ADR-0010 §10). */
export const tabShown = (ws: Pick<XvaWorkspaceView, 'inputs'>, tab: InputTab): boolean =>
  tab === 'netting' || tab === 'csa' || tab === CHANGES_TAB || ws.inputs.some((i) => i.tab === tab);

/** The tab that shows for the one chosen: itself, or Netting where it is a measure's own and that measure went. */
export const shownTab = (ws: Pick<XvaWorkspaceView, 'inputs'>, tab: InputTab): InputTab => (tabShown(ws, tab) ? tab : 'netting');

/**
 * The tabs, one open at a time and collapsible to their tab row (ADR-0010 §1). The page keeps which tab is open,
 * whether it is open and its height, so that a command opens a tab as a press does.
 */
export function XvaInputs({ ws, push, onIssue, tab, onTab, open, onOpen, height, focusRequest, changeList, changeActions, onVary, commandLabel }: {
  readonly ws: XvaWorkspaceView;
  /** The Changes tab's list, read while the tab is open; none until it has been read. */
  readonly changeList: ChangesList | undefined;
  readonly changeActions: ChangeActions;
  /** *Vary values…* on an editable cell's Context Menu (ADR-0010 §3), and what the menu calls the page's commands. */
  readonly onVary: (target: VaryTarget) => void;
  readonly commandLabel: (id: string) => string | undefined;
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
  const imCsas = useRef<HTMLDivElement>(null);
  const changesBody = useRef<HTMLDivElement>(null);
  const gap = pixels(useMantineTheme().spacing.xs);
  // The fields of these tabs the selected measures need (§10): CVA's recovery while CVA is selected.
  const needed = (tab: InputTab, field: string) => ws.inputs.some((i) => i.tab === tab && i.field === field);
  const recoveryShown = needed('netting', 'cvaRecovery');
  // The CSA grid's columns, made again only when the agreement or the measures reading CSAs change (ExGrid ADR-0003).
  const reading = declaring(ws.measures, ws.selectedMeasures).filter((m) => ws.csas.some((c) => c.membership?.[m.id] !== undefined));
  const columnsKey = [ws.agreement.maId, String(ws.agreement.mainCsaId), ...reading.map((m) => m.id)].join('|');
  const columnsNow = useRef({ agreement: ws.agreement, reading });
  columnsNow.current = { agreement: ws.agreement, reading };
  const csaColumns = useMemo(() => csaColumnsOf(columnsNow.current.agreement, columnsNow.current.reading), [columnsKey]);
  // A measure's own tab shows while it is selected; the tab open when it went is Netting again.
  const imShown = tabShown(ws, IM_TAB);
  const shown = shownTab(ws, tab);
  useEffect(() => {
    if (focusRequest === 0) return;
    if (shown === 'netting') recovery.current?.focus();
    else if (shown === IM_TAB) imCsas.current?.focus();
    else if (shown === CHANGES_TAB) changesBody.current?.focus();
    else csaGrid.current?.focus();
  // Only a command's request moves the keyboard; a tab pressed keeps it where the press left it.
  }, [focusRequest]);

  // Each tab counts the values in it that differ from Original (ADR-0010 §1, §2).
  const csaChanges = changes(ws.csas);
  const nettingChanges = changes([ws.credit]);
  const count = (n: number) => (n === 0 ? '' : ` ●${n}`);

  return (
    <Tabs value={shown} onChange={(value) => { const chosen = Object.keys(TAB_TITLES).find((t) => t === value); if (chosen !== undefined) onTab(chosen as InputTab); }} keepMounted={false}
      className="qwb-xva-inputs" data-open={open}>
      <Group gap="xs" wrap="nowrap" className="qwb-xva-inputs-row">
        <Tabs.List className="qwb-xva-inputs-tabs" aria-label="Inputs">
          <Tabs.Tab value="netting">{TAB_TITLES.netting}{count(nettingChanges)}</Tabs.Tab>
          <Tabs.Tab value="csa">{TAB_TITLES.csa}{count(csaChanges)}</Tabs.Tab>
          {imShown && <Tabs.Tab value={IM_TAB}>{TAB_TITLES.im}</Tabs.Tab>}
          <Tabs.Tab value={CHANGES_TAB}>{TAB_TITLES.changes}{count(ws.changes)}</Tabs.Tab>
        </Tabs.List>
        <Button size="compact-xs" variant="subtle" ml="auto" aria-expanded={open} aria-controls={bodyId}
          onClick={() => onOpen(!open)}>{open ? 'Hide inputs' : 'Show inputs'}</Button>
      </Group>
      {open && (
        <div id={bodyId} className="qwb-xva-inputs-body" data-testid="xva-inputs-body" style={partHeight(height)}>
          <Tabs.Panel value="netting" pt="xs">
            <div className="qwb-xva-scroll">
              <Table withTableBorder withColumnBorders verticalSpacing="xs" horizontalSpacing="sm" fz="sm" aria-label="Netting" className="qwb-xva-netting">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Main</Table.Th><Table.Th>Netting set</Table.Th><Table.Th>Agreement</Table.Th><Table.Th>Enforceable</Table.Th>
                    {recoveryShown && <Table.Th>CVA recovery (CVA)</Table.Th>}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  <Table.Tr>
                    <Table.Td>Yes</Table.Td>
                    <Table.Td>{ws.agreement.nettingSetId}</Table.Td>
                    <Table.Td>{ws.agreement.maId}</Table.Td>
                    <Table.Td>{ws.agreement.enforceable ? 'Yes' : 'No'}</Table.Td>
                    {recoveryShown && <Table.Td>
                      <CommitField label="CVA recovery" placeholder={`empty: CDS ${ws.credit.cdsRecovery}`}
                        value={ws.credit.cvaRecovery == null ? '' : String(ws.credit.cvaRecovery)} modified={differs(ws.credit, 'cvaRecovery')} description={originalMessage(ws.credit, 'cvaRecovery', ws.credit.cvaRecovery)}
                        parse={parseRecovery} onIssue={onIssue} inputRef={recovery}
                        onCommit={(value) => push(`netting:${ws.agreement.nettingSetId}`, 'cvaRecovery', value)} />
                    </Table.Td>}
                  </Table.Tr>
                </Table.Tbody>
              </Table>
            </div>
          </Tabs.Panel>
          <Tabs.Panel value="csa" pt="xs">
            {ws.csas.length === 0
              ? <Text size="sm">{ws.agreement.maId} has no CSA: its trades are uncollateralised ({ws.agreement.noCsa}).</Text>
              : <CsaGrid csas={ws.csas} columns={csaColumns} push={push} height={height - gap - GRID_FRAME} gridRef={csaGrid} onVary={onVary} commandLabel={commandLabel} />}
          </Tabs.Panel>
          {imShown && (
            <Tabs.Panel value={IM_TAB} pt="xs">
              <ImCsas ws={ws} focusRef={imCsas} />
            </Tabs.Panel>
          )}
          <Tabs.Panel value={CHANGES_TAB} pt="xs">
            <XvaChanges list={changeList} actions={changeActions} focusRef={changesBody} />
          </Tabs.Panel>
        </div>
      )}
    </Tabs>
  );
}

const directionTitle = (direction: string) => (direction === 'collect' ? 'IM-R, collected' : direction === 'post' ? 'IM-P, posted' : direction);

/**
 * The IM tab (ADR-0012 §3): the counterparty's IM CSAs, read-only, the source's. A regime applies from its date: a
 * trade that started before it is out of that scope. The SIMM version names the table SIMM computes with. One tab stop,
 * so `open im` puts the keyboard here and the arrow keys scroll it.
 */
function ImCsas({ ws, focusRef }: { readonly ws: XvaWorkspaceView; readonly focusRef: Ref<HTMLDivElement> }) {
  const csas = ws.agreement.imCsas;
  if (csas.length === 0)
    return <Text size="sm" tabIndex={-1} ref={focusRef}>{ws.registration.key} has no IM CSA: it exchanges no initial margin, so spot IM has no scope.</Text>;
  return (
    <div className="qwb-xva-scroll" tabIndex={0} ref={focusRef} aria-label="IM CSAs">
      <Table withTableBorder withColumnBorders verticalSpacing="xs" horizontalSpacing="sm" fz="sm" aria-label="IM CSAs">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>IM CSA</Table.Th><Table.Th>Direction</Table.Th><Table.Th>Regimes, from</Table.Th>
            <Table.Th>Calculation ccy</Table.Th><Table.Th>SIMM version</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {csas.map((c) => (
            <Table.Tr key={c.imCsaId}>
              <Table.Td>{c.imCsaId}</Table.Td>
              <Table.Td>{directionTitle(c.direction)}</Table.Td>
              <Table.Td>{c.regimes.map((r) => `${r.regime} ${r.since}`).join(' · ')}</Table.Td>
              <Table.Td>{c.calculationCurrency}</Table.Td>
              <Table.Td>{c.simmVersion}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </div>
  );
}

/**
 * The CSA terms, as high as their rows within `height`, what the tab's height leaves under the panel's top gap and the
 * grid's frame. It shows no Selection Summary: a sum or an average of terms in different currencies is no figure.
 */
function CsaGrid({ csas, columns, push, height, gridRef, onVary, commandLabel }: {
  readonly onVary: (target: VaryTarget) => void;
  readonly commandLabel: (id: string) => string | undefined;
  readonly csas: readonly EffectiveCsa[];
  readonly columns: readonly GridColumn<CsaRow>[];
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
  // Vary values… on a term the user may type (ADR-0010 §3); Main, the ID, the type and the agreement are the source's.
  const columnsNow = useRef(columns);
  columnsNow.current = columns;
  const varyNow = useRef(onVary);
  varyNow.current = onVary;
  const contextCommands = useCallback((context: GridContextMenuContext<CsaRow>) => {
    const column = columnsNow.current.find((c) => c.name === context.column);
    return column?.editable === true
      ? [{ id: VARY_COMMAND, enabled: true, invoke: () => varyNow.current({ entity: `csa:${context.row.csaId}`, field: context.column, title: `${context.row.csaId} ${column.header}` }) }]
      : [];
  }, []);

  return (
    <div aria-label="CSA terms">
      <WorkbenchGrid<CsaRow> ref={gridRef} columns={columns} source={source} onEdit={onEdit} cellState={csaCellState} cellMessage={csaCellMessage}
        contextCommands={contextCommands} commandLabel={commandLabel}
        density="compact" viewportWidth="stretch" viewportHeight={Math.max(compactGridHeight(1), Math.min(compactGridHeight(csas.length), height))} pinnedColumnCount={2} showSelectionSummary={false} locale="en-US" />
    </div>
  );
}
