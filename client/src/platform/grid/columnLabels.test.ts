import type { GridColumn } from '@ex-grid/core';
import { labelledColumns } from './columnLabels';

type Row = { readonly v: number };
const column = (name: string, extra: Partial<GridColumn<Row>> = {}): GridColumn<Row> => ({ name, type: 'number', value: (r) => r.v, ...extra });

describe('ADR-0009 note of 2026-11-22: every grid heads its columns on the left', () => {
  it('heads a right-aligned figure column, and one aligned by its type, on the left', () => {
    const [aligned, typed] = labelledColumns([column('pv', { align: 'right' }), column('notional')]);
    expect(aligned).toMatchObject({ name: 'pv', align: 'right', headerAlign: 'left' });
    expect(typed).toMatchObject({ name: 'notional', headerAlign: 'left' });
    expect(typed?.align).toBeUndefined();
  });

  it('keeps a header alignment the column gives, the same column as it came', () => {
    const own = column('flag', { align: 'center', headerAlign: 'center' });
    expect(labelledColumns([own])[0]).toBe(own);
  });

  it('gives a column back as the same object each time, so the grid keeps what it measured for it (ExGrid ADR-0003)', () => {
    const pv = column('pv', { align: 'right' });
    const first = labelledColumns([pv, column('rate')]);
    const again = labelledColumns([pv, column('rate')]);
    expect(again[0]).toBe(first[0]);
    expect(again[1]).not.toBe(first[1]);
  });
});
