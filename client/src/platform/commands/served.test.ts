import type { CapabilityDescriptor, LinePreview } from '../contracts';
import { readLine, signature, type Command } from './commands';
import { lineAsked, previewShown, servedCommands, taken, type CommandDoor } from './served';

// ADR-0016 §4, §5 and its note 15 of 2026-11-11: the line builds a command the catalog says how to type from the
// catalog, and reads nothing of its line itself. What it does with the server's answer is held here; the names are made up.

const capability = (capabilityId: string, command?: CapabilityDescriptor['command']): CapabilityDescriptor => ({
  capabilityId, version: '1.0.0', title: `Title of ${capabilityId}`, inputPorts: [], outputPorts: [], executionKind: 'requestResponse', effects: ['editWorkspace'],
  ...(command === undefined ? {} : { command }),
});

const catalog = {
  capabilities: [
    capability('entity.set', { words: ['set'], arguments: [{ name: 'rows', kind: 'rows', required: false }, { name: 'set', kind: 'assignments', required: true }] }),
    capability('workspace.undo', { words: ['undo'], arguments: [] }),
    capability('gauge.read'),
  ],
};

const door = (enter: CommandDoor['enter'] = () => Promise.resolve(undefined)): CommandDoor => ({ workspaceId: 'ws-1', enter });
const panel = { id: 'p', title: 'Board 2026-11-11' };

describe('a command the catalog says how to type (ADR-0016 §4)', () => {
  it('is built from the catalog, as a command of the page in the active panel; a capability with no form gives none', () => {
    const commands = servedCommands(catalog, panel, door(), vi.fn());

    expect(commands.map((c) => [c.id, c.words, c.title, c.group, c.target, c.page])).toEqual([
      ['entity.set', 'set', 'Title of entity.set', 'Workspace', 'Board 2026-11-11', true],
      ['workspace.undo', 'undo', 'Title of workspace.undo', 'Workspace', 'Board 2026-11-11', true],
    ]);
    expect(commands.map(signature)).toEqual(['[rows] <set>', '']);
    expect(commands[0]!.served?.workspaceId).toBe('ws-1');
  });

  it('says why it cannot run where the active panel has no workspace page', () => {
    const commands = servedCommands(catalog, panel, undefined, vi.fn());

    expect(commands.map((c) => [c.words, c.unavailable, c.served, c.page])).toEqual([
      ['set', 'This panel has no workspace for it to act on.', undefined, undefined],
      ['undo', 'This panel has no workspace for it to act on.', undefined, undefined],
    ]);
  });

  it('run from a key or a button, it has the server read its words alone, and a refusal is a notice', async () => {
    const notify = vi.fn();
    const enter = vi.fn(() => Promise.resolve({ sentence: 'There is nothing to take back: the workspace is as it was made.' }));
    const undo = servedCommands(catalog, panel, door(enter), notify).find((c) => c.words === 'undo')!;

    undo.run({});
    await Promise.resolve();

    expect(enter).toHaveBeenCalledWith('undo');
    expect(notify).toHaveBeenCalledWith('refused', 'undo: There is nothing to take back: the workspace is as it was made.');
  });

  it('the client reads nothing of its line: whatever follows its words is the server\'s to read', () => {
    const commands = servedCommands(catalog, panel, door(), vi.fn());

    const reading = readLine('set where unit=m3 checked on', commands);

    expect(reading.kind === 'arguments' && [reading.command.id, reading.given, reading.slot, reading.problems]).toEqual(['entity.set', {}, undefined, []]);
  });
});

describe('what the line asks the server to read (ADR-0016 §5)', () => {
  const commands: readonly Command[] = [...servedCommands(catalog, panel, door(), vi.fn()), { id: 'theme.toggle', words: 'toggle theme', title: 'Switch', group: 'Appearance', run: () => undefined }];
  const set = commands[0]!;
  const undo = commands[1]!;

  it('the text as typed, once it holds a served command\'s words and a space', () => {
    expect(lineAsked('set R1 che', readLine('set R1 che', commands), undefined)).toBe('set R1 che');
    expect(lineAsked('  set ', readLine('  set ', commands), undefined)).toBe('  set ');
  });

  it('the words of a command that takes no argument, while it is the one chosen in the list', () => {
    expect(lineAsked('und', readLine('und', commands), undo)).toBe('undo');
  });

  it('nothing while a command is chosen that takes arguments, or is not served, or none is', () => {
    expect(lineAsked('se', readLine('se', commands), set)).toBeUndefined();
    expect(lineAsked('tog', readLine('tog', commands), commands[2])).toBeUndefined();
    expect(lineAsked('toggle theme ', readLine('toggle theme ', commands), undefined)).toBeUndefined();
    expect(lineAsked('', readLine('', commands), undefined)).toBeUndefined();
  });
});

describe('a completion taken (ADR-0016 §5, note 15)', () => {
  it('is written from its place in the line, replacing what was typed from there, and ends its word with a space', () => {
    expect(taken('set R1 wind sp', { value: 'wind speed', start: 7 })).toBe('set R1 wind speed ');
    expect(taken('set ', { value: 'readings X1', start: 4 })).toBe('set readings X1 ');
    expect(taken('set where unit ', { value: '=', start: 15 })).toBe('set where unit = ');
  });

  it('leaves no space after a sign that awaits its value', () => {
    expect(taken('set R1 note=wet u', { value: 'unit=', start: 16 })).toBe('set R1 note=wet unit=');
    expect(taken('set where unit=g', { value: 'unit=GAL', start: 10 })).toBe('set where unit=GAL ');
  });
});

describe('what the line shows of the server\'s reading (ADR-0016 §5)', () => {
  const read = (more: Partial<LinePreview>): LinePreview => ({ revision: 7, complete: [], more: 0, ...more });

  it('the rows with the first few of them, and what would change', () => {
    const first = ['T-01', 'T-02', 'T-03', 'T-04', 'T-05', 'T-06', 'T-07', 'T-08', 'T-09', 'T-10'];
    expect(previewShown(read({ rows: { table: 'x.trades', title: 'trades, 12 rows', count: 12, first }, would: 'A: 9 on to off, 3 already off' })))
      .toEqual({ rows: 'trades, 12 rows: T-01, T-02, T-03, T-04, T-05, …', would: 'A: 9 on to off, 3 already off' });
    expect(previewShown(read({ rows: { table: 'w.readings', title: 'readings, 2 rows', count: 2, first: ['R1', 'R2'] }, would: 'checked: 1 off to on, 1 already on' })).rows)
      .toBe('readings, 2 rows: R1, R2');
  });

  it('rows that name none, as a table of one row or a condition nothing meets, by their title alone', () => {
    expect(previewShown(read({ rows: { table: 'w.settings', title: 'settings', count: 1, first: [] }, would: 'daylight: on to off' })).rows).toBe('settings');
    expect(previewShown(read({ rows: { table: 'w.readings', title: 'readings, 0 rows', count: 0, first: [] }, would: 'Nothing would change: no row of readings matches unit = L.' })).rows)
      .toBe('readings, 0 rows');
  });

  it('a command of no rows by what it would do, and when the command it speaks of ran', () => {
    expect(previewShown(read({ would: 'Takes back: set R1 checked on (ana, 1 edit)', at: '2026-11-11T01:02:03+00:00' })))
      .toEqual({ would: 'Takes back: set R1 checked on (ana, 1 edit)', at: '2026-11-11T01:02:03+00:00' });
  });

  it('a line that cannot be read by the reason alone', () => {
    expect(previewShown(read({ refused: { code: 'UnknownField', sentence: 'readings has no field colour.', word: 'colour', start: 7, end: 13, candidates: [] } })))
      .toEqual({ reason: 'readings has no field colour.' });
  });
});
