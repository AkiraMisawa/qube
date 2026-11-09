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

/** What the list's and the commands' answers say. */
export const includedText = (name: string, included: boolean) =>
  included ? `Price prices the scenario ${name}.` : `Price leaves the scenario ${name} out; its shifts stay.`;
