import { ApiError, outputOf, type Api, type CommandAnswer } from '../api';
import type { Catalog, CommandForm, GridAsk, LineArgument, LineCompletion, LinePreview, LineView, RowsEdited, TableOnScreen, TakenBack } from '../contracts';
import type { Notify } from '../notices';
import type { Command, CommandDoor, Completion, KeyboardMoved, LineReading, LineReason, Screen } from './commands';
import { browserOut, type TakenOut } from './takenOut';
import { panelLabel, type Panel } from '../workspace/workspaceStore';

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
  /** The application the workspace is of, as the server names it. */
  readonly app: string;
  readonly catalog: Pick<Catalog, 'capabilities'>;
  readonly api: Pick<Api, 'preview' | 'command' | 'content'>;
  readonly turns: Turns;
  /** Reads the workspace again and shows it; answers the revision it read. */
  reread(): Promise<number>;
  readonly notify: Notify;
  /**
   * What the user is looking at, where the page has a grid of a table (ADR-0016 note 16), with how that grid takes a
   * place read for it (note 21); read as the line opens.
   */
  readonly screen?: () => Screen | undefined;
  /**
   * What the page does with what a command of its own answered, beyond showing the workspace again, such as opening
   * the Run it made (ADR-0016 note 36). Told once the command is done, whether or not the page could read what it left.
   */
  readonly answered?: (capabilityId: string, answer: Told) => void;
}

/**
 * What the door reads of a command's answer, which every command of a workspace gives: where it left it, and what it
 * did; and `stays`, where what it said waits for the user (ADR-0016 note 33), such as edits a refresh holds back. What
 * else a command answers is its own, for the page that knows the command.
 */
export type Told = Pick<TakenBack | RowsEdited, 'revision' | 'said'> & { readonly stays?: boolean } & Readonly<Record<string, unknown>>;

/**
 * A command's answer as the door reads it (ADR-0016 note 36): its output named `answer`, or its one output where it
 * has one, as `entity.create` has its `entity`. Several outputs with none named `answer`, or an output that says
 * neither where the workspace was left nor what was done, is a command that broke the door's contract: that is said,
 * and no other output is read in its place.
 */
function toldOf(answer: CommandAnswer): Told {
  const ports = Object.keys(answer.outputs);
  const port = ports.includes('answer') ? 'answer' : ports.length === 1 ? ports[0]! : undefined;
  if (port === undefined) throw new Error(`${answer.capabilityId} answered ${ports.length === 0 ? 'nothing' : ports.join(' and ')}, and none is its answer.`);
  const told = outputOf<Partial<Told>>(answer, port);
  if (typeof told.revision !== 'number' || typeof told.said !== 'string') throw new Error(`${answer.capabilityId} answered without the revision it left the workspace at and what it did.`);
  return told as Told;
}

interface Invocation {
  readonly id: string;
  readonly version: string;
  readonly inputs: Readonly<Record<string, unknown>>;
}

/** How what a view verb's reading asks of a grid is done (ADR-0016 notes 21, 39 and 43), and what the line is told. */
interface Handing {
  /** The ask as a defect names it (`a sort`), and what a grid was to do for it (`sort`). */
  readonly named: string;
  readonly toDo: string;
  /** Whether doing it puts the keyboard somewhere: the line then gives none back. */
  readonly moves: boolean;
  /** Hands the ask to the grid the view was read of, and answers why where the grid refuses it. */
  readonly handTo: (screen: Screen) => string | undefined;
}

/** How each thing a reading asks of the grid its view was read of is handed to that grid. */
function ofGrid(asks: GridAsk): Handing {
  switch (asks.kind) {
    case 'place': return { named: 'a place', toDo: 'put the keyboard on', moves: true, handTo: (screen) => screen.put(asks) };
    case 'sort': return { named: 'a sort', toDo: 'sort', moves: false, handTo: (screen) => screen.sort(asks) };
    case 'filter': return { named: 'a filter', toDo: 'filter', moves: false, handTo: (screen) => screen.filter(asks) };
    case 'select': return { named: 'rows to select', toDo: 'select them in', moves: false, handTo: (screen) => screen.select(asks) };
  }
}

const describe = (error: unknown) => (error instanceof Error ? error.message : String(error));

/**
 * The door of a page that holds a workspace (ADR-0016 §5), the same for every such page. A command runs in the page's
 * turn. Refused, by the reading or by the write, nothing ran and the reason is answered, for the line to keep; where
 * the reason is that the workspace has moved, the page reads it again first, so that what is tried next is tried
 * against what the page then shows. Taken, the page chains on the revision it left and shows the workspace as read
 * again, and the notice is the server's sentence of what was done. What `copy` and `export` take out (note 44) goes
 * where `out` puts it: the browser's clipboard and a file, unless another is given.
 */
export function commandDoor(page: DoorPage, out: TakenOut = browserOut): CommandDoor {
  // Each view the page gave the line, with the grid it was read of: what a view verb asks for a view is done with
  // that grid, whatever the screen shows by then.
  const screens = new WeakMap<LineView, Screen>();
  // `heard` is the caller that says the answer itself, such as a dialog: it is given the sentence, and no notice is made.
  const run = <Ended extends LineReason | KeyboardMoved>(named: (revision: number) => Promise<Invocation | Ended | undefined>, heard?: (said: string) => void): Promise<Ended | LineReason | undefined> =>
    page.turns.after(async (revision): Promise<Ended | LineReason | undefined> => {
      let told: Told;
      let ran: string;
      try {
        const call = await named(revision);
        // Nothing to invoke: the line cannot be read, or what it asked for is done already.
        if (call === undefined || !('id' in call)) return call;
        told = toldOf(await page.api.command(call.id, call.version, call.inputs, crypto.randomUUID()));
        ran = call.id;
      } catch (error) {
        const sentence = describe(error);
        if (!(error instanceof ApiError && error.envelope.code === 'RevisionMoved')) return { sentence };
        return page.reread().then(
          (stands) => { page.turns.rebase(stands); return { sentence }; },
          (unread: unknown) => ({ sentence: `${sentence} The page could not read the workspace again: ${describe(unread)}` }));
      }
      page.turns.rebase(told.revision);
      // The command is done whether or not the page can read what it left: the notice then says both, and a caller
      // that says the answer itself is still told what was done.
      try {
        page.turns.rebase(await page.reread());
      } catch (error) {
        page.notify('refused', `${told.said} The page could not read the workspace again: ${describe(error)}`);
        heard?.(told.said);
        page.answered?.(ran, told);
        return undefined;
      }
      page.answered?.(ran, told);
      // What waits for the user's decision does not leave by itself.
      if (heard === undefined) page.notify('done', told.said, ...(told.stays === true ? [{ stays: true }] : []));
      else heard(told.said);
      return undefined;
    // The page's own earlier edit was refused, and this with it: nothing was sent.
    }).catch((error: unknown) => ({ sentence: describe(error) }));

  return {
    workspaceId: page.workspaceId,
    app: page.app,
    settled: () => page.turns.settled(),
    view: () => {
      const seen = page.screen?.();
      if (seen === undefined) return undefined;
      screens.set(seen.view, seen);
      return seen.view;
    },
    enter: (line, shown, view) => run<LineReason | KeyboardMoved>(async (revision) => {
      // What a view verb asks is of the workspace as it stood when it was read, and no write will refuse one that has
      // moved since: a reading of another revision than the page now holds is asked for again.
      const kept = shown !== undefined && (shown.asks === undefined || shown.revision === revision) ? shown : undefined;
      const reading = kept ?? await page.api.preview(line, page.workspaceId, { revision, ...(view === undefined ? {} : { view }) });
      if (reading.refused !== undefined) return reading.refused;
      const { asks } = reading;
      if (asks?.kind === 'copy' || asks?.kind === 'export') {
        // Cells to take out (note 44): the server reads the line again at the reading's revision and answers them,
        // refused where the workspace has moved; nothing is invoked, and nothing is said done until they are out.
        const content = await page.api.content(line, page.workspaceId, { revision: reading.revision, ...(view === undefined ? {} : { view }) });
        if (content.refused !== undefined) return content.refused;
        if (content.text === undefined) throw new Error('The server answered neither the cells of the line nor why it has none.');
        if (asks.kind === 'export') out.save(asks.fileName, content.text);
        else {
          try {
            await out.copy(content.text);
          } catch (error) {
            return { sentence: `The browser did not take the cells on its clipboard: ${describe(error)}` };
          }
        }
        page.notify('done', asks.said);
        return undefined;
      }
      if (asks !== undefined) {
        // A view verb of a grid (ADR-0016 notes 21, 39 and 43): nothing is invoked, and the grid does what is asked.
        const grid = ofGrid(asks);
        const screen = view === undefined ? undefined : screens.get(view);
        if (screen === undefined) throw new Error(`The server named ${grid.named}, and this page gave the line no grid to ${grid.toDo}.`);
        const why = grid.handTo(screen);
        if (why !== undefined) return { sentence: why };
        page.notify('done', asks.said);
        return grid.moves ? { moved: true } : undefined;
      }
      if (reading.command === undefined || reading.inputs === undefined) throw new Error('The server read the line and named no command to run.');
      return { ...reading.command, inputs: reading.inputs };
    }),
    invoke: (capabilityId, request, heard) => run<LineReason>(async (revision) => {
      const capability = page.catalog.capabilities.find((c) => c.capabilityId === capabilityId);
      if (capability === undefined) throw new Error(`This server has no command ${capabilityId}.`);
      // A request made of what the page shows (note 37) is made here, in the turn, of the revision it is sent at.
      const made = typeof request === 'function' ? request(revision) : request;
      return { id: capabilityId, version: capability.version, inputs: { request: { ...made, workspaceId: page.workspaceId, revision } } };
    }, heard),
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
 * A command per word of each capability that says how it is typed, and of each verb of the screen and each short form
 * the catalog lists (ADR-0016 notes 21 and 29), acting on the workspace of the page in the active panel. Where that panel has no such page the
 * commands are listed and say why they cannot run. A command of one application (note 27) is listed on a page of that
 * application alone: elsewhere its words are no command, as the server reads them.
 */
export function servedCommands(catalog: Pick<Catalog, 'capabilities' | 'viewVerbs' | 'shortForms'>, panel: Pick<Panel, 'title' | 'copy'> | undefined, door: CommandDoor | undefined, notify: Notify): Command[] {
  const typed: readonly { readonly id: string; readonly title: string; readonly form: CommandForm }[] = [
    ...catalog.capabilities.flatMap((c) => (c.command === undefined ? [] : [{ id: c.capabilityId, title: c.title, form: c.command }])),
    // A verb is no capability, and its id says so: its use is counted apart from any command's.
    ...catalog.viewVerbs.map((v) => ({ id: `view.${v.verbId}`, title: v.title, form: v.command })),
    // Nor is a short form (note 29): the server reads it as the line it stands for, and that line's command runs.
    ...catalog.shortForms.map((f) => ({ id: `short.${f.formId}`, title: f.title, form: f.command })),
  ];
  return typed.filter(({ form }) => form.app === undefined || form.app === door?.app).flatMap(({ id, title, form }) => form.words.map((word, index): Command => {
    const words = word.toLowerCase();
    return {
      // Its first word carries its id, by which its use is counted; another spelling says which it is.
      id: index === 0 ? id : `${id}:${words}`,
      words,
      title,
      group: 'Workspace',
      ...(door === undefined || panel === undefined
        ? { unavailable: 'This panel has no workspace for it to act on.' }
        : { target: panelLabel(panel), page: true, served: { arguments: form.arguments, door, ...(form.app === undefined ? {} : { app: form.app }) } }),
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
 * a space; the words of a served command that needs no argument (it takes none, or every one may be left out, as `pv01
 * retry` may its trade; ADR-0016 note 33) while it is the one chosen in the list, so that what it would do is seen
 * before Enter; and nothing otherwise.
 */
export function lineAsked(text: string, reading: LineReading, chosen: Command | Completion | undefined): string | undefined {
  if (reading.kind === 'arguments') return reading.command.served === undefined ? undefined : text;
  if (chosen === undefined || !('words' in chosen) || chosen.served === undefined) return undefined;
  return chosen.served.arguments.every((a) => !a.required) ? chosen.words : undefined;
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

/** An argument of a served command as the line draws it: by its name, with what the server read for it where it read it. */
export interface ArgumentDrawn {
  readonly name: string;
  readonly required: boolean;
  readonly read?: LineArgument;
}

/**
 * A served command's arguments as the line draws them (ADR-0016 note 36): the form's, each with what the server read
 * for it, then what the server read under a name the form does not list, as a product's terms are read by their
 * fields. Those stand for an argument of the form that may be left out and was not read by its own name, so that one
 * is not drawn beside them as left out.
 */
export function argumentsDrawn(form: readonly { readonly name: string; readonly required: boolean }[], read: readonly LineArgument[]): readonly ArgumentDrawn[] {
  const beyond = read.filter((r) => !form.some((a) => a.name === r.name));
  return [
    ...form.flatMap((a): ArgumentDrawn[] => {
      const of = read.find((r) => r.name === a.name);
      if (of !== undefined) return [{ ...a, read: of }];
      return a.required || beyond.length === 0 ? [{ ...a }] : [];
    }),
    ...beyond.map((r) => ({ name: r.name, required: false, read: r })),
  ];
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
  /** The line a short form stands for (ADR-0016 note 29): what Enter runs, and what `undo` will name. */
  readonly expands?: string;
  readonly rows?: string;
  readonly would?: string;
  /** When the command the reading speaks of ran, as the server wrote it. */
  readonly at?: string;
  readonly reason?: string;
}

export function previewShown(preview: LinePreview): PreviewShown {
  const expands = preview.expands === undefined ? {} : { expands: preview.expands };
  if (preview.refused !== undefined) return { ...expands, reason: preview.refused.sentence };
  const { rows } = preview;
  const named = rows === undefined || rows.first.length === 0 ? undefined
    : `${rows.title}: ${rows.first.slice(0, ROWS_NAMED).join(', ')}${rows.count > Math.min(rows.first.length, ROWS_NAMED) ? ', …' : ''}`;
  return {
    ...expands,
    ...(rows === undefined ? {} : { rows: named ?? rows.title }),
    ...(preview.would === undefined ? {} : { would: preview.would }),
    ...(preview.at === undefined ? {} : { at: preview.at }),
  };
}
