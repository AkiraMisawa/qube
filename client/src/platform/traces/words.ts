import type { Side, SideValue, TermRead, TraceHeader, TraceParameter, TraceUnit } from './contracts';
import { checkText, diffText, stateText, valueText } from './formula';

// What the tree, the drill-in list and the formula pane say of a term, in words (design guidelines §7: a state is a
// word as well as a colour). Pure, so the three surfaces cannot say it differently.

export const SIDE_TITLE: Readonly<Record<Side, string>> = { before: 'Before', after: 'After' };

/** The provider that wrote a side, for `not returned by <provider>`. */
export function providerOf(header: TraceHeader, side: Side): string | undefined {
  const engine = header.origins.find((o) => o.side === side)?.engine;
  return engine === undefined || engine === null ? undefined : engine.name;
}

/** A unit in words: an amount's currency, the trace's reporting currency where the unit names none. */
export function unitText(unit: TraceUnit | null | undefined, header: TraceHeader): string {
  if (unit === undefined || unit === null || unit.kind === 'text') return '';
  if (unit.kind === 'amount') return unit.currency ?? header.origins.find((o) => o.reportingCurrency)?.reportingCurrency ?? 'amount';
  return unit.kind;
}

/** A side's figure or word, as a cell holds it: a number for the grid to align and judge, else its word. */
export function sideCell(side: SideValue | null | undefined, unit: TraceUnit | null | undefined): number | string | undefined {
  if (side === undefined || side === null) return undefined;
  if (side.state === 'value' && typeof side.value === 'number') return side.value;
  return valueText(side, unit);
}

/** A Diff as a cell holds it: the change, or the word for why there is none; nothing on a case priced on one side. */
export function diffCell(term: TermRead): number | string | undefined {
  const diff = term.diff;
  if (diff === undefined || diff === null) return undefined;
  if (diff.state === 'value' && typeof diff.value === 'number') return diff.value;
  return diffText(diff, term.unit);
}

/**
 * The state column: each side's state where it has no value, and its reconciliation where it has a formula. A side's
 * name leads only where two sides are shown; `matches` when every side reconciles.
 */
export function stateCell(term: TermRead, header: TraceHeader): string {
  const both = header.sides.length === 2;
  const parts: string[] = [];
  let checked = 0;
  for (const side of header.sides) {
    const value = term[side];
    if (value === undefined || value === null) continue;
    const lead = both ? `${SIDE_TITLE[side]} ` : '';
    if (value.state !== 'value') { parts.push(`${lead}${stateText(value.state, null, providerOf(header, side))}`); continue; }
    if (value.check.status === 'matches') { checked++; continue; }
    const check = checkText(value.check, value.value, term.unit);
    if (check !== undefined) parts.push(`${lead}${check}`);
  }
  if (parts.length === 0 && checked > 0) return 'matches';
  return parts.join(' · ');
}

/** The operand that governed a max, min or clamp on each side, by its role. */
export function governedCell(term: TermRead, header: TraceHeader): string {
  const marks = header.sides.flatMap((side) => {
    const governed = term[side]?.governed;
    return governed === undefined || governed === null ? [] : [[side, typeof governed === 'number' ? `operand ${governed + 1}` : governed] as const];
  });
  if (marks.length === 0) return '';
  if (marks.every(([, g]) => g === marks[0]![1])) return marks[0]![1];
  return marks.map(([side, g]) => `${SIDE_TITLE[side]} ${g}`).join(' · ');
}

/** The citation of a term: the rule a side applied where it is narrower than its kind's, else the kind's. */
export function citationCell(term: TermRead, header: TraceHeader): string {
  const own = [...new Set(header.sides.flatMap((side) => term[side]?.citation ?? []))];
  return own.length > 0 ? own.join(' · ') : term.citation ?? '';
}

/** The figure a drill-in row leads with: the Diff where both sides were priced, else the one side's value. */
export function leadText(term: TermRead, header: TraceHeader): string {
  if (header.sides.length === 2) {
    const diff = term.diff;
    return diff === undefined || diff === null ? '' : `Diff ${diffText(diff, term.unit)}`;
  }
  const side = term[header.sides[0]!];
  return side === undefined || side === null ? '' : valueText(side, term.unit);
}

/** Where a parameter comes from, in words: the regulation's paragraph, the configuration's key and version, … */
export function parameterSource(parameter: TraceParameter): string {
  switch (parameter.source) {
    case 'regulation': return parameter.citation ? `regulation, ${parameter.citation}` : 'regulation';
    case 'configuration': return parameter.config ? `configuration ${parameter.config.key}, ${parameter.config.version}` : 'configuration';
    case 'input': return parameter.input ? `input ${parameter.input.entity} · ${parameter.input.field}` : 'input';
    case 'data': return parameter.data ? `data ${parameter.data.source}, ${parameter.data.version}` : 'data';
    case 'derived': return 'derived';
  }
}
