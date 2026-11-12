import { useMemo } from 'react';
import { screen, waitFor, within } from '@testing-library/react';
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
  ({ name, title, word: name, editable: true, optional: false, form: 'text', takes: 'a text', ...over });

const FIELDS: readonly FieldInput[] = [
  field('name', 'Name', { group: 'Site' }),
  field('altitude', 'Altitude', { group: 'Site', form: 'number', takes: 'a number', unit: 'm' }),
  field('kind', 'Kind', { group: 'Sensors', form: 'choice', takes: 'rain or wind', choices: ['rain', 'wind'] }),
  field('open', 'Open', { group: 'Sensors', form: 'flag', takes: 'on or off' }),
  field('note', 'Note', { optional: true }),
  field('id', 'Id', { editable: false }),
];
const SOURCE: Readonly<Record<string, string>> = { name: 'North one', altitude: '120', kind: 'rain', open: 'on', note: 'empty', id: KEY };
const COASTAL: DraftTemplate = { word: 'coastal', title: 'Coastal station', takes: [{ name: 'altitude', title: 'Altitude', required: true, form: 'number', takes: 'a number' }] };
const SENSOR: DraftTemplate = { word: 'sensor', title: 'Sensor', takes: [{ name: 'load', title: 'Load', required: true, form: 'number', takes: 'a number' }] };
const SENSORS = { table: 't.sensors', title: 'Sensors of the station', word: 'sensors', fields: [field('load', 'Load')], rows: [], makes: [SENSOR] };

const formOf = (draft: LineDraft, made: boolean): DraftForm => {
  const written = { ...SOURCE };
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
  redrafts = 0;
  drafts: Drafts | undefined;

  readonly api = {
    draft: (_workspace: string, draft: LineDraft): Promise<DraftForm> => (this.left
      ? Promise.reject(new ApiError(409, { code: 'RevisionMoved', message: `The workspace is at revision 8 and the draft of ${draft.key} was opened at 7.`, issues: [] }))
      : Promise.resolve(formOf(draft, this.made))),
  };

  door(drafts: Drafts): Pick<CommandDoor, 'enter' | 'view' | 'redraft'> {
    const view = (): LineView | undefined => { const draft = drafts.current(); return draft === undefined ? undefined : { table: draft.table, draft }; };
    return {
      view,
      enter: (line, _shown, given) => {
        this.entered.push({ line, view: given });
        const sentence = this.refused.get(line);
        if (sentence !== undefined) return Promise.resolve({ sentence });
        // As the page's door does: `apply` written closes the draft, and any other line read is one the draft gains.
        if (line === 'apply') drafts.close();
        else drafts.gain(line);
        return Promise.resolve(undefined);
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
    await typed(user, /Altitude/, '150');
    await user.tab();
    await waitFor(() => expect(pending()).toBe('1 pending'));
    expect(lines(rig)).toEqual(['set altitude 150']);
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

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await user.click(screen.getByRole('button', { name: 'Discard' }));
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());
    expect(rig.drafts?.current()).toBeUndefined();
    expect(lines(rig)).toEqual(['set altitude 150']);
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
    await user.click(await screen.findByRole('button', { name: 'Discard' }));
    await waitFor(() => expect(screen.queryByTestId('draft-pending')).not.toBeInTheDocument());
    expect(rig.entered).toEqual([]);
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
  });
});
