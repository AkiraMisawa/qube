import type { LineFilter, LinePlace, LineSort } from '../contracts';
import { filterGrid, heldView, menuView, pagedView, placeFocus, sortGrid, whereOf, type Answered, type GridRead, type ScreenGrid } from './lineView';

// ADR-0016 notes 16 and 17 of 2026-11-11: what a page reads of a grid for the command line. The names are made up.

const read = (ranges: readonly (readonly [number, number])[], focus?: readonly [number, number], rowSequenceVersion = 4): GridRead => ({
  rowSequenceVersion,
  selection: { ranges: ranges.map(([topRow, rowCount]) => ({ topRow, rowCount })), focus: focus === undefined ? undefined : { row: focus[0], column: focus[1] } },
});
const grid = { table: 'w.readings', subset: 'manual', columns: ['id', 'volume', 'note', 'age'] };

describe('the view of a grid the server pages (ADR-0016 notes 16, 17)', () => {
  interface Row { readonly id: string }
  const largest: Answered = { sorts: [{ column: 'volume', direction: 'descending' }, { column: 'id', direction: 'ascending' }], orderToken: 'v3/r7' };
  const rows: readonly Row[] = [{ id: 'R4' }, { id: 'R1' }, { id: 'R9' }];
  /** The rows in hand, each from an answer of the grid's reader. */
  const drawn = (answers: readonly (Answered | undefined)[]) => {
    const of = new Map(rows.map((row, i) => [row, answers[i]]));
    return { source: { window: rows, rowSequenceVersion: 4, sorts: [], filter: undefined }, answered: (row: Row) => of.get(row) };
  };

  it('names the rows by their positions and the cell by its row\'s, in the sort and the order the rows on screen were answered in', () => {
    const { source, answered } = drawn([largest, largest, { ...largest }]);

    expect(pagedView(grid, read([[0, 2], [5, 1]], [5, 2]), source, answered)).toEqual({
      table: 'w.readings', subset: 'manual',
      sorts: [{ column: 'volume', descending: true }, { column: 'id', descending: false }], order: 'v3/r7',
      ranges: [{ start: 0, count: 2 }, { start: 5, count: 1 }],
      focus: { field: 'note', position: 5 },
    });
  });

  it('with nothing selected names no rows and no cell, and a grid of a whole table names no subset', () => {
    const { source, answered } = drawn([largest, largest, largest]);

    expect(pagedView({ table: 'w.readings', columns: grid.columns }, read([]), source, answered))
      .toEqual({ table: 'w.readings', sorts: [{ column: 'volume', descending: true }, { column: 'id', descending: false }], order: 'v3/r7' });
  });

  it('gives no order where the positions are not of the rows in hand, so that the server refuses them and guesses nothing', () => {
    const selected = read([[1, 1]], [1, 0]);
    const asked = [{ column: 'note', direction: 'ascending' as const }];
    const unordered = { table: 'w.readings', subset: 'manual', sorts: [{ column: 'note', descending: false }], ranges: [{ start: 1, count: 1 }], focus: { field: 'id', position: 1 } };

    // The selection is written in a sequence the source has left: the grid is about to drop it.
    const moved = drawn([largest, largest, largest]);
    expect(pagedView(grid, selected, { ...moved.source, rowSequenceVersion: 5, sorts: asked }, moved.answered)).toEqual(unordered);
    // The rows in hand are of two orders, or of a reader this page did not hear answer.
    const mixed = drawn([largest, { ...largest, orderToken: 'v3/r8' }, largest]);
    expect(pagedView(grid, selected, { ...mixed.source, sorts: asked }, mixed.answered)).toEqual(unordered);
    const resorted = drawn([largest, { ...largest, sorts: [largest.sorts[0]!] }, largest]);
    expect(pagedView(grid, selected, { ...resorted.source, sorts: asked }, resorted.answered)).toEqual(unordered);
    const unheard = drawn([largest, undefined, largest]);
    expect(pagedView(grid, selected, { ...unheard.source, sorts: asked }, unheard.answered)).toEqual(unordered);
    // No row is in hand yet: the sort is the one the grid asked for.
    expect(pagedView(grid, read([]), { window: [], rowSequenceVersion: 4, sorts: asked, filter: undefined }, () => undefined))
      .toEqual({ table: 'w.readings', subset: 'manual', sorts: [{ column: 'note', descending: false }] });
  });

  it('ADR-0016 note 41: carries the conditions the line gave its source, and the order only where every row in hand was answered under them', () => {
    const north = { columns: {}, opaque: { where: 'station = NORTH' } };
    const under: Answered = { ...largest, where: 'station = NORTH', orderToken: 'v3/r7/f0a1b' };
    const sorts = [{ column: 'volume', descending: true }, { column: 'id', descending: false }];
    const filtered = drawn([under, under, under]);

    expect(pagedView(grid, read([[0, 2]]), { ...filtered.source, filter: north }, filtered.answered))
      .toEqual({ table: 'w.readings', subset: 'manual', sorts, where: 'station = NORTH', order: 'v3/r7/f0a1b', ranges: [{ start: 0, count: 2 }] });
    // The conditions were just given, and the rows in hand are of the grid before them: they are said, with the sort
    // the source holds and no order.
    const before = drawn([largest, largest, largest]);
    expect(pagedView(grid, read([[0, 2]]), { ...before.source, sorts: largest.sorts, filter: north }, before.answered))
      .toEqual({ table: 'w.readings', subset: 'manual', sorts, where: 'station = NORTH', ranges: [{ start: 0, count: 2 }] });
    // Other conditions than the rows in hand were answered under, or none any more: no order either.
    expect(pagedView(grid, read([]), { ...filtered.source, sorts: largest.sorts, filter: { columns: {}, opaque: { where: 'station = SOUTH' } } }, filtered.answered))
      .toEqual({ table: 'w.readings', subset: 'manual', sorts, where: 'station = SOUTH' });
    expect(pagedView(grid, read([]), { ...filtered.source, sorts: largest.sorts }, filtered.answered)).toEqual({ table: 'w.readings', subset: 'manual', sorts });
  });

  it('a Focus on a column the page did not name is the page\'s defect, and is not read as another column', () => {
    const { source, answered } = drawn([largest, largest, largest]);

    expect(() => pagedView(grid, read([[0, 1]], [0, 4]), source, answered)).toThrow('The Focus is on column 4 of a grid of w.readings, and its page names 4 columns.');
  });
});

describe('the view of a grid whose page holds every row (ADR-0016 note 16)', () => {
  interface Row { readonly id: string }
  const window: readonly Row[] = [{ id: 'A' }, { id: 'B' }, { id: 'C' }, { id: 'D' }];
  const source = { window, windowStart: 0, rowSequenceVersion: 4, filter: undefined };
  const keyOf = (row: Row) => row.id;
  const held = { table: 'w.stations', columns: ['id', 'altitude'] };

  it('names the rows by their keys, each once in the order selected, and the cell by its row\'s key', () => {
    expect(heldView(held, read([[2, 2], [0, 1], [3, 1]], [3, 1]), source, keyOf))
      .toEqual({ table: 'w.stations', keys: ['C', 'D', 'A'], focus: { field: 'altitude', key: 'D' } });
    expect(heldView({ ...held, subset: 'high' }, read([]), source, keyOf)).toEqual({ table: 'w.stations', subset: 'high' });
  });

  it('a selection written in a sequence the source has left names no row: the grid is about to drop it', () => {
    expect(heldView(held, read([[0, 2]], [0, 0], 3), source, keyOf)).toEqual({ table: 'w.stations' });
  });

  it('a grid its page filters names the rows it shows, which `shown` then means (ADR-0016 §2), whatever sequence its selection is written in', () => {
    const filtered = { window: [{ id: 'B' }, { id: 'D' }], windowStart: 0, rowSequenceVersion: 5, filter: { columns: { altitude: { clauses: [] } } } };

    expect(heldView(held, read([[1, 1]], [1, 0], 5), filtered, keyOf)).toEqual({ table: 'w.stations', shown: ['B', 'D'], keys: ['D'], focus: { field: 'id', key: 'D' } });
    expect(heldView(held, read([[1, 1]], [1, 0], 4), filtered, keyOf)).toEqual({ table: 'w.stations', shown: ['B', 'D'] });
    expect(heldView(held, read([]), { ...filtered, window: [] }, keyOf)).toEqual({ table: 'w.stations', shown: [] });
  });

  it('ADR-0016 note 41: a grid filtered from the line says its conditions and names no row for them: which rows they leave is the server\'s to read, whatever the grid has drawn', () => {
    const high = { window: [{ id: 'B' }, { id: 'D' }], windowStart: 0, rowSequenceVersion: 5, filter: { columns: {}, opaque: { where: 'altitude > 100' } } };

    expect(heldView(held, read([[1, 1]], [1, 0], 5), high, keyOf)).toEqual({ table: 'w.stations', where: 'altitude > 100', keys: ['D'], focus: { field: 'id', key: 'D' } });
    // Its page has yet to hear which rows they leave, and still holds every row: the view is the same one.
    expect(heldView(held, read([]), { ...source, filter: high.filter }, keyOf)).toEqual({ table: 'w.stations', where: 'altitude > 100' });
  });

  it('ADR-0016 note 41: with a filter of its own columns too, the grid names the rows it shows beside the conditions, which the server reads among them', () => {
    const both = { window: [{ id: 'B' }], windowStart: 0, rowSequenceVersion: 5, filter: { columns: { id: { clauses: [] } }, opaque: { where: 'altitude > 100' } } };

    expect(heldView(held, read([]), both, keyOf)).toEqual({ table: 'w.stations', shown: ['B'], where: 'altitude > 100' });
  });

  it('a position the page holds no row at is the page\'s defect', () => {
    expect(() => heldView(held, read([[3, 2]], [3, 0]), source, keyOf)).toThrow('A grid of w.stations has row 4 selected, and its page holds 4 rows from 0.');
  });
});

describe('the cell a place names in a grid (ADR-0016 §9, note 21 of 2026-11-11)', () => {
  const grid: ScreenGrid = { table: 'w.readings', subset: 'manual', columns: ['id', 'station', 'note'] };
  const version: GridRead = { rowSequenceVersion: 4, selection: { ranges: [], focus: undefined } };
  const place: LinePlace = { table: 'w.readings', subset: 'manual', key: 'R2', said: 'The keyboard is on R2 in readings (manual).' };
  const placing = (takes = true, editing?: string) => ({ placeSelection: vi.fn(() => takes), readEditText: () => editing });
  const nowhere = () => undefined;

  it('is placed as one cell with the Focus on it, by its position where the server pages the grid and its column by name, in the version the view was read in', () => {
    const handle = placing();

    expect(placeFocus(grid, handle, version, { ...place, position: 7, column: 'note' })).toBeUndefined();

    expect(handle.placeSelection.mock.calls).toEqual([[{ topRow: 7, rowCount: 1, leftColumn: 2, columnCount: 1 }, { row: 7, column: 2 }, 4]]);
  });

  it('is found by its key where the page holds every row, and takes the first column where the grid had no Focus to keep', () => {
    const handle = placing();

    expect(placeFocus(grid, handle, version, place, (key) => (key === 'R2' ? 5 : undefined))).toBeUndefined();

    expect(handle.placeSelection.mock.calls).toEqual([[{ topRow: 5, rowCount: 1, leftColumn: 0, columnCount: 1 }, { row: 5, column: 0 }, 4]]);
  });

  it('is not placed, and says why, where the grid shows no such column, holds no such row, or was read in no one order while its page holds part of its rows', () => {
    const handle = placing();
    expect(placeFocus(grid, handle, version, { ...place, position: 7, column: 'wind' }, nowhere)).toBe('The grid on screen shows no column wind: name R2 alone to go to its row.');
    expect(placeFocus(grid, handle, version, place, nowhere)).toBe('R2 is no longer a row the grid on screen holds: open the line again.');
    expect(placeFocus(grid, handle, version, place)).toBe('The grid on screen held its rows in no one order when the line opened, so R2 has no position in it: open the line again.');
    expect(handle.placeSelection).not.toHaveBeenCalled();
  });

  it('says which it is where the grid refuses the place: it has moved since the view was read, or it keeps an edit open that it could not commit', () => {
    expect(placeFocus(grid, placing(false), version, { ...place, position: 7 })).toBe('The grid on screen has moved since the line opened: open the line again.');
    expect(placeFocus(grid, placing(false, '12x'), version, { ...place, position: 7 })).toBe('The grid on screen keeps an edit open that it could not commit: correct or cancel it first.');
  });
});

describe('the view a Context Menu\'s item is run with (ADR-0016 §4, note 25 of 2026-11-11)', () => {
  const menu = { column: 'note', selection: [{ topRow: 2, rowCount: 3, leftColumn: 0, columnCount: 4 }], rowSequenceVersion: 4 };

  it('is the grid\'s view of the Selection the menu was handed, in the version it was handed in, with the cell the menu was opened on as the focused cell, by its row\'s key', () => {
    const seen: GridRead[] = [];
    const view = menuView((read) => { seen.push(read); return { table: 'w.readings', subset: 'manual', order: 'v3/o1', ranges: [{ start: 2, count: 3 }] }; }, menu, 'R9');

    // The grid's own Focus is not read: inside a Selection it may be another cell than the one under the menu.
    expect(seen).toEqual([{ rowSequenceVersion: 4, selection: { ranges: [{ topRow: 2, rowCount: 3 }], focus: undefined } }]);
    expect(view).toEqual({ table: 'w.readings', subset: 'manual', order: 'v3/o1', ranges: [{ start: 2, count: 3 }], focus: { field: 'note', key: 'R9' } });
  });
});

describe('a sort and a filter the line gives a grid (ADR-0016 notes 39 to 41 of 2026-11-11)', () => {
  const grid: ScreenGrid = { table: 'w.readings', subset: 'manual', columns: ['id', 'volume', 'note'] };
  /** A grid's source as the line reads it: the Filter it holds, and what it was last given. */
  const source = (filter?: { readonly columns: Readonly<Record<string, unknown>>; readonly opaque?: unknown }) => {
    const held = { filter, sorts: undefined as unknown };
    return { held, snapshot: () => ({ filter: held.filter }), setSorts: vi.fn((sorts: unknown) => { held.sorts = sorts; }), setFilter: vi.fn((next: typeof filter) => { held.filter = next; }) };
  };
  const by = (...sorts: LineSort['sorts']): LineSort => ({ table: 'w.readings', subset: 'manual', sorts, said: 'Sorted readings (manual).' });
  const where = (conditions?: string): LineFilter => ({ table: 'w.readings', subset: 'manual', ...(conditions === undefined ? {} : { where: conditions }), said: 'Said.' });

  it('a sort is the grid\'s own Sort by its columns\' names, the major key first, and none takes it off', () => {
    const s = source();

    expect(sortGrid(grid, s, by({ column: 'volume', descending: true }, { column: 'id', descending: false }))).toBeUndefined();
    expect(s.held.sorts).toEqual([{ column: 'volume', direction: 'descending' }, { column: 'id', direction: 'ascending' }]);
    expect(sortGrid(grid, s, by())).toBeUndefined();
    expect(s.held.sorts).toEqual([]);
  });

  it('a field the grid shows no column of is refused by name, and the grid keeps the sort it had', () => {
    const s = source();

    expect(sortGrid(grid, s, by({ column: 'volume', descending: false }, { column: 'wind', descending: true }))).toBe('The grid on screen shows no column wind to sort by.');
    expect(s.setSorts).not.toHaveBeenCalled();
  });

  it('a column the grid itself does not sort by is refused by name too, where its page says so', () => {
    const s = source();

    expect(sortGrid({ ...grid, unsorted: ['note'] }, s, by({ column: 'note', descending: false }))).toBe('The grid on screen does not sort by note.');
    expect(s.setSorts).not.toHaveBeenCalled();
    expect(sortGrid({ ...grid, unsorted: ['note'] }, s, by({ column: 'volume', descending: false }))).toBeUndefined();
  });

  it('the conditions are kept as the source\'s Opaque Filter, beside the filters of the grid\'s own columns, and read back from it', () => {
    const s = source({ columns: { note: { clauses: [] } } });

    expect(filterGrid(s, where('station = NORTH'))).toBeUndefined();
    expect(s.held.filter).toEqual({ columns: { note: { clauses: [] } }, opaque: { where: 'station = NORTH' } });
    expect(whereOf(s.held.filter)).toBe('station = NORTH');

    // New conditions replace the ones before; none takes them off and leaves the columns' own.
    filterGrid(s, where('volume > 5mm'));
    expect(whereOf(s.held.filter)).toBe('volume > 5mm');
    filterGrid(s, where());
    expect(s.held.filter).toEqual({ columns: { note: { clauses: [] } } });
    expect(whereOf(s.held.filter)).toBeUndefined();
  });

  it('with no filter of a column either, conditions taken off leave the grid no Filter at all', () => {
    const s = source();

    filterGrid(s, where('station = NORTH'));
    expect(s.held.filter).toEqual({ columns: {}, opaque: { where: 'station = NORTH' } });
    filterGrid(s, where());
    expect(s.held.filter).toBeUndefined();
    expect(whereOf(undefined)).toBeUndefined();
  });

  it('an Opaque Filter the line did not give is a defect, never read as no conditions', () => {
    expect(() => whereOf({ columns: {}, opaque: { desk: 'rates' } })).toThrow('A grid holds an Opaque Filter the command line did not give it.');
  });
});
