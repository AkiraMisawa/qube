import { ApiError, outputOf, type Api } from '../api';
import type { Catalog, CommandForm, LineArgument, LineCompletion, LinePreview, LineView, RowsEdited, TableOnScreen, TakenBack } from '../contracts';
import type { Notify } from '../notices';
import type { Command, CommandDoor, Completion, KeyboardMoved, LineReading, LineReason, Screen } from './commands';

// The commands the catalog says how to type (ADR-0016 §4, §5): the line builds each from the catalog and reads nothing
// of its line itself. The server reads the line, while it is typed and at Enter; what the line does with the answer is
// here, and so is the door a page opens for them. Nothing here knows a table, a field or an application.

/** A page's edits, sent one at a time: a command takes its turn among them, and they chain on what it leaves. */
export interface Turns {
  /** Runs `command` after the edits already sent, with the revision the page then holds; edits sent later wait for it. */
  after<T>(command: (revision: number) => Promise<T>): Promise<T>;
  /** The workspace stands at this revision: the page's next edit chains on it. */
  rebase(revision: number): void;
  /** Resolves once everything taken so far is answered. */
  settled(): Promise<void>;
}

/** What a page has for its door: its workspace, its turns, and how it shows the workspace once a command has moved it. */
export interface DoorPage {
  readonly workspaceId: string;
  readonly catalog: Pick<Catalog, 'capabilities'>;
  readonly api: Pick<Api, 'preview' | 'command'>;
  readonly turns: Turns;
  /** Reads the workspace again and shows it; answers the revision it read. */
  reread(): Promise<number>;
  readonly notify: Notify;
  /**
   * What the user is looking at, where the page has a grid of a table (ADR-0016 note 16), with how that grid takes a
   * place read for it (note 21); read as the line opens.
   */
  readonly screen?: () => Screen | undefined;
}

/** What the door reads of a command's answer, which every command that writes a workspace gives: where it left it, and what it did. */
type Told = Pick<TakenBack | RowsEdited, 'revision' | 'said'>;

interface Invocation {
  readonly id: string;
  readonly version: string;
  readonly inputs: Readonly<Record<string, unknown>>;
}

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

/**
 * The door of a page that holds a workspace (ADR-0016 §5), the same for every such page. A command runs in the page's
 * turn. Refused, by the reading or by the write, nothing ran and the reason is answered, for the line to keep; where
 * the reason is that the workspace has moved, the page reads it again first, so that what is tried next is tried
 * against what the page then shows. Taken, the page chains on the revision it left and shows the workspace as read
 * again, and the notice is the server's sentence of what was done.
 */
export function commandDoor(page: DoorPage): CommandDoor {
  // Each view the page gave the line, with how the grid it was read of takes a place: a place read for a view is put
  // in that grid, whatever the screen shows by then.
  const puts = new WeakMap<LineView, Screen['put']>();
  const run = <Ended extends LineReason | KeyboardMoved>(named: (revision: number) => Promise<Invocation | Ended>): Promise<Ended | LineReason | undefined> =>
    page.turns.after(async (revision): Promise<Ended | LineReason | undefined> => {
      let told: Told;
      try {
        const call = await named(revision);
        // Nothing to invoke: the line cannot be read, or what it asked for is done already.
        if (!('id' in call)) return call;
        told = outputOf<Told>(await page.api.command(call.id, call.version, call.inputs, crypto.randomUUID()), 'answer');
      } catch (error) {
        const sentence = describe(error);
        if (!(error instanceof ApiError && error.envelope.code === 'RevisionMoved')) return { sentence };
        return page.reread().then(
          (stands) => { page.turns.rebase(stands); return { sentence }; },
          (unread: unknown) => ({ sentence: `${sentence} The page could not read the workspace again: ${describe(unread)}` }));
      }
      page.turns.rebase(told.revision);
      // The command is done whether or not the page can read what it left: the notice then says both.
      try {
        page.turns.rebase(await page.reread());
      } catch (error) {
        page.notify('refused', `${told.said} The page could not read the workspace again: ${describe(error)}`);
        return undefined;
      }
      page.notify('done', told.said);
      return undefined;
    // The page's own earlier edit was refused, and this with it: nothing was sent.
    }).catch((error: unknown) => ({ sentence: describe(error) }));

  return {
    workspaceId: page.workspaceId,
    settled: () => page.turns.settled(),
    view: () => {
      const seen = page.screen?.();
      if (seen === undefined) return undefined;
      puts.set(seen.view, seen.put);
      return seen.view;
    },
    enter: (line, shown, view) => run<LineReason | KeyboardMoved>(async (revision) => {
      // A place is of the workspace as it stood when it was read, and no write will refuse one that has moved since:
      // a reading of another revision than the page now holds is asked for again.
      const kept = shown !== undefined && (shown.place === undefined || shown.revision === revision) ? shown : undefined;
      const reading = kept ?? await page.api.preview(line, page.workspaceId, { revision, ...(view === undefined ? {} : { view }) });
      if (reading.refused !== undefined) return reading.refused;
      if (reading.place !== undefined) {
        // A view verb (ADR-0016 note 21): nothing is invoked, and the page puts the keyboard where the reading says.
        const put = view === undefined ? undefined : puts.get(view);
        if (put === undefined) throw new Error('The server named a place, and this page gave the line no grid to put the keyboard on.');
        const why = put(reading.place);
        if (why !== undefined) return { sentence: why };
        page.notify('done', reading.place.said);
        return { moved: true };
      }
      if (reading.command === undefined || reading.inputs === undefined) throw new Error('The server read the line and named no command to run.');
      return { ...reading.command, inputs: reading.inputs };
    }),
    invoke: (capabilityId, request) => run<LineReason>(async (revision) => {
      const capability = page.catalog.capabilities.find((c) => c.capabilityId === capabilityId);
      if (capability === undefined) throw new Error(`This server has no command ${capabilityId}.`);
      return { id: capabilityId, version: capability.version, inputs: { request: { ...request, workspaceId: page.workspaceId, revision } } };
    }),
  };
}

const doors = new Map<string, CommandDoor>();

/** A page registers its door while it is mounted, by its panel, and takes it away when it goes. */
export function registerCommandDoor(panelId: string, door: CommandDoor | undefined): void {
  if (door === undefined) doors.delete(panelId);
  else doors.set(panelId, door);
}

export function commandDoorOf(panelId: string | undefined): CommandDoor | undefined {
  return panelId === undefined ? undefined : doors.get(panelId);
}

/**
 * A command per word of each capability that says how it is typed, and of each verb of the screen the catalog lists
 * (ADR-0016 note 21), acting on the workspace of the page in the active panel. Where that panel has no such page the
 * commands are listed and say why they cannot run.
 */
export function servedCommands(catalog: Pick<Catalog, 'capabilities' | 'viewVerbs'>, panel: { readonly title: string } | undefined, door: CommandDoor | undefined, notify: Notify): Command[] {
  const typed: readonly { readonly id: string; readonly title: string; readonly form: CommandForm }[] = [
    ...catalog.capabilities.flatMap((c) => (c.command === undefined ? [] : [{ id: c.capabilityId, title: c.title, form: c.command }])),
    // A verb is no capability, and its id says so: its use is counted apart from any command's.
    ...catalog.viewVerbs.map((v) => ({ id: `view.${v.verbId}`, title: v.title, form: v.command })),
  ];
  return typed.flatMap(({ id, title, form }) => form.words.map((word, index): Command => {
    const words = word.toLowerCase();
    return {
      // Its first word carries its id, by which its use is counted; another spelling says which it is.
      id: index === 0 ? id : `${id}:${words}`,
      words,
      title,
      group: 'Workspace',
      ...(door === undefined || panel === undefined
        ? { unavailable: 'This panel has no workspace for it to act on.' }
        : { target: panel.title, page: true, served: { arguments: form.arguments, door } }),
      // From a key or a button there is no line: the words alone are read, with what is on screen at the press, and a
      // refusal is a notice.
      run: () => {
        if (door === undefined) throw new Error(`${words} ran with no workspace to act on.`);
        void door.enter(words, undefined, door.view()).then((ended) => { if (ended !== undefined && 'sentence' in ended) notify('refused', `${words}: ${ended.sentence}`); });
      },
    };
  }));
}

/** What a table's actions make of a grid's Context Menu: its items, and what the menu calls each. */
export interface TableMenu {
  /** One item per action, each running its line with `view`, what the menu was opened over. */
  commands(view: LineView): readonly { readonly id: string; readonly enabled: boolean; readonly invoke: () => Promise<void> }[];
  /** What the menu calls an item of its own: its title and the line that does the same. None for another's id. */
  label(id: string): string | undefined;
}

/**
 * A table's actions as the Context Menu of a grid bound to it offers them (ADR-0016 §4, note 25 of 2026-11-11). Each
 * is run as a served command is from a key: the server reads its line with the menu's view, in the page's turn, and a
 * refusal is a notice. Every action is offered on every cell: what one would do there is the server's to say. Until
 * the page has read its tables (`table` is not there yet) the menu holds none of them.
 */
export function tableMenu(table: TableOnScreen | undefined, door: Pick<CommandDoor, 'enter'>, notify: Notify): TableMenu {
  const actions = table === undefined ? [] : table.actions.map((action) => ({ ...action, id: `${table.tableId}:${action.line}` }));
  return {
    commands: (view) => actions.map((action) => ({
      id: action.id,
      enabled: true,
      invoke: async () => {
        const ended = await door.enter(action.line, undefined, view);
        if (ended !== undefined && 'sentence' in ended) notify('refused', `${action.line}: ${ended.sentence}`);
      },
    })),
    label: (id) => { const action = actions.find((a) => a.id === id); return action === undefined ? undefined : `${action.title} · ${action.line}`; },
  };
}

/**
 * The line the server is asked to read, while it is typed: the text as it is once it holds a served command's words and
 * a space; the words of a served command that takes no argument while it is the one chosen in the list, so that what it
 * would do is seen before Enter; and nothing otherwise.
 */
export function lineAsked(text: string, reading: LineReading, chosen: Command | Completion | undefined): string | undefined {
  if (reading.kind === 'arguments') return reading.command.served === undefined ? undefined : text;
  return chosen !== undefined && 'words' in chosen && chosen.served?.arguments.length === 0 ? chosen.words : undefined;
}

/**
 * The line once a completion is taken: written from its place, then a space, unless it is a name with the sign that
 * awaits its value (`unit=`). An operator taken alone (`=`, `<=`) is a word of its own, and a space follows it.
 */
export function taken(line: string, completion: LineCompletion): string {
  return `${line.slice(0, completion.start)}${completion.value}${/[^\s=<>!~]=$/.test(completion.value) ? '' : ' '}`;
}

/**
 * An argument as the line names it under its command: its name and the words read for it. One the user did not type
 * says where it came from (ADR-0016 §9, note 18 of 2026-11-11): in words, since a colour alone would not be read out,
 * and before the words read, so that a narrow line that cuts the end of many rows still says it.
 */
export function argumentShown(argument: LineArgument): string {
  return argument.from === 'context' ? `${argument.name} from the screen: ${argument.text}` : `${argument.name} ${argument.text}`;
}

/** The screen a line was opened on, as the line says it: the server's sentence, and whether its rows can still be said. */
export interface ScreenShown {
  readonly sentence: string;
  readonly moved: boolean;
}

/**
 * What the line says of its screen (ADR-0016 §9, note 19 of 2026-11-11): the sentence of its latest reading where that
 * has one, so that a view gone stale while the line is open is said as soon as the server says it, and otherwise the
 * sentence asked for as the line opened. Whether the order has moved is the same reading's word, never another's.
 */
export function screenShown(latest: LinePreview | undefined, opening: LinePreview | undefined): ScreenShown | undefined {
  const said = latest?.view === undefined ? opening : latest;
  return said?.view === undefined ? undefined : { sentence: said.view, moved: said.viewMoved === true };
}

/** How many of a reading's first rows the line names; the server sends a few more than a line has room for. */
const ROWS_NAMED = 5;

/** What the line shows of a reading: its rows and what would change, or why the line cannot be read. */
export interface PreviewShown {
  readonly rows?: string;
  readonly would?: string;
  /** When the command the reading speaks of ran, as the server wrote it. */
  readonly at?: string;
  readonly reason?: string;
}

export function previewShown(preview: LinePreview): PreviewShown {
  if (preview.refused !== undefined) return { reason: preview.refused.sentence };
  const { rows } = preview;
  const named = rows === undefined || rows.first.length === 0 ? undefined
    : `${rows.title}: ${rows.first.slice(0, ROWS_NAMED).join(', ')}${rows.count > Math.min(rows.first.length, ROWS_NAMED) ? ', …' : ''}`;
  return {
    ...(rows === undefined ? {} : { rows: named ?? rows.title }),
    ...(preview.would === undefined ? {} : { would: preview.would }),
    ...(preview.at === undefined ? {} : { at: preview.at }),
  };
}
