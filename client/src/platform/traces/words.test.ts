import { describe, expect, it } from 'vitest';
import type { SideValue, TermRead, TraceHeader, TraceParameter } from './contracts';
import { mismatchText } from './LeafRows';
import { explainLine, explainTyped, traceAddressOf } from './open';
import { afterWord, allocationCell, allocationDiffCell, citationCell, diffCell, governedCell, governsMark, leadText, parameterGroups, parameterSource, sideCell, sidesHolding, stateCell, unitText } from './words';

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
    expect(sidesHolding(row, ['before', 'after'])).toBe('After only');
    expect(sidesHolding({ ...row, before: {} }, ['before', 'after'])).toBe('Before and After');
    expect(sidesHolding(row, ['after'])).toBe('');
    expect(sidesHolding(row, ['before'])).toBe('not on this side');
    expect(mismatchText(row, 'after', { leafSet: 's', entityField: 'id', columns: [{ name: 'haircut', label: 'Haircut' }] }))
      .toBe('Haircut does not reconcile: 0.08 by its formula, 0.1 returned');
  });
});

describe('a label after a word (design guidelines §10)', () => {
  it('reads in sentence case, and an initialism keeps its capitals', () => {
    expect(['Trade', 'CRIF line', 'IM-R', 'tree', 'Netting set'].map(afterWord)).toEqual(['trade', 'CRIF line', 'IM-R', 'tree', 'netting set']);
  });
});

describe('explain (ADR-0011 §7)', () => {
  it('reads the measure, then a path that may hold spaces', () => {
    expect(explainTyped('ccr.spotCapital')).toEqual({ measure: 'ccr.spotCapital', path: undefined });
    expect(explainTyped(' ccr.spotCapital  NS-001 CSA-A/m ')).toEqual({ measure: 'ccr.spotCapital', path: 'NS-001 CSA-A/m' });
    expect(() => explainTyped('  ')).toThrow(/needs the measure/);
  });

  it('writes the line that comes back to a term, and none for a path that would not read back as one', () => {
    const line = explainLine('simm.spotIm', 'collect/JFSA/RatesFX/IR/delta/JPY/OIS 1m');
    expect(line).toBe('explain simm.spotIm collect/JFSA/RatesFX/IR/delta/JPY/OIS 1m');
    expect(explainTyped(line!.slice('explain '.length))).toEqual({ measure: 'simm.spotIm', path: 'collect/JFSA/RatesFX/IR/delta/JPY/OIS 1m' });
    expect(explainLine('m', 'a  b')).toBeUndefined();
    expect(explainLine('m', ' a')).toBeUndefined();
  });

  it('refuses a panel that does not name its trace', () => {
    expect(() => traceAddressOf({ runId: 'r' })).toThrow(/names its Run, case, scenario and measure/);
  });
});

describe('which operand governs, and the allocation (ADR-0011 §1, §2, §6)', () => {
  it('marks the operand a max took, and says the side where the sides differ', () => {
    const both = header(['before', 'after']);
    expect(governsMark(term({ before: value(1, { governs: true }), after: value(2, { governs: true }) }), both)).toEqual({ text: 'Governs', governs: true });
    expect(governsMark(term({ before: value(1, { governs: false }), after: value(2, { governs: false }) }), both)).toEqual({ text: 'Not governing', governs: false });
    expect(governsMark(term({ before: value(1, { governs: false }), after: value(2, { governs: true }) }), both)).toEqual({ text: 'Governs on After', governs: true });
    expect(governsMark(term({ after: value(2) }), header(['after']))).toBeUndefined();
  });

  it('holds each side\'s allocation and its change, an absent side as nothing and a missing allocation as no figure', () => {
    const shared = (v: number) => ({ allocation: { method: 'euler', value: v } });
    expect(allocationCell(term({ after: value(2, shared(1.5)) }), 'after')).toBe(1.5);
    expect(allocationCell(term({ after: value(2) }), 'after')).toBeUndefined();
    expect(allocationDiffCell(term({ before: value(1, shared(0.5)), after: value(2, shared(1.5)) }))).toBe(1);
    const absent = { state: 'absent' as const, check: { status: 'notApplicable' as const }, written: false };
    expect(allocationDiffCell(term({ before: absent, after: value(2, shared(1.5)) }))).toBe(1.5);
    expect(allocationDiffCell(term({ before: value(1), after: value(2, shared(1.5)) }))).toBeUndefined();
  });
});

describe('the parameters a term shows (ADR-0011 §6\'s note of 2026-11-08)', () => {
  it('shows first those its formula names and those scoped to it, and folds those of the terms above it', () => {
    const p = (name: string, scope?: string): TraceParameter => ({ name, source: 'regulation', value: 1, ...(scope === undefined ? {} : { scope }) });
    const groups = parameterGroups(new Set(['rw', 'alpha']), 'bed/rose', [
      p('rw', 'bed/rose'), p('cr', 'bed/rose'), p('rho', 'bed'), p('alpha'), p('kappa'), p('rw', 'bed'), p('psi', 'garden'),
    ]);
    // Named or its own first; a trace-wide one the formula does not name (kappa) is another formula's, not folded here.
    expect(groups).toEqual({ own: ['rw', 'cr', 'alpha'], above: ['rho', 'psi'] });
  });
});
