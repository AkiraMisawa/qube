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

/**
 * Which sides hold a row, as a row of rows says it: `Before and After`, or the one side `only`, where both sides were
 * priced; nothing where one was, unless the row is not on it.
 */
export function sidesHolding(row: Readonly<Partial<Record<Side, unknown>>>, sides: readonly Side[]): string {
  const on = sides.filter((s) => row[s] !== undefined && row[s] !== null);
  if (sides.length === 1) return on.length === 1 ? '' : 'not on this side';
  if (on.length === 2) return 'Before and After';
  const [only] = on;
  return only === undefined ? 'on neither side' : `${SIDE_TITLE[only]} only`;
}

/**
 * A label as it reads after another word, in sentence case (design guidelines §10): `Keeper` reads `keeper`, and a
 * word that is an initialism, such as `ID`, keeps its capitals.
 */
export function afterWord(label: string): string {
  return /^[A-Z][a-z]/.test(label) ? label[0]!.toLowerCase() + label.slice(1) : label;
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

/**
 * Whether a term is the operand its parent took (ADR-0011 §2, §6): `Governs` or `Not governing` where every priced side
 * says alike, else the side it governs on. None for a term that is no operand of a max, a min or a clamp.
 */
export function governsMark(term: TermRead, header: TraceHeader): { readonly text: string; readonly governs: boolean } | undefined {
  const marks = header.sides.flatMap((side) => {
    const governs = term[side]?.governs;
    return governs === undefined || governs === null ? [] : [[side, governs] as const];
  });
  if (marks.length === 0) return undefined;
  if (marks.every(([, g]) => g === marks[0]![1])) return marks[0]![1] ? { text: 'Governs', governs: true } : { text: 'Not governing', governs: false };
  const on = marks.filter(([, g]) => g).map(([side]) => SIDE_TITLE[side]);
  return { text: `Governs on ${on.join(' and ')}`, governs: true };
}

/** A term's allocation on a side as a cell holds it: the figure, nothing where the side gave none or the side is absent. */
export function allocationCell(term: TermRead, side: Side): number | undefined {
  const value = term[side];
  return value?.allocation?.value ?? undefined;
}

/**
 * The change in a term's allocation, After − Before, where both sides were priced: an absent side contributes nothing,
 * and a side with a value and no allocation leaves it blank rather than a figure made up.
 */
export function allocationDiffCell(term: TermRead): number | undefined {
  const share = (side: Side) => {
    const value = term[side];
    if (value === undefined || value === null) return undefined;
    if (value.allocation !== undefined && value.allocation !== null) return value.allocation.value;
    return value.state === 'absent' ? 0 : undefined;
  };
  const before = share('before');
  const after = share('after');
  return before === undefined || after === undefined ? undefined : after - before;
}

/**
 * The parameters the formula pane shows of a term, in two groups (ADR-0011 §6's note of 2026-11-08): its own, those its
 * formula names and those scoped to it, shown first; and those scoped to a term above it that its formula does not
 * name, folded. A parameter of the whole trace that the formula does not name is the formula's that names it.
 */
export function parameterGroups(named: ReadonlySet<string>, termId: string, parameters: readonly TraceParameter[]):
  { readonly own: readonly string[]; readonly above: readonly string[] } {
  const own = [...new Set(parameters.filter((p) => named.has(p.name) || p.scope === termId).map((p) => p.name))];
  const above = [...new Set(parameters.filter((p) => !named.has(p.name) && p.scope !== undefined && p.scope !== null && p.scope !== termId).map((p) => p.name))]
    .filter((name) => !own.includes(name));
  return { own, above };
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
