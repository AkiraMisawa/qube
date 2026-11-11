import { useCallback, useEffect, useMemo, useRef, type Ref } from 'react';
import { NativeSelect, Select, Stack, Text } from '@mantine/core';
import { GridSource, type CellState, type GridColumn, type GridContextMenuContext, type GridEditIntent } from '@ex-grid/core';
import { QubeGrid } from '../../platform/grid/QubeGrid';
import type { EffectiveCds, EffectiveCredit } from './contracts';
import { bpFigure, bpText, catalogueOf, catalogueRefused, inBp, spreadTyped, type CatalogueRead } from './credit';
import type { PushEdit, SetText } from './editing';
import { ToolbarField } from './fields';
import { compactGridHeight } from './layout';
import { differs, originalMessage } from './original';
import { VARY_COMMAND, type VaryTarget } from './VaryDialog';
import { useColumnWidths } from '../../platform/grid/columnWidths';

// XVA's Credit tab (ADR-0010 §8's note of 2026-11-09), shown while CVA is selected: the counterparty's CDS, a single name
// found by its reference or a proxy by sector, region and rating, or none; and the chosen curve as one row, its spreads
// editable in bp. The server holds the catalogue, the choice and every rule; the tab sends what the user chose or typed,
// and says why CVA is refused while no curve is chosen.

const TYPES = [
  { value: 'singleName', label: 'Single name' },
  { value: 'proxy', label: 'Proxy' },
  { value: 'none', label: 'None' },
];

const percent = new Intl.NumberFormat('en-US', { style: 'percent', maximumFractionDigits: 2 });
const word = (v: unknown) => String(v);

type Row = EffectiveCds;

/**
 * The curve's terms, read-only, then a column a tenor, editable, in bp (made once a tenor list, ExGrid ADR-0003). It is
 * one row, the counterparty's curve: nothing to sort or filter, so no header keeps room for a sort mark.
 */
const curveColumnsOf = (tenors: readonly string[]): readonly GridColumn<Row>[] => [
  { name: 'type', header: 'Type', type: 'text', queryable: false, width: 104, value: (r) => (r.type === 'proxy' ? 'Proxy' : 'Single name'), format: word },
  { name: 'ticker', header: 'Ticker', type: 'text', queryable: false, width: 112, value: (r) => r.ticker, format: word },
  { name: 'currency', header: 'Ccy', type: 'text', queryable: false, width: 72, value: (r) => r.currency, format: word },
  { name: 'seniority', header: 'Seniority', type: 'text', queryable: false, width: 104, value: (r) => r.seniority, format: word },
  { name: 'docClause', header: 'Doc clause', type: 'text', queryable: false, width: 112, value: (r) => r.docClause, format: word },
  { name: 'recovery', header: 'Recovery', type: 'number', queryable: false, width: 104, value: (r) => r.recovery, format: (v) => percent.format(v as number) },
  ...tenors.map((t): GridColumn<Row> => ({
    name: t, header: `${t.toUpperCase()} bp`, type: 'number', queryable: false, width: 88, editable: true,
    value: (r) => (r.spreads[t] === undefined ? undefined : inBp(r.spreads[t])), format: (v) => bpFigure(v as number),
  })),
];

// A spread that differs from the catalogue's is marked and described in words (ADR-0010 §2): `Catalogue 52bp · now 60bp`.
const curveCellState = (row: Row, col: GridColumn<Row>): CellState => (differs(row, col.name) ? 'modified' : 'normal');
const curveCellMessage = (row: Row, col: GridColumn<Row>): string | undefined => {
  if (!differs(row, col.name)) return undefined;
  const was = row.original?.[col.name];
  const now = row.spreads[col.name];
  return typeof was === 'number' && now !== undefined ? `Catalogue ${bpText(was)} · now ${bpText(now)}` : undefined;
};

export function XvaCredit({ panelId, credit, registration, catalogue: read, push, setText, onVary, commandLabel, focusRef }: {
  readonly credit: EffectiveCredit;
  /** The panel the page is in, which keeps the widths the user dragged in its grids. */
  readonly panelId: string;
  /** The counterparty's registration key, whose CDS the choice is. */
  readonly registration: string;
  /** The catalogue the reference search offers; none until it has been read, and why where it could not be. */
  readonly catalogue: CatalogueRead;
  readonly push: PushEdit;
  /** A spread as written, with its unit, for the server to read as the cell reads it. */
  readonly setText: SetText;
  readonly onVary: (target: VaryTarget) => void;
  readonly commandLabel: (id: string) => string | undefined;
  /** The CDS type, where `open credit` puts the keyboard. */
  readonly focusRef: Ref<HTMLSelectElement>;
}) {
  const entity = `credit:${registration}`;
  const catalogue = catalogueOf(read);
  const refused = catalogueRefused(read);
  const type = credit.choice.type ?? 'none';
  const marked = (field: string) => (differs(credit, field)
    ? { 'data-modified': true, title: originalMessage(credit, field, credit.choice[keyOf(field)]) ?? 'Set by you', classNames: { input: 'qube-modified' } } : {});
  const key = (field: string, label: string, values: readonly string[] | undefined, chosen: string | null | undefined) => (
    <ToolbarField label={label}>
      {(id) => (
        <NativeSelect id={id} size="xs" className="qube-toolbar-input" value={chosen ?? ''} {...marked(field)}
          data={[...(chosen == null ? [{ value: '', label: 'Choose…' }] : []), ...(values ?? (chosen == null ? [] : [chosen])).map((v) => ({ value: v, label: v }))]}
          onChange={(e) => push(entity, field, e.currentTarget.value === '' ? null : e.currentTarget.value, `The CDS proxy's ${label.toLowerCase()}`)} />
      )}
    </ToolbarField>
  );

  return (
    <Stack gap="xs" className="qube-xva-credit">
      <div className="qube-toolbar" role="group" aria-label="CDS">
        <ToolbarField label="CDS">
          {(id) => (
            <NativeSelect id={id} size="xs" className="qube-toolbar-input" value={type} data={TYPES} ref={focusRef} {...marked('cdsType')}
              onChange={(e) => push(entity, 'cdsType', e.currentTarget.value === 'none' ? null : e.currentTarget.value, 'The CDS')} />
          )}
        </ToolbarField>
        {type === 'singleName' && (
          <ToolbarField label="Reference" wide>
            {(id) => (
              <Select id={id} size="xs" className="qube-toolbar-input qube-toolbar-input--wide" searchable allowDeselect={false} {...marked('cdsReference')}
                value={credit.choice.reference ?? null} placeholder="Search ticker, ccy, seniority, doc clause or name"
                nothingFoundMessage={refused === undefined ? 'No single name of the catalogue matches.' : 'The CDS catalogue could not be read.'}
                data={(catalogue?.singleNames ?? []).map((c) => ({ value: c.id, label: c.reference }))}
                onChange={(value) => { if (value !== null) push(entity, 'cdsReference', value, 'The CDS reference'); }} comboboxProps={{ withinPortal: true }} />
            )}
          </ToolbarField>
        )}
        {type === 'proxy' && (
          <>
            {key('proxySector', 'Sector', catalogue?.sectors, credit.choice.sector)}
            {key('proxyRegion', 'Region', catalogue?.regions, credit.choice.region)}
            {key('proxyRating', 'Rating', catalogue?.ratings, credit.choice.rating)}
          </>
        )}
      </div>
      {refused !== undefined && <Text size="sm" c="red" data-testid="cds-catalogue-refused">The CDS catalogue could not be read: {refused}</Text>}
      {credit.missing != null && <Text size="sm" c="red" data-testid="cds-missing">{credit.missing}</Text>}
      {credit.cds != null && (
        <CurveGrid panelId={panelId} curve={credit.cds} tenors={catalogue?.tenors ?? Object.keys(credit.cds.spreads)} setText={setText} onVary={onVary}
          commandLabel={commandLabel} />
      )}
    </Stack>
  );
}

/** The choice's field as the view holds it: `cdsType` is `type`, `proxySector` is `sector`. */
function keyOf(field: string): keyof EffectiveCredit['choice'] {
  switch (field) {
    case 'cdsType': return 'type';
    case 'cdsReference': return 'reference';
    case 'proxySector': return 'sector';
    case 'proxyRegion': return 'region';
    case 'proxyRating': return 'rating';
    default: throw new Error(`${field} is no field of the CDS choice.`);
  }
}

/**
 * The chosen curve as one row (ADR-0010 §8's note of 2026-11-09): its terms, the catalogue's, and its spreads, typed in
 * bp. *Vary values…* on a spread makes a case per value (ADR-0010 §3). It shows no Selection Summary: a sum of spreads
 * at different tenors is no figure.
 */
function CurveGrid({ panelId, curve, tenors, setText, onVary, commandLabel }: {
  /** The panel the page is in, which keeps the widths the user dragged in its grids. */
  readonly panelId: string;
  readonly curve: EffectiveCds;
  readonly tenors: readonly string[];
  readonly setText: SetText;
  readonly onVary: (target: VaryTarget) => void;
  readonly commandLabel: (id: string) => string | undefined;
}) {
  const source = useMemo(() => GridSource.from<Row>([], { rowKey: (r) => r.curveId }), []);
  useEffect(() => { source.replaceAll([curve]); }, [curve, source]);
  const tenorsKey = tenors.join('|');
  const declared = useMemo(() => curveColumnsOf(tenorsKey.split('|')), [tenorsKey]);
  const { columns, onColumnWidthChanged } = useColumnWidths(panelId, 'credit/curve', declared);

  // The server reads the spread and checks it; one it refuses keeps the editor open with its reason (ExGrid ADR-0380),
  // unless the user has cancelled the commit meanwhile.
  const onEdit = useCallback(async (intent: GridEditIntent<Row>) => {
    if (intent.row.spreads[intent.column] === undefined) { intent.refuse('This column is not editable.'); return; }
    if (intent.seenText !== intent.replacedText) { intent.refuse('The value changed while you were editing. Look at it again.'); return; }
    const typed = spreadTyped(intent.value);
    if (!typed.ok) { intent.refuse(typed.message); return; }
    const refused = await setText(`cds:${intent.row.curveId}`, intent.column, String(typed.value), `CDS ${intent.column}`);
    if (refused !== undefined && !intent.signal.aborted) intent.refuse(refused);
  }, [setText]);
  const varyNow = useRef(onVary);
  varyNow.current = onVary;
  const contextCommands = useCallback((context: GridContextMenuContext<Row>) =>
    (context.row.spreads[context.column] === undefined ? []
      : [{ id: VARY_COMMAND, enabled: true, invoke: () => varyNow.current({ entity: `cds:${context.row.curveId}`, field: context.column, title: `CDS ${context.column}` }) }]), []);

  return (
    <div aria-label="CDS curve">
      <QubeGrid<Row> columns={columns} onColumnWidthChanged={onColumnWidthChanged} source={source} onEdit={onEdit} cellState={curveCellState} cellMessage={curveCellMessage}
        contextCommands={contextCommands} commandLabel={commandLabel}
        density="compact" viewportWidth="stretch" viewportHeight={compactGridHeight(1)} pinnedColumnCount={2}
        showSelectionSummary={false} locale="en-US" />
    </div>
  );
}
