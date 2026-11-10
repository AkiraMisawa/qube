import { gridHeaderRequiredPx, type GridColumn } from '@ex-grid/core';
import { columnsAt } from './columnWidths';

type Row = { readonly v: string };
const column = (name: string, header: string, width: GridColumn<Row>['width'], queryable?: false): GridColumn<Row> => ({
  name, header, type: 'text', value: (r) => r.v, ...(width === undefined ? {} : { width }), ...(queryable === undefined ? {} : { queryable }),
});
const needs = (header: string, sortable = true) => gridHeaderRequiredPx(header, { density: 'compact', menuButton: true, sortable });

// Column widths as ExGrid ADR-0016 hands them to the page: a declared width never cuts its header, and a dragged one is
// kept per panel (IMPLEMENTATION_STATUS, slice 01's ExGrid notes).
describe('column widths', () => {
  it('never cuts a header at the default width, but leaves a wide declaration and an Auto width as declared', () => {
    const [narrow, bounded, unsorted, wide, auto] = columnsAt([
      column('par', 'Par / fair rate', 40),
      column('index', 'Leg 1 index', { px: 30, minPx: 24 }),
      column('runId', 'Run Id', 20, false),
      column('notional', 'Notional', 300),
      column('issues', 'Issues', { auto: true, maxPx: 500 }),
    ], {});

    expect(narrow!.width).toBeGreaterThanOrEqual(needs('Par / fair rate'));
    expect(bounded!.width).toEqual({ px: Math.ceil(needs('Leg 1 index')), minPx: 24 });
    // A column that does not sort has no sort mark to make room for.
    expect(unsorted!.width).toBe(Math.ceil(needs('Run Id', false)));
    expect(wide!.width).toBe(300);
    expect(auto!.width).toEqual({ auto: true, maxPx: 500 });
  });

  it('charges no column-menu button to a grid that hides its column menu (ADR-0015 §4)', () => {
    const [ccy] = columnsAt([column('ccy', 'Ccy', 20, false)], {}, { hideColumnMenu: true });

    expect(ccy!.width).toBe(Math.ceil(gridHeaderRequiredPx('Ccy', { density: 'compact', menuButton: false, sortable: false })));
    expect(ccy!.width).toBeLessThan(needs('Ccy', false));
  });

  it('draws a dragged column at the width the user chose, even below its header, with its declared bounds', () => {
    const [dragged, other] = columnsAt([
      column('par', 'Par / fair rate', { px: 120, minPx: 30, maxPx: 400 }),
      column('notional', 'Notional', 300),
    ], { par: 32 });

    expect(dragged!.width).toEqual({ px: 32, minPx: 30, maxPx: 400 });
    expect(other!.width).toBe(300);
  });
});
