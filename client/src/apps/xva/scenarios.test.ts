import { describe, expect, it } from 'vitest';
import type { ShiftRow } from './contracts';
import { clearedEdits, pastedEdits, shiftGridRows } from './scenarios';

// The Scenarios grid's edits as the page makes them of a paste or a clear (ADR-0010 §4). Every name is synthetic.

const shift = (id: string, scenario: string, index: string, sizeText: string): ShiftRow => ({
  id, row: 0, scenario, category: 'IR', index, tenor: null, factor: 'all', type: 'abs', size: 10, sizeText, description: null, issue: null, issueColumn: null,
});

describe('the Scenarios grid', () => {
  const rows = shiftGridRows([shift('s3', 'up', 'jpy_ois', '+10bp'), shift('s4', 'up', 'usd_sofr', '+5bp')], 3);

  it('ADR-0010 §4: every shift in the order made, then the blank rows a paste fills, each at its place', () => {
    expect(rows.map((r) => [r.key, r.position, r.shift?.id])).toEqual([
      ['s3', 1, 's3'], ['s4', 2, 's4'], ['blank-1', 3, undefined], ['blank-2', 4, undefined], ['blank-3', 5, undefined],
    ]);
  });

  it('ADR-0010 §4: a block pasted onto the blank rows is a new row each, its cells by column; a row pasted empty makes none', () => {
    const block = [['dn', '', 'jpy_ois', 'all', '', '-10bp'], ['', '', '', '', '', ''], ['dn', '', 'usdjpy', 'spot', '', '+5%']];
    const edits = pastedEdits(rows, [{ topRow: 2, leftColumn: 0, rowCount: 3, columnCount: 6 }], (c) => block[c.row - 2]![c.column]!);

    expect(edits).toEqual([
      { id: null, row: 3, cells: { scenario: 'dn', category: '', index: 'jpy_ois', factor: 'all', type: '', size: '-10bp' } },
      { id: null, row: 5, cells: { scenario: 'dn', category: '', index: 'usdjpy', factor: 'spot', type: '', size: '+5%' } },
    ]);
  });

  it('ADR-0010 §4: a block over a shift edits it by its id, and one cell typed onto many is each of them', () => {
    const edits = pastedEdits(rows, [{ topRow: 0, leftColumn: 5, rowCount: 2, columnCount: 1 }], () => '+1bp');

    expect(edits).toEqual([{ id: 's3', row: 1, cells: { size: '+1bp' } }, { id: 's4', row: 2, cells: { size: '+1bp' } }]);
  });

  it('ADR-0010 §4: Delete clears the shifts\' cells covered, and has nothing to clear on a blank row', () => {
    const edits = clearedEdits(rows, [{ topRow: 1, leftColumn: 0, rowCount: 3, columnCount: 7 }]);

    expect(edits).toEqual([{ id: 's4', row: 2, cells: { scenario: '', category: '', index: '', factor: '', type: '', size: '', description: '' } }]);
  });

  it('spine 1: a block past the grid\'s columns that take a value is refused, never written onto another column', () => {
    expect(() => pastedEdits(rows, [{ topRow: 0, leftColumn: 6, rowCount: 1, columnCount: 2 }], () => 'x')).toThrow('Column 8 of the Scenarios grid takes no value.');
  });
});
