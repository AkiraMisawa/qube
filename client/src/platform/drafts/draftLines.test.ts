import type { DraftForm, DraftTable, DraftTemplate, FieldInput } from '../contracts';
import { cellLine, fieldLine, makeLine, removeLine, sectionsOf, templateLine } from './draftLines';

// ADR-0016 note 57 of 2026-11-12: every change a dialog makes is a line the server reads. What the dialog writes of a
// field, a cell, a row and a template is held here; the names are made up.

const field = (name: string, more: Partial<FieldInput> = {}): FieldInput =>
  ({ name, title: name, word: name, editable: true, optional: false, form: 'text', takes: 'text', ...more });
const sensors: DraftTable = { table: 'w.sensors', title: 'Sensors', word: 'sensors', fields: [field('kind')], rows: [], makes: [] };
const gauged: DraftTemplate = { word: 'gauged', title: 'Gauged by hand', takes: [
  { name: 'unit', title: 'Unit', required: true, form: 'choice', takes: 'one of M3, GAL', choices: ['M3', 'GAL'] },
  { name: 'drift', title: 'Drift', required: false, form: 'rate', takes: 'a rate' }] };

describe('the lines a dialog writes (ADR-0016 note 57)', () => {
  it('a field of the draft\'s row is set by its word, with no row; an emptied one is written empty, for the server to take or refuse', () => {
    expect(fieldLine(field('altitude'), ' 20 ')).toBe('set altitude 20');
    expect(fieldLine(field('cpty threshold', { word: 'cpty threshold' }), '5mm')).toBe('set cpty threshold 5mm');
    expect(fieldLine(field('note'), '')).toBe('set note empty');
  });

  it('a cell of a row under the row names its table and its row, and a row is removed the same way', () => {
    expect(cellLine(sensors, 'S1/2', field('kind'), 'gust')).toBe('set sensors S1/2 kind gust');
    expect(cellLine(sensors, 'S1/2', field('kind'), '')).toBe('set sensors S1/2 kind empty');
    expect(removeLine(sensors, 'S1/2')).toBe('remove sensors S1/2');
  });

  it('a template and a product are said by their word, each term filled by its name, in the order asked; one left empty is left out', () => {
    expect(templateLine(gauged, { drift: '2%', unit: 'GAL' })).toBe('set as gauged unit=GAL drift=2%');
    expect(templateLine(gauged, { unit: 'GAL', drift: ' ' })).toBe('set as gauged unit=GAL');
    expect(templateLine({ word: 'calm', title: 'Calm', takes: [] }, {})).toBe('set as calm');
    expect(makeLine({ word: 'sensor', title: 'Sensor', takes: gauged.takes }, { unit: 'M3' })).toBe('new sensor unit=M3');
  });

  it('a group is a section, in the order the groups first appear; the fields of no group come last, under Other', () => {
    const fields: DraftForm['fields'] = [
      { field: field('id', { group: 'Identity' }), written: 'S1', changed: false },
      { field: field('altitude', { group: 'Site' }), written: '15', changed: true },
      { field: field('spare'), written: 'empty', changed: false },
      { field: field('name', { group: 'Identity' }), written: 'South', changed: false },
    ];

    expect(sectionsOf(fields).map((s) => [s.title, s.fields.map((f) => `${f.field.name}@${f.index}`)])).toEqual([
      ['Identity', ['id@0', 'name@3']], ['Site', ['altitude@1']], ['Other', ['spare@2']]]);
    // A table that groups nothing is one section with no heading.
    expect(sectionsOf([fields[2]!]).map((s) => s.title)).toEqual([undefined]);
  });
});
