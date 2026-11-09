import { describe, expect, it } from 'vitest';
import { scenarioParts, scenariosText, scenarioTyped } from './scenarios';

// The `scenario` command as the line reads it (ADR-0010 §4): the name and the shifts as written, for the server to
// resolve. Every name is synthetic.

describe('scenarioTyped', () => {
  it('ADR-0010 §4: the first word names the scenario, and the rest are its shifts as written', () => {
    expect(scenarioTyped(' s1  jpy_ois +10bp; usdjpy -5% ')).toEqual({ name: 's1', shifts: 'jpy_ois +10bp; usdjpy -5%' });
  });

  it('spine 1: a line with no shift is refused here by name, never sent as a scenario of nothing', () => {
    expect(() => scenarioTyped('s1')).toThrow('scenario needs a name and its shifts');
    expect(() => scenarioTyped('  ')).toThrow('scenario needs a name and its shifts');
  });
});

describe('scenarioParts', () => {
  it('ADR-0010 §4: the line shows the name and each shift before Enter', () => {
    expect(scenarioParts('s1 jpy_ois +10bp; usdjpy -5%')).toEqual([
      { name: 'name', value: 's1', required: true },
      { name: 'shift', value: 'jpy_ois +10bp', required: true },
      { name: 'shift', value: 'usdjpy -5%', required: true },
    ]);
    expect(scenarioParts('')).toEqual([{ name: 'name', required: true }, { name: 'shifts', required: true }]);
  });

  it('ADR-0010 §4: a shift left empty between two semicolons is shown as a problem; one being typed is not', () => {
    expect(scenarioParts('s1 jpy_ois +10bp;; usdjpy -5%').map((p) => p.name)).toEqual(['name', 'shift', '?', 'shift']);
    expect(scenarioParts('s1 jpy_ois +10bp; ').map((p) => [p.name, p.value])).toEqual([['name', 's1'], ['shift', 'jpy_ois +10bp'], ['shift', undefined]]);
  });
});

describe('scenariosText', () => {
  it('ADR-0010 §4: says what was made, and what a scenario of the same name replaced', () => {
    expect(scenariosText({ made: ['s1'], replaced: [] })).toBe('Made the scenario s1.');
    expect(scenariosText({ made: ['S1'], replaced: ['S1'] })).toBe('Replaced the shifts of S1.');
    expect(scenariosText({ made: ['a', 'b', 'c'], replaced: ['b'] })).toBe('Made the scenarios a, c; replaced the shifts of b.');
  });
});
