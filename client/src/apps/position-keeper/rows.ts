import type { PkPosition, PkView, Mark, PositionCounterparty, PositionHedge, PositionPnl, PositionRisk, TableRead } from './contracts';

// The rows the Position Keeper page's grids show, made from what the server answered (ADR-0019). Nothing here computes a figure
// or a mark: a row joins what two answers say of the same thing, and a cell the server left out stays empty.

/** How `tables.read` writes a cell that holds nothing, as the command line types it (ADR-0016 note 38). */
const EMPTY = 'empty';

/** The marks of a row as its cell says them. None is an empty cell: the figure is the engine's own. */
export const marksSaid = (marks: readonly Mark[]): string => marks.join(' · ');

/** A table's cell by its field's name; undefined where the cell is empty. A field the answer does not hold is a defect. */
function cellOf(table: TableRead, row: TableRead['rows'][number], field: string): string | undefined {
  const at = table.fields.indexOf(field);
  const cell = at < 0 ? undefined : row.cells[at];
  if (cell === undefined) throw new Error(`The server answered row ${row.key} without its ${field}.`);
  return cell === EMPTY ? undefined : cell;
}

export interface HedgeRow extends PositionHedge {
  /** The hedge's terms, each as typed: `1bn`, `5y`, `1.2%`. */
  readonly terms: Readonly<Record<string, string | undefined>>;
}

/** Each what-if hedge: its terms from its table, and its value and who computed it from the position. */
export function hedgeRows(view: PkView): readonly HedgeRow[] {
  return view.hedges.rows.map((row) => {
    const priced = view.position.hedges.find((h) => h.hedge === row.key);
    if (priced === undefined) throw new Error(`The position holds no figure of hedge ${row.key}, which the hedges table lists.`);
    return { ...priced, terms: Object.fromEntries(view.hedges.fields.map((field) => [field, cellOf(view.hedges, row, field)])) };
  });
}

export interface MoveRow {
  /** The row's key in the moves table: `kind/input`. */
  readonly key: string;
  readonly kind: string;
  readonly input: string;
  /** What a plain number typed as the move is in, `bp` or `quote`; none where the result set declares no unit for the kind, and no move can be typed. */
  readonly typedIn: string | undefined;
  /** The move held, as the server writes it: `10` absolute, `1 %` of the input's level; none where none is held. */
  readonly move: string | undefined;
  /** What the move predicts; none where no move is held. */
  readonly pnl: PositionPnl | undefined;
}

/** Each input a move can be held for, with the move held and what it predicts. */
export function moveRows(view: PkView): readonly MoveRow[] {
  return view.moves.rows.map((row) => {
    const kind = cellOf(view.moves, row, 'kind');
    const input = cellOf(view.moves, row, 'input');
    if (kind === undefined || input === undefined) throw new Error(`The moves table answered row ${row.key} without its kind and input.`);
    const move = cellOf(view.moves, row, 'move');
    const pnl = view.position.pnl.find((line) => line.kind === kind && line.input === input);
    if ((move === undefined) !== (pnl === undefined))
      throw new Error(`The moves table and the position disagree on whether a move of ${row.key} is held.`);
    return { key: row.key, kind, input, typedIn: cellOf(view.moves, row, 'typedIn'), move, pnl };
  });
}

/** What the risk grid can be narrowed to: each kind and input the position has risk on, in the server's order. */
export function riskInputs(risk: readonly PositionRisk[]): readonly string[] {
  return [...new Set(risk.map(riskInputOf))];
}

/** A risk line's kind and input, as the page's control names them. */
export const riskInputOf = (line: Pick<PositionRisk, 'kind' | 'input'>): string => `${line.kind} ${line.input}`;

/** A risk line's key among the lines: its kind, its input and its key, which no two lines share. */
export const riskKey = (line: PositionRisk): string => `${line.kind}/${line.input}/${line.key ?? ''}`;

/** A counterparty's row: its total as the server summed it, and each metric's value and standard error. */
export interface XvaRow extends PositionCounterparty {
  readonly byMetric: Readonly<Record<string, { readonly value: number; readonly stdError: number } | undefined>>;
}

/** The metrics the book has values of, in the order the engine wrote them. */
export const metricsOf = (position: Pick<PkPosition, 'values'>): readonly string[] => [...new Set(position.values.map((v) => v.metric))];

/** Each counterparty once, in the server's order (the largest cost first), with its values by metric beside its total. */
export function xvaRows(position: Pick<PkPosition, 'values' | 'byCounterparty'>): readonly XvaRow[] {
  const listed = new Set(position.byCounterparty.map((c) => c.counterparty));
  const stray = position.values.find((v) => !listed.has(v.counterparty));
  if (stray !== undefined) throw new Error(`The position gives a value of ${stray.counterparty} and no total of it.`);
  return position.byCounterparty.map((party) => ({
    ...party,
    byMetric: Object.fromEntries(position.values.filter((v) => v.counterparty === party.counterparty).map((v) => [v.metric, { value: v.value, stdError: v.stdError }])),
  }));
}

/** A risk line's tenor as a trader names it, or its key as the engine wrote it where it is no tenor's day. */
export const tenorOf = (line: Pick<PositionRisk, 'tenor' | 'key'>): string | undefined => line.tenor ?? line.key;
