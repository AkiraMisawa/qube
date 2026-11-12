import { createElement, type ReactNode } from 'react';
import { renderHook } from '@testing-library/react';
import { GridSource, type CellPosition, type GridColumn, type GridSelection, type SelectionRange } from '@ex-grid/core';
import type { ExGridApi } from '@ex-grid/react';
import { PanelPlaceProvider } from '../panelState';
import { PanelScreens } from '../workspace/panelScreens';
import { heldInOrder } from './heldFilter';
import { answeredInOrder, fixedRowsBySort, keptSelection, keysInOrder, placementOf, useKeptGrid, type OrderOf } from './keptGrid';
import type { Answered } from './lineView';

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
describe('ADR-0006 note 10: a grid\'s Selection kept by its panel', () => {
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
    const order = fixedRowsBySort<Row>();
    expect(order(sourceOf([]).snapshot())).toBeUndefined();
    expect(order(sourceOf(rows, 'ascending').snapshot())).not.toBe(order(sourceOf(rows, 'descending').snapshot()));
    expect(order(sourceOf(rows, 'ascending').snapshot())).toBe(order(sourceOf(rows.map((r) => ({ ...r })), 'ascending').snapshot()));
  });

  it('names a paged grid\'s order by its reader\'s token only where every row in hand came in one answer, under the conditions the source holds', () => {
    const answers = new Map<string, Answered>();
    const order = answeredInOrder<Row>((r) => answers.get(r.id));
    const source = sourceOf(rows);
    const first: Answered = { sorts: [], orderToken: 'o1' };
    for (const r of rows) answers.set(r.id, first);
    expect(order(source.snapshot())).toBe('o1');
    // A row of an earlier answer, while the next is read: the rows in hand are in no one order.
    answers.set('T3', { sorts: [], orderToken: 'o0' });
    expect(order(source.snapshot())).toBeUndefined();
    // Rows read before the conditions the source now holds.
    for (const r of rows) answers.set(r.id, first);
    source.setFilter({ columns: {}, opaque: { where: 'notional>15' } });
    expect(order(source.snapshot())).toBeUndefined();
    expect(order(sourceOf([]).snapshot())).toBeUndefined();
  });

  it('names a held grid\'s order only once the server has said which rows the conditions its source holds leave', () => {
    const source = sourceOf(rows);
    source.setFilter({ columns: {}, opaque: { where: 'notional>15' } });
    const state = source.snapshot();
    // The page drawn before the source took its conditions back, then before the server answered for them.
    const id = (r: Row) => r.id;
    expect(heldInOrder(id, undefined)(state)).toBeUndefined();
    expect(heldInOrder(id, { where: 'notional>15' })(state)).toBeUndefined();
    expect(heldInOrder(id, { where: 'notional>15', counted: { left: 3, of: 3 } })(state)).toBe(byId(state));
    expect(heldInOrder(id, undefined)(sourceOf(rows).snapshot())).toBe(byId(sourceOf(rows).snapshot()));
  });
});

/** A grid as a page's ref holds it, standing in for ExGrid: the Selection it shows, and the ones placed on it. */
function gridOf(source: GridSource<Row>, shows: GridSelection) {
  const placed: { range: SelectionRange; focus: CellPosition; rowSequenceVersion: number }[] = [];
  let selection = shows;
  const api = {
    readView: () => ({ rowSequenceVersion: source.snapshot().rowSequenceVersion, selection, scrollTopPx: 0, scrollLeftPx: 0 }),
    placeSelection: (range: SelectionRange, at: CellPosition, rowSequenceVersion: number) => {
      placed.push({ range, focus: at, rowSequenceVersion });
      selection = selected([range], at);
    },
  };
  // The two members of ExGrid's handle the hook reads and calls.
  return { ref: { current: api as unknown as ExGridApi<Row> }, placed, select: (next: GridSelection) => { selection = next; } };
}

/** Draws `useKeptGrid` in the panel `panel-1`, as a page drawn again after its panel was hidden. */
function draw(screens: PanelScreens, source: GridSource<Row>, grid: ReturnType<typeof gridOf>, orderOf: OrderOf<Row> = byId) {
  const wrapper = ({ children }: { readonly children: ReactNode }) => createElement(PanelPlaceProvider, { value: { panelId: 'panel-1', screens } }, children);
  return renderHook(() => useKeptGrid('test/trades', source, grid.ref, columns, orderOf), { wrapper, reactStrictMode: true });
}

describe('ADR-0006 note 10: a grid drawn again takes back what its panel kept', () => {
  const screensOf = () => new PanelScreens((panelId) => panelId === 'panel-1');
  const nothing = selected([], undefined);

  it('keeps a Selection given back and not yet decided while the page is hidden again before its rows came, and places it once they come', () => {
    const screens = screensOf();
    const before = sourceOf(rows, 'descending');
    draw(screens, before, gridOf(before, selected([range], focus))).unmount();
    // Shown again, and hidden again before any row is in hand.
    const empty = sourceOf([], 'descending');
    const waiting = gridOf(empty, nothing);
    draw(screens, empty, waiting).unmount();
    expect(waiting.placed).toEqual([]);
    // Shown once more: the rows come, in the order the Selection was read in.
    const again = sourceOf([], 'descending');
    const shown = gridOf(again, nothing);
    draw(screens, again, shown);
    again.replaceAll(rows.map((r) => ({ ...r })));
    expect(shown.placed).toEqual([{ range, focus, rowSequenceVersion: again.snapshot().rowSequenceVersion }]);
    expect(again.snapshot().sorts).toEqual([{ column: 'notional', direction: 'descending' }]);
  });

  it('places nothing over a Selection the user made before it was decided, and keeps the user\'s', () => {
    const screens = screensOf();
    const before = sourceOf(rows, 'descending');
    draw(screens, before, gridOf(before, selected([range], focus))).unmount();
    // Shown again, its rows drawn in no order yet (a held grid before the server said which rows its conditions leave),
    // and the user selects a cell meanwhile.
    let said = false;
    const again = sourceOf(rows.map((r) => ({ ...r })), 'descending');
    const shown = gridOf(again, nothing);
    const drawn = draw(screens, again, shown, (state) => (said ? byId(state) : undefined));
    const mine = { topRow: 0, rowCount: 1, leftColumn: 0, columnCount: 1 };
    shown.select(selected([mine], { row: 0, column: 0 }));
    said = true;
    again.replaceAll(rows.map((r) => ({ ...r })));
    expect(shown.placed).toEqual([]);
    drawn.unmount();
    // Shown once more: the user's Selection is the one kept, and placed.
    const last = sourceOf([], 'descending');
    const back = gridOf(last, nothing);
    draw(screens, last, back);
    last.replaceAll(rows.map((r) => ({ ...r })));
    expect(back.placed).toEqual([{ range: mine, focus: { row: 0, column: 0 }, rowSequenceVersion: last.snapshot().rowSequenceVersion }]);
  });
});
