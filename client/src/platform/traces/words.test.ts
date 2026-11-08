import { describe, expect, it } from 'vitest';
import type { SideValue, TermRead, TraceHeader } from './contracts';
import { membershipOf, mismatchText } from './LeafRows';
import { explainTyped, traceAddressOf } from './open';
import { citationCell, diffCell, governedCell, leadText, parameterSource, sideCell, stateCell, unitText } from './words';

// ADR-0011 §4, §6, §7: what the tree, the drill-in list and the leaf rows say of a term, in words. Synthetic.

const header = (sides: TraceHeader['sides']): TraceHeader => ({
  runId: 'run-1', case: 'base', scenario: 'base', measure: 'm', method: { id: 'method', version: '1' }, depth: 'trade', sides,
  origins: sides.map((side) => ({ side, engine: { name: 'Engine 2', version: '1' }, reportingCurrency: 'JPY' })), roots: [],
});
const value = (v: number | string, extra: Partial<SideValue> = {}): SideValue => ({ state: 'value', value: v, check: { status: 'matches' }, written: true, ...extra });
const term = (extra: Partial<TermRead>): TermRead => ({ id: 't', kind: 'k', label: 'T', symbol: 'T', childCount: 0, unit: { kind: 'amount' }, ...extra });

describe('a term in words (ADR-0011 §6)', () => {
  it('says matches once every priced side reconciles, and each side that does not, by name', () => {
    expect(stateCell(term({ before: value(1), after: value(2) }), header(['before', 'after']))).toBe('matches');
    const one = term({ before: value(1), after: { state: 'notReturned', check: { status: 'notChecked' }, written: false } });
    expect(stateCell(one, header(['before', 'after']))).toBe('After not returned by Engine 2');
    const off = term({ after: value(2, { check: { status: 'inconsistent', computed: 3 } }) });
    expect(stateCell(off, header(['after']))).toBe('does not reconcile: 3.00 by its terms, 2.00 returned');
  });

  it('holds a figure as a number and a state as its word, never 0', () => {
    expect(sideCell(value(12.5), { kind: 'amount' })).toBe(12.5);
    expect(sideCell({ state: 'absent', check: { status: 'notApplicable' }, written: true }, { kind: 'amount' })).toBe('absent');
    expect(sideCell(undefined, { kind: 'amount' })).toBeUndefined();
    expect(diffCell(term({ diff: { state: 'unavailable', reason: 'Before is unavailable.' } }))).toBe('unavailable: Before is unavailable.');
    expect(diffCell(term({}))).toBeUndefined();
  });

  it('names the operand that governed once where both sides agree, and each side where they differ', () => {
    expect(governedCell(term({ before: value(1, { governed: 'm' }), after: value(1, { governed: 'm' }) }), header(['before', 'after']))).toBe('m');
    expect(governedCell(term({ before: value(1, { governed: 'm' }), after: value(1, { governed: 'u' }) }), header(['before', 'after']))).toBe('Before m · After u');
  });

  it('cites the rule a side applied where it is narrower than its kind\'s', () => {
    expect(citationCell(term({ citation: 'CRE20', after: value('Bank', { citation: 'CRE20.16' }) }), header(['after']))).toBe('CRE20.16');
    expect(citationCell(term({ citation: 'CRE20', after: value(1) }), header(['after']))).toBe('CRE20');
  });

  it('leads a drill-in row with the Diff, or with the value where one side was priced (§4)', () => {
    expect(leadText(term({ diff: { state: 'value', value: 5 } }), header(['before', 'after']))).toBe('Diff 5.00');
    expect(leadText(term({ after: value(7) }), header(['after']))).toBe('7.00');
  });

  it('writes an amount\'s unit as the trace\'s currency', () => {
    expect(unitText({ kind: 'amount' }, header(['after']))).toBe('JPY');
    expect(unitText({ kind: 'ratio' }, header(['after']))).toBe('ratio');
    expect(unitText({ kind: 'text' }, header(['after']))).toBe('');
  });

  it('says where a parameter comes from', () => {
    expect(parameterSource({ name: 'kappa', source: 'configuration', config: { key: 'ccr.capitalRatio', version: 'v1' } })).toBe('configuration ccr.capitalRatio, v1');
    expect(parameterSource({ name: 'alpha', source: 'regulation', citation: 'CRE52.1' })).toBe('regulation, CRE52.1');
  });
});

describe('a leaf row in words (ADR-0011 §6)', () => {
  const row = { leafSet: 's', entity: 'T-1', before: null, after: { haircut: 0.1 }, beforeMismatches: [], afterMismatches: [{ column: 'haircut', returned: 0.1, computed: 0.08 }] };

  it('says which sides hold it, and which computed column does not reconcile', () => {
    expect(membershipOf(row, ['before', 'after'])).toBe('After only');
    expect(membershipOf(row, ['after'])).toBe('');
    expect(mismatchText(row, 'after', { leafSet: 's', entityField: 'id', columns: [{ name: 'haircut', label: 'Haircut' }] }))
      .toBe('Haircut does not reconcile: 0.08 by its formula, 0.1 returned');
  });
});

describe('explain (ADR-0011 §7)', () => {
  it('reads the measure, then a path that may hold spaces', () => {
    expect(explainTyped('ccr.spotCapital')).toEqual({ measure: 'ccr.spotCapital', path: undefined });
    expect(explainTyped(' ccr.spotCapital  NS-001 CSA-A/m ')).toEqual({ measure: 'ccr.spotCapital', path: 'NS-001 CSA-A/m' });
    expect(() => explainTyped('  ')).toThrow(/needs the measure/);
  });

  it('refuses a panel that does not name its trace', () => {
    expect(() => traceAddressOf({ runId: 'r' })).toThrow(/names its Run, case, scenario and measure/);
  });
});
