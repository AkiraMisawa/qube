import { GridSource, type GridColumn, type GridSelection } from '@ex-grid/core';
import { keptSelection, keysInOrder, placementOf, rowsThatNeverChange } from './keptGrid';

type Row = { readonly id: string; readonly notional: number };
const columns: readonly GridColumn<Row>[] = [
  { name: 'id', header: 'ID', type: 'text', value: (r) => r.id },
  { name: 'notional', header: 'Notional', type: 'number', value: (r) => r.notional },
];
const rows: readonly Row[] = [{ id: 'T1', notional: 30 }, { id: 'T2', notional: 10 }, { id: 'T3', notional: 20 }];
const byId = keysInOrder<Row>((r) => r.id);

/** A source holding every row, given the grid's columns as a grid gives them, sorted where asked. */
function sourceOf(held: readonly Row[], sort?: 'ascending' | 'descending') {
  const source = GridSource.from<Row>(held, { rowKey: (r) => r.id });
  source.setColumns(columns);
  if (sort !== undefined) source.setSorts([{ column: 'notional', direction: sort }]);
  return source;
}

const range = { topRow: 1, rowCount: 2, leftColumn: 0, columnCount: 2 };
const focus = { row: 1, column: 1 };
const selected = (ranges: GridSelection['ranges'], at: GridSelection['focus']): GridSelection =>
  ({ ranges, focus: at, isEmpty: ranges.length === 0 }) as GridSelection;

// ADR-0006 note 10: a grid's Selection is kept with what makes its positions mean the same cells, and given back only
// onto the rows it was read among.
describe('a grid\'s Selection kept by its panel', () => {
  it('is kept as one range and its Focus, with the order of the rows and the columns it was read among', () => {
    const state = sourceOf(rows, 'descending').snapshot();
    const kept = keptSelection({ rowSequenceVersion: state.rowSequenceVersion, selection: selected([range], focus) }, state, columns, byId);
    expect(kept).toEqual({ range, focus, columns: ['id', 'notional'], order: JSON.stringify(['T1', 'T3', 'T2']) });
  });

  it('is not kept where nothing is selected, where it is of several ranges, or where it was read in another order than the source holds', () => {
    const state = sourceOf(rows).snapshot();
    const read = (selection: GridSelection, version = state.rowSequenceVersion) => keptSelection({ rowSequenceVersion: version, selection }, state, columns, byId);
    expect(read(selected([], undefined))).toBeUndefined();
    expect(read(selected([range, { topRow: 0, rowCount: 1, leftColumn: 0, columnCount: 1 }], focus))).toBeUndefined();
    expect(read(selected([range], focus), state.rowSequenceVersion + 1)).toBeUndefined();
  });

  it('is placed again on the same rows, in the order and columns it was read in, at the version the source holds now', () => {
    const before = sourceOf(rows, 'descending').snapshot();
    const kept = keptSelection({ rowSequenceVersion: before.rowSequenceVersion, selection: selected([range], focus) }, before, columns, byId)!;
    // A grid drawn again: a new source, the sort given back, the rows read again.
    const again = sourceOf([], 'descending');
    expect(placementOf(kept, again.snapshot(), columns, byId)).toEqual({ kind: 'pending' });
    again.replaceAll(rows.map((r) => ({ ...r })));
    const now = again.snapshot();
    expect(placementOf(kept, now, columns, byId)).toEqual({ kind: 'place', range, focus, rowSequenceVersion: now.rowSequenceVersion });
  });

  it('is placed nowhere where the rows moved while the panel was hidden, or the grid shows other columns: never on other cells', () => {
    const before = sourceOf(rows, 'descending').snapshot();
    const kept = keptSelection({ rowSequenceVersion: before.rowSequenceVersion, selection: selected([range], focus) }, before, columns, byId)!;
    // T2 grew past T3: the second position is another trade now.
    const moved = sourceOf([rows[0]!, { id: 'T2', notional: 25 }, rows[2]!], 'descending').snapshot();
    expect(placementOf(kept, moved, columns, byId)).toEqual({ kind: 'none' });
    expect(placementOf(kept, sourceOf(rows, 'descending').snapshot(), [...columns].reverse(), byId)).toEqual({ kind: 'none' });
  });

  it('names the order of rows that never change by their sort alone, once rows are in hand', () => {
    const order = rowsThatNeverChange<Row>();
    expect(order(sourceOf([]).snapshot())).toBeUndefined();
    expect(order(sourceOf(rows, 'ascending').snapshot())).not.toBe(order(sourceOf(rows, 'descending').snapshot()));
    expect(order(sourceOf(rows, 'ascending').snapshot())).toBe(order(sourceOf(rows.map((r) => ({ ...r })), 'ascending').snapshot()));
  });
});
