import { ApiError, type Api, type CommandAnswer } from '../api';
import type { CapabilityDescriptor, LinePreview, LineView } from '../contracts';
import { readLine, signature, type Command, type CommandDoor } from './commands';
import { argumentShown, commandDoor, lineAsked, previewShown, servedCommands, taken } from './served';

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

/** What a page says is on screen: two rows selected, the keyboard on a cell of the first. */
const onScreen: LineView = { table: 'w.readings', subset: 'manual', keys: ['R1', 'R3'], focus: { field: 'note', key: 'R1' } };

const door = (enter: CommandDoor['enter'] = () => Promise.resolve(undefined), view: CommandDoor['view'] = () => undefined): CommandDoor =>
  ({ workspaceId: 'ws-1', settled: () => Promise.resolve(), view, enter, invoke: () => Promise.resolve(undefined) });
const panel = { id: 'p', title: 'Board 2026-11-11' };

describe('a command the catalog says how to type (ADR-0016 §4)', () => {
  it('is built from the catalog, as a command of the page in the active panel; a capability with no form gives none', () => {
    const commands = servedCommands(catalog, panel, door(), vi.fn());

    expect(commands.map((c) => [c.id, c.words, c.title, c.group, c.target, c.page])).toEqual([
      ['entity.set', 'set', 'Title of entity.set', 'Workspace', 'Board 2026-11-11', true],
      ['workspace.undo', 'undo', 'Title of workspace.undo', 'Workspace', 'Board 2026-11-11', true],
    ]);
    expect(commands.map(signature)).toEqual(['[rows] <set>', '']);
    expect(commands[0]!.served?.door.workspaceId).toBe('ws-1');
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

    expect(enter).toHaveBeenCalledWith('undo', undefined, undefined);
    expect(notify).toHaveBeenCalledWith('refused', 'undo: There is nothing to take back: the workspace is as it was made.');
  });

  it('run from a key or a button, its words are read with what is on screen at the press (ADR-0016 note 16)', () => {
    const enter = vi.fn(() => Promise.resolve(undefined));
    const undo = servedCommands(catalog, panel, door(enter, () => onScreen), vi.fn()).find((c) => c.words === 'undo')!;

    undo.run({});

    expect(enter).toHaveBeenCalledWith('undo', undefined, onScreen);
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
    const first = ['R01', 'R02', 'R03', 'R04', 'R05', 'R06', 'R07', 'R08', 'R09', 'R10'];
    expect(previewShown(read({ rows: { table: 'w.readings', title: 'readings, 12 rows', count: 12, first }, would: 'checked: 9 off to on, 3 already on' })))
      .toEqual({ rows: 'readings, 12 rows: R01, R02, R03, R04, R05, …', would: 'checked: 9 off to on, 3 already on' });
    expect(previewShown(read({ rows: { table: 'w.readings', title: 'readings, 2 rows', count: 2, first: ['R1', 'R2'] }, would: 'checked: 1 off to on, 1 already on' })).rows)
      .toBe('readings, 2 rows: R1, R2');
  });

  it('rows that name none, as a table of one row or a condition nothing meets, by their title alone', () => {
    expect(previewShown(read({ rows: { table: 'w.settings', title: 'settings', count: 1, first: [] }, would: 'daylight: on to off' })).rows).toBe('settings');
    expect(previewShown(read({ rows: { table: 'w.readings', title: 'readings, 0 rows', count: 0, first: [] }, would: 'Nothing would change: no row of readings matches unit = L.' })).rows)
      .toBe('readings, 0 rows');
  });

  it('a command of no rows by what it would do, and when the command it speaks of ran', () => {
    expect(previewShown(read({ would: 'Takes back: set R1 checked on (you, 1 edit)', at: '2026-11-11T01:02:03+00:00' })))
      .toEqual({ would: 'Takes back: set R1 checked on (you, 1 edit)', at: '2026-11-11T01:02:03+00:00' });
  });

  it('an argument by its name and the words read for it, and one that was not typed says that it is from the screen (ADR-0016 note 18)', () => {
    expect(argumentShown({ name: 'set', text: 'csa=CSA-B', from: 'typed' })).toBe('set csa=CSA-B');
    expect(argumentShown({ name: 'rows', text: 'R1, R2, R3 and 2 more', from: 'context' })).toBe('rows R1, R2, R3 and 2 more (from the screen)');
  });

  it('a line that cannot be read by the reason alone', () => {
    expect(previewShown(read({ refused: { code: 'UnknownField', sentence: 'readings has no field colour.', word: 'colour', start: 7, end: 13, candidates: [] } })))
      .toEqual({ reason: 'readings has no field colour.' });
  });
});

describe('Enter on a line the server reads, through its page\'s door (ADR-0016 §2, §5, note 15)', () => {
  const reading = (revision: number, more: Partial<LinePreview> = {}): LinePreview => ({
    revision, command: { id: 'entity.set', version: '1.0.0' }, inputs: { request: { workspaceId: 'ws-1', revision, rows: 'R1', set: { checked: 'on' } } },
    would: 'checked: off to on', complete: [], more: 0, ...more,
  });
  const noField: LinePreview = { revision: 7, refused: { code: 'UnknownField', sentence: 'readings has no field colour.', word: 'colour', start: 7, end: 13, candidates: [] }, complete: [], more: 0 };
  const done = (revision: number, said: string): CommandAnswer => ({ capabilityId: 'entity.set', version: '1.0.0', outputs: { answer: { workspaceId: 'ws-1', revision, said } } });
  const moved = (at: number, read: number) => new ApiError(409, { code: 'RevisionMoved', message: `The workspace is at revision ${at}, not ${read}.`, issues: [] });

  /** A page that holds its workspace at `held`, with nothing of its own waiting, and reads it again at `stands`. */
  const page = (held: number, stands: number = held, view?: () => LineView | undefined) => {
    const turns = {
      revision: held,
      after: <T,>(run: (revision: number) => Promise<T>) => run(turns.revision),
      rebase: (revision: number) => { turns.revision = revision; },
      settled: () => Promise.resolve(),
    };
    const notify = vi.fn();
    const preview = vi.fn<Api['preview']>();
    const command = vi.fn<Api['command']>();
    const reread = vi.fn(() => Promise.resolve(stands));
    return { turns, notify, preview, command, reread, door: commandDoor({ workspaceId: 'ws-1', catalog, api: { preview, command }, turns, reread, notify, ...(view === undefined ? {} : { view }) }) };
  };

  it('runs the reading the line showed, by its own inputs with the revision it was read at, and reads the line no second time', async () => {
    // Someone else moved the workspace to 9 and the line read it there; the page still holds 7.
    const p = page(7, 10);
    p.command.mockResolvedValue(done(10, 'Set R1 checked to on.'));

    const reason = await p.door.enter('set R1 checked on', reading(9));

    expect(reason).toBeUndefined();
    expect(p.preview).not.toHaveBeenCalled();
    expect(p.command).toHaveBeenCalledWith('entity.set', '1.0.0', reading(9).inputs, expect.any(String));
    expect(p.notify.mock.calls).toEqual([['done', 'Set R1 checked to on.']]);
    expect(p.turns.revision).toBe(10);
  });

  it('a workspace that moved since the reading was shown refuses it: nothing runs, the line keeps the reason, and the page reads the workspace again', async () => {
    const p = page(9, 10);
    p.command.mockRejectedValue(moved(10, 9));

    const reason = await p.door.enter('set R1 checked on', reading(9));

    expect(reason).toEqual({ sentence: 'The workspace is at revision 10, not 9.' });
    expect(p.reread).toHaveBeenCalledTimes(1);
    expect(p.turns.revision).toBe(10);
    expect(p.notify).not.toHaveBeenCalled();
  });

  it('with no reading shown, the line is read at the revision the page holds in its turn, and that reading is what runs', async () => {
    const p = page(7, 8);
    p.preview.mockResolvedValue(reading(7));
    p.command.mockResolvedValue(done(8, 'Set R1 checked to on.'));

    expect(await p.door.enter('set R1 checked on')).toBeUndefined();

    expect(p.preview).toHaveBeenCalledWith('set R1 checked on', 'ws-1', { revision: 7 });
    expect(p.command).toHaveBeenCalledWith('entity.set', '1.0.0', reading(7).inputs, expect.any(String));
  });

  it('the door answers what its page says is on screen, and a page that says nothing has no view (ADR-0016 note 16)', () => {
    let shown: LineView | undefined = onScreen;
    const p = page(7, 7, () => shown);

    expect(p.door.view()).toBe(onScreen);
    shown = undefined;
    expect(p.door.view()).toBeUndefined();
    expect(page(7).door.view()).toBeUndefined();
  });

  it('with no reading shown, the line is read with the view the line opened with, not with what the screen shows by then (ADR-0016 note 16)', async () => {
    // The page would now say another row: the line was opened on the two, and its Enter is read with them.
    const p = page(7, 8, () => ({ table: 'w.readings', keys: ['R9'] }));
    p.preview.mockResolvedValue(reading(7));
    p.command.mockResolvedValue(done(8, 'Set R1 and R3 checked to on.'));

    expect(await p.door.enter('set checked on', undefined, onScreen)).toBeUndefined();

    expect(p.preview).toHaveBeenCalledWith('set checked on', 'ws-1', { revision: 7, view: onScreen });
  });

  it('a line that cannot be read answers its reason and runs nothing, whether the line showed the reading or Enter asked for it', async () => {
    const shown = page(7);
    expect(await shown.door.enter('set R1 colour red', noField)).toEqual(noField.refused);
    expect(shown.preview).not.toHaveBeenCalled();
    expect(shown.command).not.toHaveBeenCalled();

    const asked = page(7);
    asked.preview.mockResolvedValue(noField);
    expect(await asked.door.enter('set R1 colour red')).toEqual(noField.refused);
    expect(asked.command).not.toHaveBeenCalled();
  });

  it('what only the write refuses keeps the line too, as does a reading that names no command', async () => {
    const held = page(7);
    held.command.mockRejectedValue(new ApiError(422, { code: 'ConflictUnresolved', message: 'R1 checked has an edit that is held: keep it or reset it first.', issues: [] }));
    expect(await held.door.enter('set R1 checked on', reading(7))).toEqual({ sentence: 'R1 checked has an edit that is held: keep it or reset it first.' });
    expect(held.reread).not.toHaveBeenCalled();
    expect(held.notify).not.toHaveBeenCalled();

    const unnamed = page(7);
    unnamed.preview.mockResolvedValue({ revision: 7, complete: [], more: 0 });
    expect(await unnamed.door.enter('set')).toEqual({ sentence: 'The server read the line and named no command to run.' });
    expect(unnamed.command).not.toHaveBeenCalled();
  });

  it('a command that ran is done even where the page cannot read what it left: the line closes, and the notice says both', async () => {
    const p = page(7);
    p.command.mockResolvedValue(done(8, 'Set R1 checked to on.'));
    p.reread.mockRejectedValue(new Error('The server did not answer.'));

    expect(await p.door.enter('set R1 checked on', reading(7))).toBeUndefined();

    expect(p.notify.mock.calls).toEqual([['refused', 'Set R1 checked to on. The page could not read the workspace again: The server did not answer.']]);
    // The page's next edit chains on what the command left, whatever the page could read.
    expect(p.turns.revision).toBe(8);
  });

  it('a command by its id, from a key or a button, runs in the same turn at the catalog\'s version and the page\'s revision', async () => {
    const p = page(7, 8);
    p.command.mockResolvedValue({ capabilityId: 'workspace.undo', version: '1.0.0', outputs: { answer: { workspaceId: 'ws-1', revision: 8, said: 'Took back: set R1 checked on. redo puts it back.' } } });

    expect(await p.door.invoke('workspace.undo', {})).toBeUndefined();
    expect(p.command).toHaveBeenCalledWith('workspace.undo', '1.0.0', { request: { workspaceId: 'ws-1', revision: 7 } }, expect.any(String));
    expect(p.notify.mock.calls).toEqual([['done', 'Took back: set R1 checked on. redo puts it back.']]);

    expect(await p.door.invoke('workspace.nothing', {})).toEqual({ sentence: 'This server has no command workspace.nothing.' });
  });
});
