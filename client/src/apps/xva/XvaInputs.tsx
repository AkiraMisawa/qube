import { useCallback, useEffect, useId, useMemo, useRef, type Ref, type RefObject } from 'react';
import { Button, Group, Stack, Table, Tabs, Text, useMantineTheme } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { GridSource, type CellState, type GridColumn, type GridContextMenuContext, type GridEditIntent } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import { filterGrid, heldView, menuView, placeFocus, selectRows, sortGrid } from '../../platform/grid/lineView';
import { GridFilterLine } from '../../platform/grid/GridFilterLine';
import { useHeldFilter } from '../../platform/grid/heldFilter';
import { useWorkspaceContext } from '../../platform/workspace/WorkspaceProvider';
import type { TableMenu } from '../../platform/commands/served';
import type { Screen } from '../../platform/commands/commands';
import type { Agreement, ChangesList, EffectiveCsa, RegisteredMeasure, XvaWorkspaceView } from './contracts';
import { creditChanges, emptyRecovery, nettingChanges, type CatalogueRead } from './credit';
import { XvaCredit } from './XvaCredit';
import { XvaSettings } from './XvaSettings';
import { XvaChanges, type ChangeActions } from './XvaChanges';
import { XvaScenarios, type ScenarioActions } from './XvaScenarios';
import type { ShiftGridRow } from './scenarios';
import { VARY_COMMAND, type VaryTarget } from './VaryDialog';
import { CSAS_TABLE, csaFields, parseRecovery, type PushEdit, type SetText } from './editing';
import { CommitField } from './fields';
import { compactGridHeight, GRID_FRAME, partHeight, pixels } from './layout';
import { declaring, membershipColumn } from './membership';
import { changes, differs, originalMessage } from './original';
import { useColumnWidths } from '../../platform/grid/columnWidths';

// The shared input tabs (ADR-0010 §1, §8): Netting and CSA. The server holds every term and every rule; these send what
// the user changed and show what the server answers. What a measure adds to them is its registration's (§10): a field
// it needs, such as CVA's recovery, shows while the measure is selected, and a rule that reads CSAs, such as FVA's
// imperfect-CSA rule (§5), has its column on the CSA grid, which the page never decides.

const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });
const money = (v: unknown) => amount.format(v as number);

type CsaRow = EffectiveCsa;
const csaKey = (row: CsaRow) => row.csaId;

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

/** The input tabs every page has (ADR-0010 §1), those a selected measure registers, CVA's Credit (§8's note of
 * 2026-11-09) and spot IM's IM tab (ADR-0012 §3), Scenarios, the market shifts Price prices beside the base market
 * (ADR-0010 §4), Settings, the selected modules' settings (§8), and Changes, last, which lists every change and case
 * (ADR-0010 §3's note of 2026-11-08). */
export type InputTab = 'netting' | 'csa' | 'credit' | 'im' | 'scenarios' | 'settings' | 'changes';

/** The Scenarios tab (ADR-0010 §4). */
export const SCENARIOS_TAB = 'scenarios' satisfies InputTab;

/** The Changes tab, last in the row. */
export const CHANGES_TAB = 'changes' satisfies InputTab;

/** Spot IM's own tab, as its input names it (ADR-0012 §3). */
export const IM_TAB = 'im' satisfies InputTab;

/** CVA's Credit tab, as its input names it (ADR-0010 §8's note of 2026-11-09). */
export const CREDIT_TAB = 'credit' satisfies InputTab;

/** The Settings tab, shown while a selected module has a setting a selected measure needs (ADR-0010 §8). */
export const SETTINGS_TAB = 'settings' satisfies InputTab;

/** Each tab's title, as its tab and the page's words name it. */
export const TAB_TITLES: Readonly<Record<InputTab, string>> = {
  netting: 'Netting', csa: 'CSA', credit: 'Credit', im: 'IM', scenarios: 'Scenarios', settings: 'Settings', changes: 'Changes',
};

/** Whether a tab is shown: Netting, CSA, Scenarios and Changes always, a measure's own while a selected measure needs it
 * (ADR-0010 §10), and Settings while a selected measure needs a setting (§8). */
export const tabShown = (ws: Pick<XvaWorkspaceView, 'inputs' | 'settings'>, tab: InputTab): boolean =>
  tab === 'netting' || tab === 'csa' || tab === SCENARIOS_TAB || tab === CHANGES_TAB
  || (tab === SETTINGS_TAB ? ws.settings.length > 0 : ws.inputs.some((i) => i.tab === tab));

/** The tab that shows for the one chosen: itself, or Netting where it is a measure's own and that measure went. */
export const shownTab = (ws: Pick<XvaWorkspaceView, 'inputs' | 'settings'>, tab: InputTab): InputTab => (tabShown(ws, tab) ? tab : 'netting');

/**
 * The tabs, one open at a time and collapsible to their tab row (ADR-0010 §1). The page keeps which tab is open,
 * whether it is open and its height, so that a command opens a tab as a press does.
 */
export function XvaInputs({ panelId, ws, push, setText, catalogue, onIssue, onGridEntered, onPlaced, tab, onTab, open, onOpen, height, focusRequest, changeList, changeActions, scenarioActions, onVary, commandLabel, csaMenu }: {
  readonly ws: XvaWorkspaceView;
  /** The panel the page is in, which keeps the widths the user dragged in its grids. */
  readonly panelId: string;
  /** A value as written, for the server to read as the cell reads it (a spread, a setting). */
  readonly setText: SetText;
  /** The CDS catalogue the Credit tab searches; none until it has been read, and why where it could not be. */
  readonly catalogue: CatalogueRead;
  /** The Changes tab's list, read while the tab is open; none until it has been read. */
  readonly changeList: ChangesList | undefined;
  readonly changeActions: ChangeActions;
  /** What the Scenarios tab's grid and list ask the page to do (ADR-0010 §4). */
  readonly scenarioActions: ScenarioActions;
  /** *Vary values…* on an editable cell's Context Menu (ADR-0010 §3), and what the menu calls the page's commands. */
  readonly onVary: (target: VaryTarget) => void;
  readonly commandLabel: (id: string) => string | undefined;
  /** What the CSAs' table offers in the CSA grid's Context Menu (ADR-0016 note 25), read when a menu opens. */
  readonly csaMenu: { readonly current: { readonly csas: TableMenu } };
  /** An edit of a field, titled as the screen names it: its row and its column's header, or its label. */
  readonly push: PushEdit;
  readonly onIssue: (message: string | undefined) => void;
  /** The keyboard entered a grid of a table here: what the command line is told is on screen, read when it opens (ADR-0016 note 16). */
  readonly onGridEntered: (screen: () => Screen | undefined) => void;
  /** A place was put in the CSA grid (`find`, ADR-0016 note 21): its tab is shown and the keyboard given to it, or why not is answered. */
  readonly onPlaced: () => string | undefined;
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
  const scenarioGrid = useRef<ExGridApi<ShiftGridRow>>(null);
  const creditType = useRef<HTMLSelectElement>(null);
  const firstSetting = useRef<HTMLInputElement>(null);
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
  const creditShown = tabShown(ws, CREDIT_TAB);
  const settingsShown = tabShown(ws, SETTINGS_TAB);
  const shown = shownTab(ws, tab);
  const tabList = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (focusRequest === 0) return;
    const worked: { focus(): void } | null = shown === 'netting' ? recovery.current
      : shown === IM_TAB ? imCsas.current
      : shown === CHANGES_TAB ? changesBody.current
      : shown === SCENARIOS_TAB ? scenarioGrid.current
      : shown === CREDIT_TAB ? creditType.current
      : shown === SETTINGS_TAB ? firstSetting.current
      : csaGrid.current;
    // A tab that draws nothing to work in, as Netting does while no measure reads the recovery and CSA where the
    // agreement has none, takes the keyboard on its own tab (ADR-0016 note 20): a place always puts it somewhere.
    const take = worked ?? tabList.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
    if (take === null || take === undefined) throw new Error(`The input tabs were asked to take the keyboard on ${shown}, and have drawn no tab for it.`);
    take.focus();
  // Only a command's request moves the keyboard; a tab pressed keeps it where the press left it.
  }, [focusRequest]);

  // Each tab counts the values in it that differ from Original (ADR-0010 §1, §2).
  const csaChanges = changes(ws.csas);
  const nettingCount = nettingChanges(ws.credit);
  const creditCount = creditChanges(ws.credit);
  const settingsCount = ws.settings.reduce((n, m) => n + m.settings.filter((x) => x.changed).length, 0);
  const count = (n: number) => (n === 0 ? '' : ` ●${n}`);

  return (
    <Tabs value={shown} onChange={(value) => { const chosen = Object.keys(TAB_TITLES).find((t) => t === value); if (chosen !== undefined) onTab(chosen as InputTab); }} keepMounted={false}
      className="qube-xva-inputs" data-open={open}>
      <Group gap="xs" wrap="nowrap" className="qube-xva-inputs-row">
        <Tabs.List ref={tabList} className="qube-xva-inputs-tabs" aria-label="Inputs">
          <Tabs.Tab value="netting">{TAB_TITLES.netting}{count(nettingCount)}</Tabs.Tab>
          <Tabs.Tab value="csa">{TAB_TITLES.csa}{count(csaChanges)}</Tabs.Tab>
          {creditShown && <Tabs.Tab value={CREDIT_TAB} {...(ws.credit.missing == null ? {} : { color: 'red', title: ws.credit.missing })}>{TAB_TITLES.credit}{count(creditCount)}{ws.credit.missing == null ? '' : ' · no CDS'}</Tabs.Tab>}
          {imShown && <Tabs.Tab value={IM_TAB}>{TAB_TITLES.im}</Tabs.Tab>}
          <Tabs.Tab value={SCENARIOS_TAB}>{TAB_TITLES.scenarios}{ws.scenarios.length === 0 ? '' : ` (${ws.scenarios.length})`}</Tabs.Tab>
          {settingsShown && <Tabs.Tab value={SETTINGS_TAB}>{TAB_TITLES.settings}{count(settingsCount)}</Tabs.Tab>}
          <Tabs.Tab value={CHANGES_TAB}>{TAB_TITLES.changes}{count(ws.changes)}</Tabs.Tab>
        </Tabs.List>
        <Button size="compact-xs" variant="subtle" ml="auto" className="qube-xva-inputs-toggle" aria-expanded={open} aria-controls={bodyId}
          onClick={() => onOpen(!open)}>{open ? 'Hide inputs' : 'Show inputs'}</Button>
      </Group>
      {open && (
        <div id={bodyId} className="qube-xva-inputs-body" data-testid="xva-inputs-body" style={partHeight(height)}>
          <Tabs.Panel value="netting" pt="xs">
            <div className="qube-xva-scroll">
              <Table withTableBorder withColumnBorders verticalSpacing="xs" horizontalSpacing="sm" fz="sm" aria-label="Netting" className="qube-xva-netting">
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
                      <CommitField label="CVA recovery" placeholder={emptyRecovery(ws.credit)}
                        value={ws.credit.cvaRecovery == null ? '' : String(ws.credit.cvaRecovery)} modified={differs(ws.credit, 'cvaRecovery')} description={originalMessage(ws.credit, 'cvaRecovery', ws.credit.cvaRecovery)}
                        parse={parseRecovery} onIssue={onIssue} inputRef={recovery}
                        onCommit={(value) => push(`netting:${ws.agreement.nettingSetId}`, 'cvaRecovery', value, `${ws.agreement.nettingSetId} CVA recovery`)} />
                    </Table.Td>}
                  </Table.Tr>
                </Table.Tbody>
              </Table>
            </div>
          </Tabs.Panel>
          <Tabs.Panel value="csa" pt="xs">
            {ws.csas.length === 0
              ? <Text size="sm">{ws.agreement.maId} has no CSA: its trades are uncollateralised ({ws.agreement.noCsa}).</Text>
              : <CsaGrid panelId={panelId} workspaceId={ws.workspaceId} revision={ws.revision} csas={ws.csas} columns={csaColumns} push={push} height={height - gap - GRID_FRAME} gridRef={csaGrid} onVary={onVary} commandLabel={commandLabel} menu={csaMenu} onGridEntered={onGridEntered} onPlaced={onPlaced} />}
          </Tabs.Panel>
          {creditShown && (
            <Tabs.Panel value={CREDIT_TAB} pt="xs">
              <XvaCredit panelId={panelId} credit={ws.credit} registration={ws.registration.key} catalogue={catalogue} push={push} setText={setText}
                onVary={onVary} commandLabel={commandLabel} focusRef={creditType} />
            </Tabs.Panel>
          )}
          {imShown && (
            <Tabs.Panel value={IM_TAB} pt="xs">
              <ImCsas ws={ws} focusRef={imCsas} />
            </Tabs.Panel>
          )}
          <Tabs.Panel value={SCENARIOS_TAB} pt="xs">
            <XvaScenarios panelId={panelId} shifts={ws.shifts} scenarios={ws.scenarios} actions={scenarioActions} height={height - gap - GRID_FRAME} gridRef={scenarioGrid} commandLabel={commandLabel} />
          </Tabs.Panel>
          {settingsShown && (
            <Tabs.Panel value={SETTINGS_TAB} pt="xs">
              <XvaSettings settings={ws.settings} measures={ws.measures} push={push} setText={setText} focusRef={firstSetting} />
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
    <div className="qube-xva-scroll" tabIndex={0} ref={focusRef} aria-label="IM CSAs">
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
function CsaGrid({ panelId, workspaceId, revision, csas, columns: declared, push, height, gridRef, onVary, commandLabel, menu, onGridEntered, onPlaced }: {
  /** The panel the page is in, which keeps the widths the user dragged in its grids. */
  readonly panelId: string;
  /** The workspace the CSAs are of, and the revision the page shows of it. */
  readonly workspaceId: string;
  readonly revision: number;
  readonly onVary: (target: VaryTarget) => void;
  readonly commandLabel: (id: string) => string | undefined;
  readonly menu: { readonly current: { readonly csas: TableMenu } };
  readonly csas: readonly EffectiveCsa[];
  readonly columns: readonly GridColumn<CsaRow>[];
  /** An edit of a field, titled as the screen names it: its row and its column's header, or its label. */
  readonly push: PushEdit;
  readonly height: number;
  readonly gridRef: RefObject<ExGridApi<CsaRow> | null>;
  readonly onGridEntered: (screen: () => Screen | undefined) => void;
  readonly onPlaced: () => string | undefined;
}) {
  const source = useMemo(() => GridSource.from<CsaRow>([], { rowKey: csaKey }), []);
  const { columns, onColumnWidthChanged } = useColumnWidths(panelId, 'csas', declared);
  const { api } = useWorkspaceContext();
  const heldGrid = useMemo(() => ({ api, workspaceId, table: CSAS_TABLE, revision }), [api, workspaceId, revision]);
  // Filtered from the line (ADR-0016 note 41), the grid shows the CSAs the server says its conditions leave.
  const { rows: shown, said: filter } = useHeldFilter(source, heldGrid, csas, csaKey);
  useEffect(() => { source.replaceAll(shown); }, [shown, source]);

  const onEdit = useCallback((intent: GridEditIntent<CsaRow>) => {
    const parse = csaFields[intent.column];
    if (parse === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const parsed = parse(intent.value);
    if (!parsed.ok) { intent.refuse(parsed.message); return; }
    const header = declared.find((c) => c.name === intent.column)?.header;
    if (header === undefined) throw new Error(`The CSA grid has an editable column ${intent.column} with no header.`);
    push(`csa:${intent.row.csaId}`, intent.column, parsed.value, `${intent.row.csaId} ${header}`);
  }, [push, declared]);
  // Vary values… on a term the user may type (ADR-0010 §3); Main, the ID, the type and the agreement are the source's.
  const columnsNow = useRef(columns);
  columnsNow.current = columns;
  const varyNow = useRef(onVary);
  varyNow.current = onVary;
  // What the CSAs' table offers on the rows selected and the cell under the menu (ADR-0016 note 25) comes first.
  const contextCommands = useCallback((context: GridContextMenuContext<CsaRow>) => {
    const column = columnsNow.current.find((c) => c.name === context.column);
    const under = menuView((read) => heldView({ table: CSAS_TABLE, columns: columnsNow.current.map((c) => c.name) }, read, source.snapshot(), csaKey), context, context.row.csaId);
    return [
      ...menu.current.csas.commands(under),
      ...(column?.editable === true
        ? [{ id: VARY_COMMAND, enabled: true, invoke: () => varyNow.current({ entity: `csa:${context.row.csaId}`, field: context.column, title: `${context.row.csaId} ${column.header}` }) }]
        : []),
    ];
  }, [menu, source]);

  // The grid as the line is told of it (ADR-0016 note 16): the page holds every CSA, so its rows are named by their ids,
  // and a place read for that view (note 21) is the row of its key among the rows the grid then held, as are the rows
  // a line selects (note 43).
  const placed = useRef(onPlaced);
  placed.current = onPlaced;
  const screen = useCallback((): Screen | undefined => {
    const grid = gridRef.current;
    if (grid === null) return undefined;
    const declared = { table: CSAS_TABLE, columns: columnsNow.current.map((c) => c.name), unsorted: columnsNow.current.filter((c) => c.queryable === false).map((c) => c.name) };
    const read = grid.readView();
    const held = source.snapshot();
    const rowOf = (key: string) => {
      const at = held.window.findIndex((row) => row.csaId === key);
      return at < 0 ? undefined : held.windowStart + at;
    };
    return {
      view: heldView(declared, read, held, csaKey),
      put: (place) => placeFocus(declared, grid, read, place, rowOf) ?? placed.current(),
      // The grid sorts the CSAs it holds itself, as its header does; which of them conditions leave is the server's.
      sort: (asked) => sortGrid(declared, source, asked),
      filter: (asked) => filterGrid(source, asked),
      select: (asked) => selectRows(declared, grid, read, asked, rowOf),
    };
  }, [gridRef, source]);
  const filterOff = useCallback(() => { filterGrid(source, {}); }, [source]);
  // The filter's line takes its height from the grid's, as high as it is drawn (it wraps where the tab is narrow) and
  // the gap under it, so the tab stays as high as the page set it.
  const gap = pixels(useMantineTheme().spacing.xs);
  const { ref: filterLine, height: filterHeight } = useElementSize();
  const gridHeight = Math.min(compactGridHeight(shown.length), height - (filter === undefined ? 0 : filterHeight + gap));

  return (
    <Stack gap="xs">
      {filter === undefined ? null : <div ref={filterLine}><GridFilterLine said={filter} grid="the CSA terms" onOff={filterOff} /></div>}
      <div aria-label="CSA terms" onFocusCapture={() => onGridEntered(screen)}>
        <QubeGrid<CsaRow> ref={gridRef} columns={columns} onColumnWidthChanged={onColumnWidthChanged} source={source} onEdit={onEdit} cellState={csaCellState} cellMessage={csaCellMessage}
          contextCommands={contextCommands} commandLabel={commandLabel}
          density="compact" viewportWidth="stretch" viewportHeight={Math.max(compactGridHeight(1), gridHeight)} pinnedColumnCount={2} showSelectionSummary={false} locale="en-US" />
      </div>
    </Stack>
  );
}
