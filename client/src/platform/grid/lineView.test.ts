import { heldView, pagedView, type Answered, type GridRead } from './lineView';

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
    return { source: { window: rows, rowSequenceVersion: 4, sorts: [] }, answered: (row: Row) => of.get(row) };
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
    expect(pagedView(grid, read([]), { window: [], rowSequenceVersion: 4, sorts: asked }, () => undefined))
      .toEqual({ table: 'w.readings', subset: 'manual', sorts: [{ column: 'note', descending: false }] });
  });

  it('a Focus on a column the page did not name is the page\'s defect, and is not read as another column', () => {
    const { source, answered } = drawn([largest, largest, largest]);

    expect(() => pagedView(grid, read([[0, 1]], [0, 4]), source, answered)).toThrow('The Focus is on column 4 of a grid of w.readings, and its page names 4 columns.');
  });
});

describe('the view of a grid whose page holds every row (ADR-0016 note 16)', () => {
  interface Row { readonly id: string }
  const window: readonly Row[] = [{ id: 'A' }, { id: 'B' }, { id: 'C' }, { id: 'D' }];
  const source = { window, windowStart: 0, rowSequenceVersion: 4 };
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

  it('a position the page holds no row at is the page\'s defect', () => {
    expect(() => heldView(held, read([[3, 2]], [3, 0]), source, keyOf)).toThrow('A grid of w.stations has row 4 selected, and its page holds 4 rows from 0.');
  });
});
