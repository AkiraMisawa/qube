import type { Side, SideValue, TermCheck, TermDiff, TermKind, TermRead, TermState, TraceExpr, TraceParameter, TraceUnit } from './contracts';

// The typeset formula of one term (ADR-0011 §6), as pure functions from the catalogue's TeX and the terms read: the
// kind's formula with each operand's symbol, then the same formula with each side's values substituted, then the
// Diff. Every operand that is a term is wrapped in \htmlData, the only extension the renderer trusts, so the view can
// select it; an operand with no value keeps its symbol, marked by its state, and is never written as 0. States,
// checks and Diffs are also said in words, for the reader who does not read colour or TeX.

const GREEK = new Set(['alpha', 'beta', 'gamma', 'delta', 'epsilon', 'zeta', 'eta', 'theta', 'iota', 'kappa', 'lambda', 'mu', 'nu', 'xi',
  'pi', 'rho', 'sigma', 'tau', 'upsilon', 'phi', 'chi', 'psi', 'omega']);
const UPPER_GREEK = new Set(['Gamma', 'Delta', 'Theta', 'Lambda', 'Xi', 'Pi', 'Sigma', 'Upsilon', 'Phi', 'Psi', 'Omega']);

/** A text set inside TeX's \text (or \textbf), its specials escaped. */
export function texText(text: string, bold = false): string {
  return `\\${bold ? 'textbf' : 'text'}{${text.replace(/[\\{}$&#^_%~]/g, (c) => (c === '\\' ? '\\textbackslash ' : c === '^' ? '\\textasciicircum ' : c === '~' ? '\\textasciitilde ' : `\\${c}`))}}`;
}

/**
 * A symbol as TeX: the catalogue's TeX where it is TeX already (`\mathrm{RW}_{table}`), a single letter in italic as
 * mathematics writes one (`K`), a word upright (`RWA`), and anything else as text (`Exposure class`).
 */
export function symbolTex(symbol: string): string {
  if (symbol.includes('\\')) return symbol;
  if (/^[A-Za-z]$/.test(symbol)) return symbol;
  if (/^[A-Za-z][A-Za-z0-9]*$/.test(symbol)) return `\\mathrm{${symbol}}`;
  return texText(symbol);
}

/** A parameter's name as mathematics writes it: `kappa` is κ, `SF` upright, `F` italic. */
export function parameterTex(name: string): string {
  if (GREEK.has(name)) return `\\${name}`;
  if (UPPER_GREEK.has(name)) return `\\${name}`;
  return symbolTex(name);
}

const amount = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const general = new Intl.NumberFormat('en-US', { maximumSignificantDigits: 6 });

/** A number in words by its unit: an amount to the cent as the Metrics show it, any other figure to six digits. */
export function formatNumber(value: number, unit: TraceUnit | null | undefined): string {
  if (!Number.isFinite(value)) throw new Error(`A trace value is not a finite number: ${value}.`);
  return unit?.kind === 'amount' ? amount.format(value) : general.format(value);
}

/** A number as TeX: grouped without TeX's spacing after a comma, and in brackets when negative, as an operand. */
export function numberTex(value: number, unit: TraceUnit | null | undefined): string {
  const text = formatNumber(Math.abs(value), unit).replace(/,/g, '{,}');
  return value < 0 ? `(-${text})` : text;
}

/** A side's value in words: the figure, the text of a decision, or the state that stands where there is none. */
export function valueText(side: SideValue, unit: TraceUnit | null | undefined): string {
  if (side.state !== 'value') return stateText(side.state, side.reason);
  if (typeof side.value === 'number') return formatNumber(side.value, unit);
  if (typeof side.value === 'string') return side.value;
  throw new Error('A term in the value state carries no value.');
}

/** A state in words (design guidelines §7): `absent`, `unavailable: <reason>`, `not returned`. */
export function stateText(state: TermState, reason?: string | null, provider?: string): string {
  switch (state) {
    case 'value': return 'value';
    case 'absent': return 'absent';
    case 'unavailable': return reason === undefined || reason === null || reason === '' ? 'unavailable' : `unavailable: ${reason}`;
    case 'notReturned': return provider === undefined ? 'not returned' : `not returned by ${provider}`;
  }
}

/** A term's reconciliation in words, or nothing where it has no formula to be checked against. */
export function checkText(check: TermCheck, value: SideValue['value'], unit: TraceUnit | null | undefined): string | undefined {
  switch (check.status) {
    case 'matches': return 'matches its formula';
    case 'inconsistent': {
      if (typeof check.computed !== 'number' || typeof value !== 'number') return `does not reconcile${check.reason ? `: ${check.reason}` : ''}`;
      return `does not reconcile: ${formatNumber(check.computed, unit)} by its terms, ${formatNumber(value, unit)} returned`;
    }
    case 'notChecked': return `not checked${check.reason ? `: ${check.reason}` : ''}`;
    case 'notApplicable': return undefined;
  }
}

/** A Diff in words: the change, the text's change, or why there is none. */
export function diffText(diff: TermDiff, unit: TraceUnit | null | undefined): string {
  switch (diff.state) {
    case 'value':
      if (typeof diff.value !== 'number') throw new Error('A Diff in the value state carries no value.');
      return formatNumber(diff.value, unit);
    case 'absent': return 'absent';
    case 'unavailable': return diff.reason ? `unavailable: ${diff.reason}` : 'unavailable';
    case 'notNumeric': return diff.changed === true ? 'changed' : diff.changed === false ? 'unchanged' : '';
  }
}

/** The child playing a role of a term's formula: the one the provider named, else `{id}/{role}` (ADR-0011 §2). */
export function slotTarget(term: TermRead, role: string): string {
  return term.slots?.[role] ?? `${term.id}/${role}`;
}

/** The roles and parameters a TeX template names, in the order written. */
export function markersOf(tex: string): readonly { readonly marker: 'slot' | 'param' | 'field'; readonly name: string }[] {
  return [...tex.matchAll(/\\(slot|param|field)\{([^{}]*)\}/g)].map((m) => ({ marker: m[1] as 'slot' | 'param' | 'field', name: m[2]! }));
}

/** Replaces each marker of a template with what `replace` makes of it. */
export function substitute(tex: string, replace: (marker: 'slot' | 'param' | 'field', name: string) => string): string {
  return tex.replace(/\\(slot|param|field)\{([^{}]*)\}/g, (_, marker: 'slot' | 'param' | 'field', name: string) => replace(marker, name));
}

/** The parameters a term's formula names, and the tables its lookups read. */
export function parametersNamed(kind: TermKind): ReadonlySet<string> {
  const names = new Set(markersOf(kind.tex).filter((m) => m.marker === 'param').map((m) => m.name));
  for (const expr of nodesOf(kind.expression)) {
    if (expr.op === 'param' && typeof expr.name === 'string') names.add(expr.name);
    if (expr.op === 'lookup' && typeof expr.table === 'string') names.add(expr.table);
  }
  return names;
}

/** The term ids a term's formula reads as operands, so the view can read the ones that are not its children. */
export function operandTargets(kind: TermKind, term: TermRead): readonly string[] {
  return [...new Set(markersOf(kind.tex).filter((m) => m.marker === 'slot').map((m) => slotTarget(term, m.name)))];
}

/** A parameter by name among those in a term's scope; the narrowest scope wins, as the provider's own lookup would. */
export function parameterNamed(parameters: readonly TraceParameter[] | null | undefined, name: string): TraceParameter | undefined {
  const named = (parameters ?? []).filter((p) => p.name === name);
  return named.find((p) => p.scope !== undefined && p.scope !== null) ?? named[0];
}

/** One line of the typeset formula, and the side it is of. */
export interface FormulaLine { readonly side: Side | 'symbolic' | 'diff'; readonly tex: string }

/** What an `\htmlData` operand index stands for: the term it selects. */
export interface Operand { readonly index: number; readonly termId: string }

export interface Formula {
  readonly lines: readonly FormulaLine[];
  /** The operands the lines wrap, by their `data-operand` index. */
  readonly operands: readonly Operand[];
}

/**
 * The formula of a term: its kind's TeX with symbols, once per side with the values substituted and the term's value
 * after it, and the Diff when both sides were priced. `operand` reads a term the formula names, which is a child or a
 * term elsewhere in the trace; undefined while it has not been read.
 */
export function formulaOf(kind: TermKind, term: TermRead, sides: readonly Side[], operand: (id: string) => TermRead | undefined,
  parameters: Readonly<Partial<Record<Side, readonly TraceParameter[] | null | undefined>>>): Formula {
  const operands: Operand[] = [];
  const indexOf = (termId: string) => {
    const found = operands.find((o) => o.termId === termId);
    if (found !== undefined) return found.index;
    operands.push({ index: operands.length, termId });
    return operands.length - 1;
  };
  const wrap = (termId: string, content: string, state?: TermState | 'pending') =>
    `\\htmlData{operand=${indexOf(termId)}${state === undefined ? '' : `, state=${state}`}}{${content}}`;

  const symbolOf = (role: string) => {
    const target = operand(slotTarget(term, role));
    return target === undefined ? symbolTex(role) : symbolTex(target.symbol);
  };
  const symbolic = substitute(kind.tex, (marker, name) => {
    if (marker === 'slot') return wrap(slotTarget(term, name), symbolOf(name));
    if (marker === 'param') return parameterTex(name);
    return texText(name);
  });

  const lines: FormulaLine[] = [{ side: 'symbolic', tex: symbolic }];
  for (const side of sides) {
    const own = term[side];
    if (own === undefined || own === null) continue;
    const substituted = substitute(kind.tex, (marker, name) => {
      if (marker === 'slot') {
        const id = slotTarget(term, name);
        const target = operand(id);
        if (target === undefined) return wrap(id, symbolOf(name), 'pending');
        const value = target[side];
        // The side the case priced always reads every term; a term it does not have keeps its symbol.
        if (value === undefined || value === null) return wrap(id, symbolTex(target.symbol), 'absent');
        if (value.state !== 'value') return wrap(id, symbolTex(target.symbol), value.state);
        return wrap(id, typeof value.value === 'number' ? numberTex(value.value, target.unit) : texText(String(value.value)));
      }
      if (marker === 'param') {
        const parameter = parameterNamed(parameters[side], name);
        if (parameter?.value !== undefined && parameter.value !== null) return numberTex(parameter.value, parameter.unit);
        if (parameter?.text !== undefined && parameter.text !== null) return texText(parameter.text);
        return parameterTex(name);
      }
      return texText(name);
    });
    lines.push({ side, tex: `${substituted} = ${resultTex(own, term.unit)}` });
  }
  if (term.diff !== undefined && term.diff !== null && sides.length === 2) {
    lines.push({ side: 'diff', tex: `\\Delta ${symbolTex(term.symbol)} = ${diffTex(term.diff, term.unit)}` });
  }
  return { lines, operands };
}

function resultTex(side: SideValue, unit: TraceUnit | null | undefined): string {
  if (side.state !== 'value') return texText(stateText(side.state));
  if (typeof side.value === 'number') return `\\mathbf{${numberTex(side.value, unit)}}`;
  return texText(String(side.value));
}

function diffTex(diff: TermDiff, unit: TraceUnit | null | undefined): string {
  if (diff.state === 'value' && typeof diff.value === 'number') return `\\mathbf{${numberTex(diff.value, unit)}}`;
  return texText(diffText(diff, unit) || 'not a number');
}

/**
 * The row a lookup took, by the table it read (ADR-0011 §6: the table with the row the lookup took marked): a kind
 * whose expression is `lookup(table, row)` reads the row's label from the value of its row operand on that side.
 */
export function lookupRows(kind: TermKind, term: TermRead, side: Side, operand: (id: string) => TermRead | undefined): Readonly<Record<string, string>> {
  const rows: Record<string, string> = {};
  for (const expr of nodesOf(kind.expression)) {
    if (expr.op !== 'lookup' || typeof expr.table !== 'string') continue;
    const row = expr.row as TraceExpr | undefined;
    if (row?.op !== 'slot' || typeof row.name !== 'string') continue;
    const value = operand(slotTarget(term, row.name))?.[side];
    if (value?.state === 'value' && typeof value.value === 'string') rows[expr.table] = value.value;
  }
  return rows;
}

const isExpr = (value: unknown): value is TraceExpr =>
  typeof value === 'object' && value !== null && typeof (value as { op?: unknown }).op === 'string';

/** Every node of an expression, itself first: the one walk each reading of a formula shares. */
function* nodesOf(expr: TraceExpr | null | undefined): Generator<TraceExpr> {
  if (expr === null || expr === undefined) return;
  yield expr;
  for (const field of Object.values(expr)) {
    if (Array.isArray(field)) {
      for (const item of field) if (isExpr(item)) yield* nodesOf(item);
    } else if (isExpr(field)) yield* nodesOf(field);
  }
}

/** A matrix parameter as a TeX array, its row labels on the left, the looked-up row marked in bold and in words. */
export function matrixTex(parameter: TraceParameter, marked: string | undefined): string {
  const matrix = parameter.matrix;
  if (matrix === undefined || matrix === null) throw new Error(`Parameter ${parameter.name} is not a matrix.`);
  const head = ['', ...matrix.columns.map((c) => texText(c))].join(' & ');
  const body = matrix.rows.map((row, i) => {
    const numbers = (matrix.values[i] ?? []).map((v) => numberTex(v, parameter.unit));
    if (row !== marked) return [texText(row), ...numbers].join(' & ');
    // Each cell is marked on its own, since a group cannot span an array's columns; the words say it too.
    const cells = [texText(row, true), ...numbers.map((n) => `\\mathbf{${n}}`)];
    return cells.map((c, j) => `\\htmlData{row=marked}{${c}}${j === cells.length - 1 ? ' \\;\\leftarrow \\text{lookup}' : ''}`).join(' & ');
  });
  return `\\begin{array}{l|${'r'.repeat(matrix.columns.length)}} ${head} \\\\ \\hline ${body.join(' \\\\ ')} \\end{array}`;
}

/** A leaf column's formula with a row's values in place of its fields, and the column's own value after it. */
export function leafFormulaTex(tex: string, row: Readonly<Record<string, unknown>>, units: (field: string) => TraceUnit | null | undefined, result: string): string {
  const substituted = substitute(tex, (marker, name) => {
    if (marker !== 'field') return texText(name);
    const value = row[name];
    if (typeof value === 'number') return numberTex(value, units(name));
    if (value === undefined || value === null) return texText(`${name} absent`);
    return texText(String(value));
  });
  const value = row[result];
  return typeof value === 'number' ? `${substituted} = \\mathbf{${numberTex(value, units(result))}}` : substituted;
}
