import { describe, expect, it } from 'vitest';
import type { ShiftRow } from './contracts';
import { clearedEdits, generated, generatedText, generatorParts, pastedEdits, scenarioParts, scenariosText, scenarioTyped, shiftGridRows } from './scenarios';

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

const shift = (id: string, scenario: string, index: string, sizeText: string): ShiftRow => ({
  id, row: 0, scenario, category: 'IR', index, tenor: null, factor: 'all', type: 'abs', size: 10, sizeText, description: null, issue: null, issueColumn: null,
});

describe('the Scenarios grid', () => {
  const rows = shiftGridRows([shift('s3', 'up', 'jpy_ois', '+10bp'), shift('s4', 'up', 'usd_sofr', '+5bp')], 3);

  it('ADR-0010 §4: every shift in the order made, then the blank rows a paste fills, each at its place', () => {
    expect(rows.map((r) => [r.key, r.position, r.shift?.id])).toEqual([
      ['s3', 1, 's3'], ['s4', 2, 's4'], ['blank-1', 3, undefined], ['blank-2', 4, undefined], ['blank-3', 5, undefined],
    ]);
  });

  it('ADR-0010 §4: a block pasted onto the blank rows is a new row each, its cells by column; a row pasted empty makes none', () => {
    const block = [['dn', '', 'jpy_ois', 'all', '', '-10bp'], ['', '', '', '', '', ''], ['dn', '', 'usdjpy', 'spot', '', '+5%']];
    const edits = pastedEdits(rows, [{ topRow: 2, leftColumn: 0, rowCount: 3, columnCount: 6 }], (c) => block[c.row - 2]![c.column]!);

    expect(edits).toEqual([
      { id: null, row: 3, cells: { scenario: 'dn', category: '', index: 'jpy_ois', factor: 'all', type: '', size: '-10bp' } },
      { id: null, row: 5, cells: { scenario: 'dn', category: '', index: 'usdjpy', factor: 'spot', type: '', size: '+5%' } },
    ]);
  });

  it('ADR-0010 §4: a block over a shift edits it by its id, and one cell typed onto many is each of them', () => {
    const edits = pastedEdits(rows, [{ topRow: 0, leftColumn: 5, rowCount: 2, columnCount: 1 }], () => '+1bp');

    expect(edits).toEqual([{ id: 's3', row: 1, cells: { size: '+1bp' } }, { id: 's4', row: 2, cells: { size: '+1bp' } }]);
  });

  it('ADR-0010 §4: Delete clears the shifts\' cells covered, and has nothing to clear on a blank row', () => {
    const edits = clearedEdits(rows, [{ topRow: 1, leftColumn: 0, rowCount: 3, columnCount: 7 }]);

    expect(edits).toEqual([{ id: 's4', row: 2, cells: { scenario: '', category: '', index: '', factor: '', type: '', size: '', description: '' } }]);
  });

  it('spine 1: a block past the grid\'s columns that take a value is refused, never written onto another column', () => {
    expect(() => pastedEdits(rows, [{ topRow: 0, leftColumn: 6, rowCount: 1, columnCount: 2 }], () => 'x')).toThrow('Column 8 of the Scenarios grid takes no value.');
  });
});

describe('the generators\' preview', () => {
  const context = { cases: 1, cap: 50, scenarios: [{ name: 'jpy_ois +10bp', included: true }, { name: 'old', included: true }, { name: 'out', included: false }] };

  it('ADR-0010 §4: a ladder of -50bp..+50bp by 10bp is ten scenarios, named from their shifts, without the zero', () => {
    const made = generated('ladder', 'JPY_OIS -50bp .. +50bp step 10bp');

    expect('names' in made && made.names).toEqual(['jpy_ois -50bp', 'jpy_ois -40bp', 'jpy_ois -30bp', 'jpy_ois -20bp', 'jpy_ois -10bp',
      'jpy_ois +10bp', 'jpy_ois +20bp', 'jpy_ois +30bp', 'jpy_ois +40bp', 'jpy_ois +50bp']);
    expect(generated('ladder', 'jpy_ois 5y -5bp..+5bp step 2.5bp')).toEqual({ names: ['jpy_ois 5y -5bp', 'jpy_ois 5y -2.5bp', 'jpy_ois 5y +2.5bp', 'jpy_ois 5y +5bp'] });
  });

  it('ADR-0010 §4: a cross of three by three is eight scenarios, the all-zero one being the base market', () => {
    const made = generated('cross', 'jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%');

    expect('names' in made && made.names).toEqual(['jpy_ois -50bp · usdjpy -10%', 'jpy_ois -50bp', 'jpy_ois -50bp · usdjpy +10%', 'usdjpy -10%', 'usdjpy +10%',
      'jpy_ois +50bp · usdjpy -10%', 'jpy_ois +50bp', 'jpy_ois +50bp · usdjpy +10%']);
  });

  it("ADR-0010 §4's note of 2026-11-09: says how many it makes and the pairs against the cap, a replaced scenario counted once", () => {
    // Ten made, one of them replacing an included scenario: the base market, `old` and the ten, for one case.
    expect(generatorParts('ladder', 'jpy_ois -50bp..+50bp step 10bp', context).map((p) => [p.name, p.value]))
      .toEqual([['makes', '10 scenario(s)'], ['pairs', '12 of 50']]);
    expect(generatorParts('ladder', 'jpy_ois -50bp..+50bp step 10bp', { ...context, cases: 5 }).map((p) => [p.name, p.value]))
      .toEqual([['makes', '10 scenario(s)'], ['?', '60 pairs, 10 over the cap of 50']]);
  });

  it('spine 1: a set over the limit, or a line that is no generator yet, is said, never counted as a guess', () => {
    expect(generatorParts('ladder', 'jpy_ois -50bp..+50bp step 1bp', context).map((p) => [p.name, p.value])).toEqual([['?', '100 scenarios: a set is 49 at most']]);
    expect(generatorParts('ladder', 'jpy_ois -50bp..+50bp step 15bp', context).map((p) => p.name)).toEqual(['factor, range and step']);
    expect(generatorParts('cross', 'jpy_ois -10bp, +10bp', context).map((p) => p.name)).toEqual(['lists']);
  });

  it('spine 1: a size with no unit, or a ladder mixing bp and %, is said as the server refuses it, never counted', () => {
    expect(generatorParts('ladder', 'jpy_ois -50..+50 step 10', undefined)).toEqual([{ name: '?', value: 'a unit on each size other than 0: bp for a curve, % for an FX rate', required: true }]);
    expect(generatorParts('ladder', 'jpy_ois -50bp..+50% step 10bp', undefined)).toEqual([{ name: '?', value: 'one unit for the range and the step: it mixes bp and %', required: true }]);
    expect(generatorParts('cross', 'jpy_ois -50, 0 x usdjpy -10%, 0', undefined)[0]?.name).toBe('?');
    expect(generated('cross', 'jpy_ois -50bp, 0 x usdjpy -10%, 0')).toEqual({ names: ['jpy_ois -50bp · usdjpy -10%', 'jpy_ois -50bp', 'usdjpy -10%'] });
  });

  it('ADR-0010 §4: says how many a generator made and replaced', () => {
    expect(generatedText({ made: ['a', 'b'], replaced: ['b'] })).toBe('Made 2 scenario(s), replacing 1 of the same name: a, b.');
  });
});
