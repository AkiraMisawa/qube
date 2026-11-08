import { describe, expect, it } from 'vitest';
import type { SideValue, TermKind, TermRead, TraceParameter } from './contracts';
import { checkText, diffText, formulaOf, leafFormulaTex, lookupRows, matrixTex, numberTex, operandTargets, parameterTex, stateText, symbolTex, valueText } from './formula';
import { typeset } from './Tex';

// ADR-0011 §6: the formula of a term, typeset with its kind's symbols and then each side's values. Every name and
// figure is synthetic.

const value = (v: number | string): SideValue => ({ state: 'value', value: v, check: { status: 'matches' }, written: true });
const absent: SideValue = { state: 'absent', reason: 'Nothing contributes.', check: { status: 'notApplicable' }, written: true };

const kind = (tex: string, extra: Partial<TermKind> = {}): TermKind =>
  ({ kind: 'k', label: 'K', symbol: 'K', unit: { kind: 'amount' }, citation: 'C1', tex, valueType: 'number', ...extra });

const term = (id: string, symbol: string, before: SideValue | undefined, after: SideValue | undefined, extra: Partial<TermRead> = {}): TermRead =>
  ({ id, kind: 'k', label: id, symbol, unit: { kind: 'amount' }, childCount: 0, before: before ?? null, after: after ?? null, ...extra });

const capital = kind('K = \\param{kappa} \\cdot \\slot{rwa}');
const root = term('capital', 'K', value(800), value(880), { childCount: 1, diff: { state: 'value', value: 80 }, slots: { rwa: 'rwa' } });
const rwa = term('rwa', 'RWA', value(10_000), value(11_000));
const kappa: TraceParameter = { name: 'kappa', source: 'configuration', value: 0.08, unit: { kind: 'ratio' } };
const read = (terms: readonly TermRead[]) => (id: string) => terms.find((t) => t.id === id);

describe('the formula of a term (ADR-0011 §6)', () => {
  it('writes the kind with symbols, then each side with its values, then the Diff', () => {
    const formula = formulaOf(capital, root, ['before', 'after'], read([rwa]), { before: [kappa], after: [kappa] });

    expect(formula.lines.map((l) => l.side)).toEqual(['symbolic', 'before', 'after', 'diff']);
    expect(formula.lines[0]!.tex).toBe('K = \\kappa \\cdot \\htmlData{operand=0}{\\mathrm{RWA}}');
    expect(formula.lines[1]!.tex).toBe('K = 0.08 \\cdot \\htmlData{operand=0}{10{,}000.00} = \\mathbf{800.00}');
    expect(formula.lines[2]!.tex).toBe('K = 0.08 \\cdot \\htmlData{operand=0}{11{,}000.00} = \\mathbf{880.00}');
    expect(formula.lines[3]!.tex).toBe('\\Delta K = \\mathbf{80.00}');
    expect(formula.operands).toEqual([{ index: 0, termId: 'rwa' }]);
  });

  it('on a case priced on one side writes that side alone, with no Before line and no Diff (§4)', () => {
    const after = term('capital', 'K', undefined, value(880), { slots: { rwa: 'rwa' } });

    const formula = formulaOf(capital, after, ['after'], read([term('rwa', 'RWA', undefined, value(11_000))]), { after: [kappa] });

    expect(formula.lines.map((l) => l.side)).toEqual(['symbolic', 'after']);
  });

  it('keeps an absent operand as its symbol, marked, and never writes it as 0', () => {
    const rw = term('rw', 'RW', value(0.3), value(0.3), { slots: { table: 'rw/table' } });
    const floor = term('rw/floor', '\\mathrm{RW}_{floor}', absent, absent);
    const table = term('rw/table', '\\mathrm{RW}_{table}', value(0.3), value(0.3), { unit: { kind: 'ratio' } });

    const formula = formulaOf(kind('\\mathrm{RW} = \\max(\\slot{table}, \\slot{floor})'), rw, ['after'], read([table, floor]), {});

    // A role the provider did not name is the child {id}/{role} (ADR-0011 §2).
    expect(formula.lines[1]!.tex).toBe('\\mathrm{RW} = \\max(\\htmlData{operand=0}{0.3}, \\htmlData{operand=1, state=absent}{\\mathrm{RW}_{floor}}) = \\mathbf{0.30}');
    expect(formula.lines[1]!.tex).not.toMatch(/\{0\}/);
  });

  it('keeps an operand not yet read as its symbol, pending, and names the terms to read', () => {
    const formula = formulaOf(capital, root, ['after'], () => undefined, { after: [kappa] });

    expect(formula.lines[1]!.tex).toContain('\\htmlData{operand=0, state=pending}{\\mathrm{rwa}}');
    expect(operandTargets(capital, root)).toEqual(['rwa']);
  });

  it('brackets a negative operand and writes a parameter it was not given as its symbol', () => {
    const pfe = term('pfe', 'PFE', undefined, value(50), { slots: { v: 'rc/v' } });
    const v = term('rc/v', 'V', undefined, value(-1_250.5));

    const formula = formulaOf(kind('m = \\param{F} + \\slot{v}'), pfe, ['after'], read([v]), { after: [] });

    expect(formula.lines[1]!.tex).toBe('m = F + \\htmlData{operand=0}{(-1{,}250.50)} = \\mathbf{50.00}');
  });

  it('says a side with no value in words in place of its figure', () => {
    const unavailable = term('capital', 'K', undefined, { state: 'unavailable', reason: 'Its CSA is one-way.', check: { status: 'notChecked' }, written: true }, { slots: { rwa: 'rwa' } });

    const formula = formulaOf(capital, unavailable, ['after'], read([rwa]), { after: [kappa] });

    expect(formula.lines[1]!.tex).toMatch(/= \\text\{unavailable\}$/);
  });

  it('typesets every line under strict KaTeX, its operands carrying their index and state', () => {
    const multiplier = kind('m = \\min\\left(1, \\param{F} + (1 - \\param{F}) \\exp\\left(\\frac{\\slot{v} - \\slot{c}}{2 (1 - \\param{F})\\, \\slot{addon}}\\right)\\right)', { unit: { kind: 'ratio' } });
    const m = term('m', 'm', undefined, value(0.97), { unit: { kind: 'ratio' }, slots: { v: 'v', c: 'c', addon: 'addon' } });
    const terms = [term('v', 'V', undefined, value(-10)), term('c', 'C', undefined, absent), term('addon', '\\mathrm{AddOn}', undefined, value(100))];

    const formula = formulaOf(multiplier, m, ['after'], read(terms), { after: [{ name: 'F', source: 'regulation', value: 0.05 }] });

    for (const line of formula.lines) {
      const set = typeset(line.tex, true);
      if ('error' in set) throw new Error(`${line.tex}: ${set.error}`);
      expect(set.html).toContain('data-operand="0"');
    }
    const after = typeset(formula.lines[1]!.tex, true);
    expect('html' in after && after.html).toContain('data-state="absent"');
  });

  it('refuses what strict KaTeX refuses, and trusts no extension but \\htmlData', () => {
    expect(typeset('\\href{https://example.invalid}{x}')).toHaveProperty('error');
    expect(typeset('\\htmlClass{x}{y}')).toHaveProperty('error');
    expect(typeset('\\htmlData{onclick=x}{y}')).toHaveProperty('error');
    expect(typeset('\\frac{1}{')).toHaveProperty('error');
  });
});

describe('symbols, numbers and words', () => {
  it('writes symbols as mathematics does', () => {
    expect(symbolTex('K')).toBe('K');
    expect(symbolTex('RWA')).toBe('\\mathrm{RWA}');
    expect(symbolTex('\\mathrm{AddOn}_{hs}')).toBe('\\mathrm{AddOn}_{hs}');
    expect(symbolTex('Exposure class')).toBe('\\text{Exposure class}');
    expect(symbolTex('50% & more')).toBe('\\text{50\\% \\& more}');
    expect(parameterTex('kappa')).toBe('\\kappa');
    expect(parameterTex('Sigma')).toBe('\\Sigma');
    expect(parameterTex('SF')).toBe('\\mathrm{SF}');
  });

  it('formats an amount to the cent and any other figure to six digits', () => {
    expect(numberTex(1_234_567.891, { kind: 'amount' })).toBe('1{,}234{,}567.89');
    expect(numberTex(0.973412345, { kind: 'ratio' })).toBe('0.973412');
    expect(numberTex(-0.5, undefined)).toBe('(-0.5)');
    expect(() => numberTex(Number.NaN, undefined)).toThrow(/not a finite number/);
  });

  it('says states, checks and Diffs in words', () => {
    expect(stateText('unavailable', 'Its CSA is one-way.')).toBe('unavailable: Its CSA is one-way.');
    expect(stateText('notReturned', undefined, 'Engine 2')).toBe('not returned by Engine 2');
    expect(valueText(absent, { kind: 'amount' })).toBe('absent');
    expect(valueText(value('Bank'), { kind: 'text' })).toBe('Bank');
    expect(checkText({ status: 'matches' }, 1, undefined)).toBe('matches its formula');
    expect(checkText({ status: 'inconsistent', computed: 12_400_000 }, 12_380_000, { kind: 'amount' }))
      .toBe('does not reconcile: 12,400,000.00 by its terms, 12,380,000.00 returned');
    expect(checkText({ status: 'notChecked', reason: 'An operand is unavailable.' }, 1, undefined)).toBe('not checked: An operand is unavailable.');
    expect(checkText({ status: 'notApplicable' }, 'Bank', undefined)).toBeUndefined();
    expect(diffText({ state: 'unavailable', reason: 'Before is unavailable.' }, undefined)).toBe('unavailable: Before is unavailable.');
    expect(diffText({ state: 'notNumeric', changed: true }, undefined)).toBe('changed');
  });
});

describe('the table a lookup read (ADR-0011 §6)', () => {
  const table: TraceParameter = {
    name: 'rwTable', source: 'regulation', scope: 'rw/table', unit: { kind: 'ratio' },
    matrix: { rows: ['AAA to AA-', 'A+ to A-'], columns: ['Base'], values: [[0.2], [0.3]] },
  };
  const weight = kind('\\mathrm{RW}_{table} = \\mathrm{lookup}(\\slot{bucket})', {
    expression: { op: 'lookup', table: 'rwTable', row: { op: 'slot', name: 'bucket' } },
  });
  const tableTerm = term('rw/table', '\\mathrm{RW}_{table}', undefined, value(0.3), { slots: { bucket: 'rw/bucket' } });

  it('marks the row the lookup took on that side', () => {
    const rows = lookupRows(weight, tableTerm, 'after', read([term('rw/bucket', 'Table row', undefined, value('A+ to A-'))]));

    expect(rows).toEqual({ rwTable: 'A+ to A-' });
    const tex = matrixTex(table, rows.rwTable);
    const set = typeset(tex, true);
    if ('error' in set) throw new Error(set.error);
    expect(set.html.match(/data-row="marked"/g)).toHaveLength(2);
    expect(tex).toContain('\\text{lookup}');
  });

  it('marks nothing while the row operand has no text', () => {
    expect(lookupRows(weight, tableTerm, 'after', () => undefined)).toEqual({});
  });
});

describe('a leaf column formula with a row\'s values', () => {
  it('substitutes the row\'s fields and writes the column\'s value', () => {
    const tex = leafFormulaTex('H = \\min(1, \\field{supervisoryHaircut} \\cdot \\field{scale})', { supervisoryHaircut: 0.04, scale: 0.5, haircut: 0.02 },
      () => ({ kind: 'ratio' }), 'haircut');

    expect(tex).toBe('H = \\min(1, 0.04 \\cdot 0.5) = \\mathbf{0.02}');
    expect(typeset(tex)).toHaveProperty('html');
  });
});
