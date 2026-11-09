import type { ScenariosMade, ShiftEdit, ShiftRow } from './contracts';

// Scenarios on the command line (ADR-0010 §4 and its note of 2026-11-09): what the page reads of a line before the
// server reads the shifts. Which index, tenor or size a word is, and whether a shift resolves, is the server's to say;
// nothing here guesses one.

/**
 * `scenario s1 jpy_ois +10bp; usdjpy -5%` as typed: the name, its first word, then the shifts as written, separated by
 * semicolons. A line with no name or no shift is refused here by name.
 */
export function scenarioTyped(text: string): { readonly name: string; readonly shifts: string } {
  const trimmed = text.trim();
  const space = trimmed.search(/\s/);
  const name = space < 0 ? trimmed : trimmed.slice(0, space);
  const shifts = space < 0 ? '' : trimmed.slice(space).trim();
  if (name === '' || shifts === '')
    throw new Error('scenario needs a name and its shifts separated by semicolons, such as scenario s1 jpy_ois +10bp; usdjpy -5%.');
  return { name, shifts };
}

/**
 * How a `scenario` line fills, for the line to show before Enter: the name, then each shift as written, one a
 * semicolon. A piece left empty between two semicolons is shown as one, since the server refuses it.
 */
export function scenarioParts(text: string): readonly { readonly name: string; readonly value?: string; readonly required: boolean }[] {
  const trimmed = text.trim();
  const space = trimmed.search(/\s/);
  const name = space < 0 ? trimmed : trimmed.slice(0, space);
  const shifts = (space < 0 ? '' : trimmed.slice(space)).split(';').map((p) => p.trim());
  const typed = shifts.length === 1 && shifts[0] === '' ? [] : shifts;
  return [
    name === '' ? { name: 'name', required: true } : { name: 'name', value: name, required: true },
    ...(typed.length === 0 ? [{ name: 'shifts', required: true }]
      : typed.map((shift, i) => (shift === '' && i < typed.length - 1 ? { name: '?', value: 'an empty shift', required: true } : shift === '' ? { name: 'shift', required: false } : { name: 'shift', value: shift, required: true }))),
  ];
}

/** What a scenario command said back: the scenarios it made, and those of the same name it replaced. */
export const scenariosText = ({ made, replaced }: Pick<ScenariosMade, 'made' | 'replaced'>): string => {
  const kept = made.filter((m) => !replaced.some((r) => r.toLowerCase() === m.toLowerCase()));
  const said = [
    ...(kept.length === 0 ? [] : [`made the scenario${kept.length === 1 ? '' : 's'} ${kept.join(', ')}`]),
    ...(replaced.length === 0 ? [] : [`replaced the shifts of ${replaced.join(', ')}`]),
  ].join('; ');
  return `${said.charAt(0).toUpperCase()}${said.slice(1)}.`;
};

// The Scenarios grid (ADR-0010 §4): one row per shift in the order made, then blank rows a paste may fill. Its
// positions are the grid's; what a cell means is the server's, which reads each as its column reads it.

/** The grid's columns that take a value, in the grid's order; Category and Shift type follow the index and the size. */
export const SHIFT_COLUMNS = ['scenario', 'category', 'index', 'factor', 'type', 'size', 'description'] as const;
export type ShiftColumn = (typeof SHIFT_COLUMNS)[number];

/** How many blank rows the grid ends in: what a block pasted onto the first of them may fill. */
export const BLANK_ROWS = 100;

/** A row of the grid: a shift, or a blank row, at its place counted from 1. */
export interface ShiftGridRow {
  readonly key: string;
  readonly position: number;
  readonly shift: ShiftRow | undefined;
}

/** The grid's rows: every shift in the order made, then the blank rows. */
export function shiftGridRows(shifts: readonly ShiftRow[], blanks: number = BLANK_ROWS): readonly ShiftGridRow[] {
  return [
    ...shifts.map((shift, i) => ({ key: shift.id, position: i + 1, shift })),
    ...Array.from({ length: blanks }, (_, k) => ({ key: `blank-${k + 1}`, position: shifts.length + k + 1, shift: undefined })),
  ];
}

/** A cell of the grid by position, as a paste or a clear names it. */
export interface GridCell { readonly row: number; readonly column: number }

/** A rectangle of the grid, as a paste or a clear targets it. */
export interface GridRange { readonly topRow: number; readonly leftColumn: number; readonly rowCount: number; readonly columnCount: number }

/**
 * The edits a block pasted onto the grid makes (ADR-0010 §4): a row's cells as pasted, by column, in the grid's order.
 * A blank row with nothing pasted into it makes nothing; the server reads every cell and refuses the paste whole for one
 * that does not resolve.
 */
export function pastedEdits(rows: readonly ShiftGridRow[], targets: readonly GridRange[], valueFor: (cell: GridCell) => string): readonly ShiftEdit[] {
  return editsOver(rows, targets, valueFor).filter((e) => e.id !== null || Object.values(e.cells).some((v) => v !== null && v.trim() !== ''));
}

/**
 * The edits Delete makes over the Selection: each shift's cells covered cleared, a row with none left removed by the
 * server. A blank row has nothing to clear.
 */
export function clearedEdits(rows: readonly ShiftGridRow[], targets: readonly GridRange[]): readonly ShiftEdit[] {
  return editsOver(rows, targets, () => '').filter((e) => e.id !== null);
}

function editsOver(rows: readonly ShiftGridRow[], targets: readonly GridRange[], valueFor: (cell: GridCell) => string): ShiftEdit[] {
  const byRow = new Map<number, Record<string, string | null>>();
  for (const range of targets)
    for (let r = range.topRow; r < range.topRow + range.rowCount; r++)
      for (let c = range.leftColumn; c < range.leftColumn + range.columnCount; c++) {
        const column = SHIFT_COLUMNS[c];
        if (column === undefined) throw new Error(`Column ${c + 1} of the Scenarios grid takes no value.`);
        const cells = byRow.get(r) ?? {};
        cells[column] = valueFor({ row: r, column: c });
        byRow.set(r, cells);
      }
  return [...byRow].sort(([a], [b]) => a - b).map(([r, cells]) => {
    const row = rows[r];
    if (row === undefined) throw new Error(`Row ${r + 1} is past the Scenarios grid's last row.`);
    return { id: row.shift?.id ?? null, row: row.position, cells };
  });
}

/** What a generator said back: how many it made, and those of the same name it replaced. */
export const generatedText = ({ made, replaced }: Pick<ScenariosMade, 'made' | 'replaced'>): string =>
  `Made ${made.length} scenario(s)${replaced.length === 0 ? '' : `, replacing ${replaced.length} of the same name`}: ${made.join(', ')}.`;

/** What the list's and the commands' answers say. */
export const includedText = (name: string, included: boolean) =>
  included ? `Price prices the scenario ${name}.` : `Price leaves the scenario ${name} out; its shifts stay.`;

// The generators (ADR-0010 §4): what the command line says of `scenario ladder` and `scenario cross` before Enter. The
// count and the names are read here from the sizes alone; whether each shift resolves is the server's to say, and it
// refuses the generator whole for one that does not.

/** What the page knows that the preview counts against: the cases, the cap of pairs, and the scenarios it holds. */
export interface GeneratorContext {
  readonly cases: number;
  readonly cap: number;
  readonly scenarios: readonly { readonly name: string; readonly included: boolean }[];
}

/** The most scenarios a generator makes under a cap of pairs: one case on the base market and on each of them. */
export const mostScenarios = (cap: number) => cap - 1;

const SIZE = /^([+-]?)(\d+(?:\.\d+)?)(bp|%)?$/i;

function sizeOf(word: string): { readonly value: number; readonly unit: string | undefined } | undefined {
  const match = SIZE.exec(word);
  if (match === null) return undefined;
  const magnitude = Number(match[2]);
  return { value: match[1] === '-' ? -magnitude : magnitude, unit: match[3]?.toLowerCase() };
}

// A size written as the server names it: `+50bp`, `-10%`.
const signed = (value: number, unit: string) => `${value > 0 ? '+' : ''}${String(value)}${unit}`;

/**
 * The scenarios a generator would make, by name, or why it cannot be read. A ladder's sizes are counted in hundredths,
 * so a step such as 2.5bp divides as it does on the server.
 */
export function generated(generator: 'ladder' | 'cross', text: string): { readonly names: readonly string[] } | { readonly problem: string } {
  if (generator === 'ladder') {
    const words = text.trim().replace(/\s*\.\.\s*/g, '..').split(/\s+/).filter((w) => w !== '');
    const range = words.findIndex((w) => w.includes('..'));
    const step = words.findIndex((w) => w.toLowerCase() === 'step');
    const ends = range < 0 ? [] : (words[range] ?? '').split('..');
    const by = step < 0 ? undefined : sizeOf(words[step + 1] ?? '');
    const [from, to] = [sizeOf(ends[0] ?? ''), sizeOf(ends[1] ?? '')];
    const factor = words.filter((_, i) => i !== range && i !== step && i !== step + 1).join(' ').toLowerCase();
    if (factor === '' || from === undefined || to === undefined || by === undefined) return { problem: 'a factor, a range and a step: jpy_ois -50bp..+50bp step 10bp' };
    const unit = from.unit ?? to.unit ?? by.unit ?? '';
    const [a, b, s] = [from.value, to.value, by.value].map((v) => Math.round(v * 100));
    if (a === undefined || b === undefined || s === undefined || s <= 0 || b <= a || (b - a) % s !== 0)
      return { problem: `a step that divides a rising range: ${ends[0] ?? ''}..${ends[1] ?? ''} by ${words[step + 1] ?? ''} does not` };
    const names: string[] = [];
    for (let v = a; v <= b; v += s) if (v !== 0) names.push(`${factor} ${signed(v / 100, unit)}`);
    return { names };
  }
  const lists = text.trim().split(/\s+x\s+/i).map((list) => {
    const [first = '', ...rest] = list.split(',').map((p) => p.trim());
    const words = first.split(/\s+/).filter((w) => w !== '');
    const sizes = [words[words.length - 1] ?? '', ...rest].map(sizeOf);
    return { factor: words.slice(0, -1).join(' ').toLowerCase(), sizes };
  });
  if (lists.length < 2 || lists.some((l) => l.factor === '' || l.sizes.some((s) => s === undefined)))
    return { problem: 'two lists or more separated by x, each a factor and its sizes: jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%' };
  let combinations: string[][] = [[]];
  for (const list of lists) {
    const unit = list.sizes.find((s) => s?.unit !== undefined)?.unit ?? '';
    combinations = combinations.flatMap((c) => list.sizes.map((s) => (s === undefined || s.value === 0 ? c : [...c, `${list.factor} ${signed(s.value, s.unit ?? unit)}`])));
  }
  return { names: combinations.filter((c) => c.length > 0).map((c) => c.join(' · ')) };
}

/**
 * The command line's preview of a generator (ADR-0010 §4's note of 2026-11-09): how many scenarios it makes, and the
 * pairs Price would then price against the cap, said as a problem where it would be refused or Price could not price.
 */
export function generatorParts(generator: 'ladder' | 'cross', text: string, context: GeneratorContext | undefined):
  readonly { readonly name: string; readonly value?: string; readonly required: boolean }[] {
  if (text.trim() === '') return [{ name: generator === 'ladder' ? 'factor, range and step' : 'lists', required: true }];
  const made = generated(generator, text);
  if ('problem' in made) return [{ name: generator === 'ladder' ? 'factor, range and step' : 'lists', required: true }];
  const count = made.names.length;
  if (context === undefined) return [{ name: 'makes', value: `${count} scenario(s)`, required: true }];
  const most = mostScenarios(context.cap);
  if (count > most) return [{ name: '?', value: `${count} scenarios: a set is ${most} at most`, required: true }];
  const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
  const kept = context.scenarios.filter((s) => s.included && !made.names.some((n) => same(n, s.name))).length;
  const pairs = context.cases * (1 + kept + count);
  return [
    { name: 'makes', value: `${count} scenario(s)`, required: true },
    pairs > context.cap
      ? { name: '?', value: `${pairs} pairs, ${pairs - context.cap} over the cap of ${context.cap}`, required: true }
      : { name: 'pairs', value: `${pairs} of ${context.cap}`, required: true },
  ];
}
