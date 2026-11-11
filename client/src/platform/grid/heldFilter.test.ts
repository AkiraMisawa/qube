import { describe, expect, it } from 'vitest';
import { leftOf, type Left } from './heldFilter';

// ADR-0016 note 41 of 2026-11-11: what a grid whose page holds every row draws under the conditions the line gave it.
// Which rows they leave is the server's answer; these are the rows the grid is given while one is awaited and after.
describe('the rows a held grid draws under conditions from the line (ADR-0016 note 41)', () => {
  interface Row { readonly id: string }
  const rows: readonly Row[] = [{ id: 'A' }, { id: 'B' }, { id: 'C' }, { id: 'D' }];
  const keyOf = (row: Row) => row.id;
  const ids = (drawn: { readonly rows: readonly Row[] }) => drawn.rows.map(keyOf).join('');
  const high = 'altitude > 100';
  const answered = (where: string, revision: number, keys: readonly string[]): Left => ({ where, revision, keys, of: 4 });

  it('with no conditions draws every row and says nothing', () => {
    expect(leftOf(rows, keyOf, { where: undefined, revision: 7, own: false }, undefined)).toEqual({ rows, said: undefined });
  });

  it('draws the rows the server answered for its conditions at the revision it shows, and counts them', () => {
    const drawn = leftOf(rows, keyOf, { where: high, revision: 7, own: false }, answered(high, 7, ['B', 'D']));

    expect([ids(drawn), drawn.said]).toEqual(['BD', { where: high, counted: { left: 2, of: 4 } }]);
  });

  it('until the server has answered once, keeps every row and says the conditions with no count', () => {
    expect(leftOf(rows, keyOf, { where: high, revision: 7, own: false }, undefined)).toEqual({ rows, said: { where: high } });
  });

  it('at a later revision keeps the rows last answered for the same conditions, counted as drawn, until that revision is answered', () => {
    // D was removed since: the grid draws the rows it holds of those answered, and its count is of what it draws.
    const drawn = leftOf(rows.filter((r) => r.id !== 'D'), keyOf, { where: high, revision: 8, own: false }, answered(high, 7, ['B', 'D']));

    expect([ids(drawn), drawn.said]).toEqual(['B', { where: high, counted: { left: 1, of: 4 } }]);
  });

  it('under new conditions keeps the rows it has, and says the new conditions with no count, until they are answered', () => {
    const drawn = leftOf(rows, keyOf, { where: 'altitude < 50', revision: 7, own: false }, answered(high, 7, ['B', 'D']));

    expect([ids(drawn), drawn.said]).toEqual(['BD', { where: 'altitude < 50' }]);
  });

  it('conditions that could not be read draw no row and say why, at a later revision too, until the server reads them', () => {
    const unread: Left = { where: 'colour = red', revision: 7, unread: 'stations has no field colour.' };
    const said = { where: 'colour = red', unread: 'No row is shown: stations has no field colour.' };

    expect(leftOf(rows, keyOf, { where: 'colour = red', revision: 7, own: false }, unread)).toEqual({ rows: [], said });
    expect(leftOf(rows, keyOf, { where: 'colour = red', revision: 8, own: false }, unread)).toEqual({ rows: [], said });
    // Other conditions typed over them are not said to be unread before they are read; the grid keeps what it has: no row.
    expect(leftOf(rows, keyOf, { where: high, revision: 8, own: false }, unread)).toEqual({ rows: [], said: { where: high } });
  });

  it('with a filter of its own columns the grid is given every row until the server has answered for what it shows now: its view names the rows in its Window', () => {
    // The view of such a grid names the rows it shows beside the conditions (lineView's heldView), and the server reads
    // the conditions among them: rows kept from an older answer would leave out a row an edit has since brought in.
    const earlier = leftOf(rows, keyOf, { where: high, revision: 8, own: true }, answered(high, 7, ['B', 'D']));
    const other = leftOf(rows, keyOf, { where: 'altitude < 50', revision: 7, own: true }, answered(high, 7, ['B', 'D']));
    const current = leftOf(rows, keyOf, { where: high, revision: 8, own: true }, answered(high, 8, ['B', 'C', 'D']));

    expect([ids(earlier), earlier.said]).toEqual(['ABCD', { where: high }]);
    expect([ids(other), other.said]).toEqual(['ABCD', { where: 'altitude < 50' }]);
    expect([ids(current), current.said]).toEqual(['BCD', { where: high, counted: { left: 3, of: 4 } }]);
  });
});
