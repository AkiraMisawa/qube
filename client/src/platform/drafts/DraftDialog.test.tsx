import { useMemo } from 'react';
import { act, renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUi } from '../../test/ui';
import { ApiError } from '../api';
import type { CommandDoor } from '../commands/commands';
import type { Drafts } from '../commands/served';
import type { DraftForm, DraftTemplate, FieldInput, LineDraft, LineView } from '../contracts';
import { DraftDialog } from './DraftDialog';
import { useDraft } from './useDraft';

// The dialog with its page's draft, the door and the server stood in (the spec's seam for the client's dialog). The
// stand-in server reads `set <word> <text>` alone: what a line means is the kernel's, and its own tests hold it.

const TABLE = 't.stations';
const KEY = 'S1';

const field = (name: string, title: string, over: Partial<FieldInput> = {}): FieldInput =>
  ({ name, title, word: name, editable: true, optional: false, form: 'text', takes: 'a text', number: false, ...over });

const FIELDS: readonly FieldInput[] = [
  field('name', 'Name', { group: 'Site' }),
  field('altitude', 'Altitude', { group: 'Site', form: 'number', takes: 'a number', unit: 'm', number: true }),
  field('kind', 'Kind', { group: 'Sensors', form: 'choice', takes: 'rain or wind', choices: ['rain', 'wind'] }),
  field('open', 'Open', { group: 'Sensors', form: 'flag', takes: 'on or off' }),
  field('note', 'Note', { optional: true }),
  field('id', 'Id', { editable: false }),
];
const SOURCE: Readonly<Record<string, string>> = { name: 'North one', altitude: '120', kind: 'rain', open: 'on', note: 'empty', id: KEY };
const COASTAL: DraftTemplate = { word: 'coastal', title: 'Coastal station', takes: [{ name: 'altitude', title: 'Altitude', required: true, form: 'number', takes: 'a number' }] };
const SENSOR: DraftTemplate = { word: 'sensor', title: 'Sensor', takes: [{ name: 'load', title: 'Load', required: true, form: 'number', takes: 'a number' }] };
const SENSORS = { table: 't.sensors', title: 'Sensors of the station', word: 'sensors', fields: [field('load', 'Load')], rows: [], makes: [SENSOR] };

const formOf = (draft: LineDraft, made: boolean, held: Readonly<Record<string, string>>): DraftForm => {
  const written = { ...SOURCE, ...held };
  const changed = new Set<string>();
  const lines = draft.lines.map((line) => {
    const set = /^set (\w+) (.+)$/.exec(line);
    if (set?.[1] === undefined || set[2] === undefined || !(set[1] in SOURCE)) return { line, said: `Reads ${line}.` };
    written[set[1]] = set[2];
    changed.add(set[1]);
    return { line, said: `Sets ${set[1]} to ${set[2]}.` };
  });
  return {
    table: TABLE, word: 'stations', key: draft.key, revision: draft.revision,
    fields: FIELDS.map((f) => ({ field: f, written: written[f.name] ?? 'empty', changed: changed.has(f.name) })),
    under: [SENSORS], templates: [COASTAL], lines,
    ...(made ? {} : { original: { written: FIELDS.map((f) => SOURCE[f.name] ?? 'empty'), under: [SENSORS] } }),
  };
};

/** What a test sets of the server and the door, and what it reads of them afterwards. */
class Rig {
  /** The lines the door was given, each with the draft its view carried. */
  readonly entered: { line: string; view: LineView | undefined }[] = [];
  /** The lines the door refuses, by the sentence it answers. */
  readonly refused = new Map<string, string>();
  /** True while the workspace has left the draft. */
  left = false;
  /** True where the workspace made the row. */
  made = false;
  /** Why the server cannot answer a form, while it cannot. */
  down: string | undefined;
  /** What the row holds beyond the source's values. */
  held: Readonly<Record<string, string>> = {};
  redrafts = 0;
  /** How often the dialog gave the keyboard back to the page. */
  keyboards = 0;
  drafts: Drafts | undefined;

  /** The server's answers that wait for `release`; none while it answers at once. */
  private waiting: (() => void)[] | undefined;

  /** From here the server's answers wait, as over a slow line. */
  hold() { this.waiting = []; }

  release() { const waiting = this.waiting; this.waiting = undefined; waiting?.forEach((answer) => answer()); }

  /** The door's answers that wait for `releaseDoor`; none while it answers at once. */
  private doorWaiting: (() => void)[] | undefined;

  /** From here the door's turns wait: a line entered is being read until they are released. */
  holdDoor() { this.doorWaiting = []; }

  releaseDoor() { const waiting = this.doorWaiting; this.doorWaiting = undefined; waiting?.forEach((answer) => answer()); }

  readonly api = {
    draft: async (_workspace: string, draft: LineDraft): Promise<DraftForm> => {
      const waiting = this.waiting;
      if (waiting !== undefined) await new Promise<void>((answer) => { waiting.push(answer); });
      if (this.left) throw new ApiError(409, { code: 'RevisionMoved', message: `The workspace is at revision 8 and the draft of ${draft.key} was opened at 7.`, issues: [] });
      if (this.down !== undefined) throw new Error(this.down);
      return formOf(draft, this.made, this.held);
    },
  };

  door(drafts: Drafts): Pick<CommandDoor, 'enter' | 'redraft' | 'keyboard'> {
    return {
      keyboard: () => { this.keyboards += 1; },
      enter: async (line, _shown, given) => {
        this.entered.push({ line, view: given });
        const waiting = this.doorWaiting;
        if (waiting !== undefined) await new Promise<void>((answer) => { waiting.push(answer); });
        const sentence = this.refused.get(line);
        if (sentence !== undefined) return { sentence };
        // As the page's door does: `apply` written closes the draft, and any other line read is one the draft gains.
        if (line === 'apply') drafts.close();
        else drafts.gain(line);
        return undefined;
      },
      redraft: () => { this.redrafts += 1; this.left = false; drafts.moved(8); return Promise.resolve(undefined); },
    };
  }
}

function Page({ rig, note }: { readonly rig: Rig; readonly note?: (table: string) => string | undefined }) {
  const { draft, drafts, drop } = useDraft();
  rig.drafts = drafts;
  const door = useMemo(() => rig.door(drafts), [rig, drafts]);
  return (
    <>
      <button type="button" onClick={() => drafts.open(TABLE, KEY, 7)}>Open S1</button>
      <DraftDialog draft={draft} workspaceId="w1" revision={7} api={rig.api} door={door} drop={drop} discard={drafts.close} {...(note === undefined ? {} : { note })} />
    </>
  );
}

const opened = async (rig = new Rig(), note?: (table: string) => string | undefined) => {
  const user = userEvent.setup();
  renderUi(<Page rig={rig} {...(note === undefined ? {} : { note })} />);
  await user.click(screen.getByRole('button', { name: 'Open S1' }));
  await screen.findByRole('group', { name: 'Site' });
  return { rig, user };
};

const lines = (rig: Rig) => rig.entered.map((e) => e.line);
const pending = () => screen.getByTestId('draft-pending').textContent;
const typed = async (user: ReturnType<typeof userEvent.setup>, label: RegExp, text: string) => {
  const box = screen.getByRole('textbox', { name: label });
  await user.clear(box);
  await user.type(box, `${text}{Enter}`);
};

describe('a row\'s dialog, drawn from the form the server answers (ADR-0016 note 57)', () => {
  it('note 57: a group is a section, the fields of no group come last under Other, and a field no command sets is read-only', async () => {
    await opened();
    const sections = screen.getAllByRole('group').map((g) => g.getAttribute('aria-label')).filter((label) => ['Site', 'Sensors', 'Other'].includes(label ?? ''));
    expect(sections).toEqual(['Site', 'Sensors', 'Other']);
    const site = within(screen.getByRole('group', { name: 'Site' }));
    expect(site.getByRole('textbox', { name: /Name/ })).toHaveValue('North one');
    expect(site.getByRole('textbox', { name: /Altitude \(m\)/ })).toHaveValue('120');
    const other = within(screen.getByRole('group', { name: 'Other' }));
    // No value is drawn as nothing, never as the word a line writes for it.
    expect(other.getByRole('textbox', { name: /Note/ })).toHaveValue('');
    expect(other.getByRole('textbox', { name: /Id/ })).toHaveAttribute('readonly');
    expect(pending()).toBe('nothing pending');
  });

  it('note 57: a field changed is a line of the draft, read through the door on the draft, and the field then says it changed', async () => {
    const { rig, user } = await opened();
    await typed(user, /Altitude/, '150');
    expect(rig.entered).toEqual([{ line: 'set altitude 150', view: { table: TABLE, draft: { table: TABLE, key: KEY, revision: 7, lines: [] } } }]);
    await waitFor(() => expect(screen.getByRole('textbox', { name: /Altitude/ })).toHaveAccessibleName(/changed/));
    expect(screen.getByRole('textbox', { name: /Altitude/ })).toHaveValue('150');
    expect(pending()).toBe('1 pending');
    const list = within(screen.getByTestId('draft-lines'));
    expect(list.getByText('set altitude 150')).toBeInTheDocument();
    expect(list.getByText('Sets altitude to 150.')).toBeInTheDocument();
  });

  it('note 57: a line is sent once though the keyboard leaves the field before the form of it is drawn', async () => {
    const { rig, user } = await opened();
    rig.hold();
    await typed(user, /Altitude/, '150');
    // The draft has the line, and the form drawn is still the one of no line.
    await waitFor(() => expect(pending()).toBe('1 pending'));
    await user.tab();
    rig.release();
    await waitFor(() => expect(screen.getByRole('textbox', { name: /Altitude/ })).toHaveAccessibleName(/changed/));
    expect(lines(rig)).toEqual(['set altitude 150']);
  });

  it('note 57: the same line from another press is a line of its own, though the form of the first is not drawn yet', async () => {
    const { rig, user } = await opened();
    rig.hold();
    const add = async () => {
      await user.click(screen.getByRole('button', { name: 'Add Sensor…' }));
      await user.type(within(screen.getByRole('group', { name: 'Sensor' })).getByRole('textbox', { name: /Load/ }), '40{Enter}');
      await waitFor(() => expect(screen.queryByRole('group', { name: 'Sensor' })).not.toBeInTheDocument());
    };
    await add();
    await add();
    expect(lines(rig)).toEqual(['new sensor load=40', 'new sensor load=40']);
    expect(pending()).toBe('2 pending');
  });

  it('note 57: a text typed while the field\'s line is being read is a line of its own, sent after it', async () => {
    const { rig, user } = await opened();
    rig.holdDoor();
    await typed(user, /Altitude/, '150');
    await typed(user, /Altitude/, '160');
    expect(lines(rig)).toEqual(['set altitude 150', 'set altitude 160']);
    rig.releaseDoor();
    await waitFor(() => expect(pending()).toBe('2 pending'));
    await waitFor(() => expect(screen.getByRole('textbox', { name: /Altitude/ })).toHaveAccessibleName(/changed/));
    expect(screen.getByRole('textbox', { name: /Altitude/ })).toHaveValue('160');
  });

  it('note 57: a switch pressed twice is two lines, the second of what the first left', async () => {
    const { rig, user } = await opened();
    rig.hold();
    await user.click(screen.getByRole('switch', { name: /Open/ }));
    await user.click(screen.getByRole('switch', { name: /Open/ }));
    expect(lines(rig)).toEqual(['set open off', 'set open on']);
  });

  it('note 57: a choice the form does not list is still drawn as what the row holds', async () => {
    const rig = new Rig();
    rig.held = { kind: 'snow' };
    await opened(rig);
    expect(screen.getByRole('combobox', { name: /Kind/ })).toHaveValue('snow');
  });

  it('note 57: a flag is a switch and a choice a list, and an emptied field is sent as empty', async () => {
    const { rig, user } = await opened();
    await user.click(screen.getByRole('switch', { name: /Open/ }));
    await user.click(screen.getByRole('combobox', { name: /Kind/ }));
    await user.click(await screen.findByRole('option', { name: 'wind' }));
    await typed(user, /Name/, '');
    expect(lines(rig)).toEqual(['set open off', 'set kind wind', 'set name empty']);
  });

  it('note 57: a refusal is said at the field it was typed in, the dialog stays open and the draft gains nothing', async () => {
    const rig = new Rig();
    rig.refused.set('set altitude high', 'Altitude takes a number, and high is not one.');
    const { user } = await opened(rig);
    await typed(user, /Altitude/, 'high');
    expect(await screen.findByText('Altitude takes a number, and high is not one.')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Altitude/ })).toHaveAttribute('aria-invalid', 'true');
    expect(pending()).toBe('nothing pending');
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('note 57: Save runs apply through the door on the draft, and the dialog closes once it ran', async () => {
    const { rig, user } = await opened();
    await typed(user, /Altitude/, '150');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(rig.entered.at(-1)).toEqual({ line: 'apply', view: { table: TABLE, draft: { table: TABLE, key: KEY, revision: 7, lines: ['set altitude 150'] } } });
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());
    // §10: written, the dialog gives the keyboard back to the page, for the grid it stood over.
    expect(rig.keyboards).toBe(1);
  });

  it('note 57: apply refused is said in the dialog, which stays open with its lines', async () => {
    const rig = new Rig();
    rig.refused.set('apply', 'The workspace moved to revision 8: read the draft again.');
    const { user } = await opened(rig);
    await typed(user, /Altitude/, '150');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByTestId('draft-refused')).toHaveTextContent('The workspace moved to revision 8: read the draft again.');
    expect(pending()).toBe('1 pending');
  });

  it('note 57: Save waits for a line still being read, and writes nothing where that line was refused', async () => {
    const rig = new Rig();
    rig.refused.set('set altitude high', 'Altitude takes a number, and high is not one.');
    const { user } = await opened(rig);
    await typed(user, /Name/, 'North two');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    rig.holdDoor();
    const altitude = screen.getByRole('textbox', { name: /Altitude/ });
    await user.clear(altitude);
    await user.type(altitude, 'high');
    // Leaving the field for Save sends its line, and Save is pressed while that line is read.
    await user.click(screen.getByRole('button', { name: 'Save' }));
    rig.releaseDoor();
    expect(await screen.findByTestId('draft-refused')).toHaveTextContent('nothing was saved');
    expect(lines(rig)).toEqual(['set name North two', 'set altitude high']);
    expect(pending()).toBe('1 pending');
    expect(screen.getByText('Altitude takes a number, and high is not one.')).toBeInTheDocument();
  });

  it('note 57: while Save is being read the form takes nothing, so no line is lost behind it', async () => {
    const { rig, user } = await opened();
    await typed(user, /Altitude/, '150');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    rig.holdDoor();
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(screen.getByRole('textbox', { name: /Name/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Take back: set altitude 150' })).toHaveAttribute('aria-disabled', 'true');
    await user.click(screen.getByRole('button', { name: 'Take back: set altitude 150' }));
    expect(pending()).toBe('1 pending');
    rig.releaseDoor();
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());
    expect(lines(rig)).toEqual(['set altitude 150', 'apply']);
  });

  it('note 57: the question is of the draft it was asked of: Escape and the close button ask it, and the next dialog opens without it', async () => {
    const { rig, user } = await opened();
    await typed(user, /Altitude/, '150');
    await waitFor(() => expect(pending()).toBe('1 pending'));
    await user.keyboard('{Escape}');
    expect(screen.getByRole('alertdialog', { name: 'Discard the draft' })).toHaveAccessibleDescription('Discard the pending line?');
    // Escape again keeps the draft, and the keyboard stays in the dialog, where the next Escape is heard.
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(screen.getByRole('dialog')).toContainElement(document.activeElement as HTMLElement);
    await user.keyboard('{Escape}');
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus();
    await user.click(screen.getByRole('button', { name: 'Close the dialog' }));
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Discard' }));
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Open S1' }));
    await screen.findByRole('group', { name: 'Site' });
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(pending()).toBe('nothing pending');
    expect(lines(rig)).toEqual(['set altitude 150']);
  });

  it('note 57: Cancel with nothing pending closes at once; with a line pending it asks first, and the server is asked nothing', async () => {
    const { rig, user } = await opened();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Open S1' }));
    await screen.findByRole('group', { name: 'Site' });
    await typed(user, /Altitude/, '150');
    await waitFor(() => expect(pending()).toBe('1 pending'));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    const asked = within(screen.getByRole('alertdialog', { name: 'Discard the draft' }));
    expect(asked.getByText('Discard the pending line?')).toBeInTheDocument();
    await user.click(asked.getByRole('button', { name: 'Keep editing' }));
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(pending()).toBe('1 pending');

    // Each time the dialog closed, and at no other time, the page was given the keyboard back for its grid.
    expect(rig.keyboards).toBe(1);
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await user.click(screen.getByRole('button', { name: 'Discard' }));
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());
    expect(rig.drafts?.current()).toBeUndefined();
    expect(lines(rig)).toEqual(['set altitude 150']);
    expect(rig.keyboards).toBe(2);
  });

  it('note 57: Original is the source\'s row, read-only, with no template and nothing to add', async () => {
    const { rig, user } = await opened();
    await typed(user, /Altitude/, '150');
    await waitFor(() => expect(pending()).toBe('1 pending'));
    await user.click(screen.getByRole('tab', { name: 'Original' }));
    const altitude = await screen.findByRole('textbox', { name: /Altitude/ });
    expect(altitude).toHaveValue('120');
    expect(altitude).toHaveAttribute('readonly');
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Templates' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add Sensor…' })).not.toBeInTheDocument();
    expect(lines(rig)).toEqual(['set altitude 150']);
  });

  it('note 57: a row the workspace made has no Original', async () => {
    const rig = new Rig();
    rig.made = true;
    await opened(rig);
    expect(screen.getByRole('tab', { name: 'Original' })).toBeDisabled();
  });

  it('note 57: a draft the workspace has left says so with the server\'s sentence, and is read again only when asked', async () => {
    const rig = new Rig();
    rig.left = true;
    const user = userEvent.setup();
    renderUi(<Page rig={rig} />);
    await user.click(screen.getByRole('button', { name: 'Open S1' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The workspace is at revision 8 and the draft of S1 was opened at 7.');
    expect(rig.redrafts).toBe(0);
    await user.click(screen.getByRole('button', { name: 'Read it again' }));
    await screen.findByRole('group', { name: 'Site' });
    expect(rig.redrafts).toBe(1);
    expect(rig.drafts?.current()).toEqual({ table: TABLE, key: KEY, revision: 8, lines: [] });
  });

  it('note 57: a draft the workspace has left can be discarded without the server', async () => {
    const rig = new Rig();
    rig.left = true;
    const user = userEvent.setup();
    renderUi(<Page rig={rig} />);
    await user.click(screen.getByRole('button', { name: 'Open S1' }));
    await screen.findByRole('alert');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());
    expect(rig.entered).toEqual([]);
  });

  it('note 57: a draft the workspace has left asks before its pending lines are discarded', async () => {
    const { rig, user } = await opened();
    rig.left = true;
    await typed(user, /Altitude/, '150');
    expect(await screen.findByRole('alert')).toHaveTextContent('The workspace is at revision 8 and the draft of S1 was opened at 7.');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    const asked = within(screen.getByRole('alertdialog', { name: 'Discard the draft' }));
    expect(asked.getByText('Discard the pending line?')).toBeInTheDocument();
    await user.click(asked.getByRole('button', { name: 'Keep editing' }));
    expect(pending()).toBe('1 pending');
    expect(screen.getByRole('button', { name: 'Read the line again' })).toBeInTheDocument();
    // Nothing is written of a draft that could not be read.
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('note 57: a dialog the server could not answer says why, and is asked for again when the user says', async () => {
    const rig = new Rig();
    rig.down = 'The server did not answer.';
    const user = userEvent.setup();
    renderUi(<Page rig={rig} />);
    await user.click(screen.getByRole('button', { name: 'Open S1' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The server did not answer.');
    rig.down = undefined;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByRole('group', { name: 'Site' });
    expect(rig.redrafts).toBe(0);
  });

  it('note 57: the page\'s sentence is said above the form of its table', async () => {
    await opened(new Rig(), (table) => (table === TABLE ? 'The figures of a station are made up.' : undefined));
    expect(screen.getByTestId('draft-note')).toHaveTextContent('The figures of a station are made up.');
  });

  it('note 57: a template is a button that asks for its terms and sends one line', async () => {
    const { rig, user } = await opened();
    await user.click(within(screen.getByRole('group', { name: 'Templates' })).getByRole('button', { name: 'coastal' }));
    const terms = within(screen.getByRole('group', { name: 'Coastal station' }));
    await user.type(terms.getByRole('textbox', { name: /Altitude/ }), '5');
    await user.click(terms.getByRole('button', { name: 'Fill as Coastal station' }));
    expect(lines(rig)).toEqual(['set as coastal altitude=5']);
    await waitFor(() => expect(screen.queryByRole('group', { name: 'Coastal station' })).not.toBeInTheDocument());
  });

  it('note 57: a row is made under the draft\'s row by a product\'s terms, and a refusal keeps the terms open', async () => {
    const rig = new Rig();
    rig.refused.set('new sensor load=heavy', 'Load takes a number, and heavy is not one.');
    const { user } = await opened(rig);
    await user.click(screen.getByRole('button', { name: 'Add Sensor…' }));
    const terms = within(screen.getByRole('group', { name: 'Sensor' }));
    await user.type(terms.getByRole('textbox', { name: /Load/ }), 'heavy');
    await user.click(terms.getByRole('button', { name: 'Add Sensor' }));
    expect(await terms.findByRole('alert')).toHaveTextContent('Load takes a number, and heavy is not one.');
    await user.clear(terms.getByRole('textbox', { name: /Load/ }));
    await user.type(terms.getByRole('textbox', { name: /Load/ }), '40{Enter}');
    await waitFor(() => expect(pending()).toBe('1 pending'));
    expect(lines(rig)).toEqual(['new sensor load=heavy', 'new sensor load=40']);
  });

  it('note 57: a pending line is taken back alone, and the lines left are read again', async () => {
    const { rig, user } = await opened();
    await typed(user, /Altitude/, '150');
    await typed(user, /Name/, 'North two');
    await waitFor(() => expect(pending()).toBe('2 pending'));
    await user.click(screen.getByRole('button', { name: 'Take back: set altitude 150' }));
    await waitFor(() => expect(pending()).toBe('1 pending'));
    expect(rig.drafts?.current()?.lines).toEqual(['set name North two']);
    await waitFor(() => expect(screen.getByRole('textbox', { name: /Altitude/ })).toHaveValue('120'));
    // The keyboard is on the line that took the place of the one taken back, and once none is left, on Cancel.
    expect(screen.getByRole('button', { name: 'Take back: set name North two' })).toHaveFocus();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(pending()).toBe('nothing pending'));
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus();
  });

  it('note 57: nothing is taken back from a list drawn of an earlier draft', async () => {
    const { rig, user } = await opened();
    await typed(user, /Altitude/, '150');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Take back: set altitude 150' })).not.toHaveAttribute('aria-disabled'));
    rig.hold();
    await typed(user, /Name/, 'North two');
    await waitFor(() => expect(pending()).toBe('2 pending'));
    // The list is still the one of the draft before: a press in it is of a line that may not be where it is drawn.
    await user.click(screen.getByRole('button', { name: 'Take back: set altitude 150' }));
    expect(pending()).toBe('2 pending');
    rig.release();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Take back: set name North two' })).not.toHaveAttribute('aria-disabled'));
    expect(rig.drafts?.current()?.lines).toEqual(['set altitude 150', 'set name North two']);
  });
});

describe('the draft a page holds (ADR-0016 note 54)', () => {
  it('note 57: a line is taken back from the draft the press was made on, and not from one that has changed since', () => {
    const { result } = renderHook(() => useDraft());
    act(() => result.current.drafts.open(TABLE, KEY, 7));
    act(() => result.current.drafts.gain('set altitude 150'));
    const seen = result.current.draft;
    if (seen === undefined) throw new Error('The draft was opened and is not held.');
    act(() => result.current.drafts.gain('set name North two'));
    let taken: boolean | undefined;
    act(() => { taken = result.current.drop(seen, 0); });
    expect(taken).toBe(false);
    expect(result.current.draft?.lines).toEqual(['set altitude 150', 'set name North two']);
    const now = result.current.draft;
    if (now === undefined) throw new Error('The draft is open and is not held.');
    act(() => { taken = result.current.drop(now, 0); });
    expect(taken).toBe(true);
    expect(result.current.draft?.lines).toEqual(['set name North two']);
  });
});
