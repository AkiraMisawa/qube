import type { ScenariosMade } from './contracts';

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
