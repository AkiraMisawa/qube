import { ApiError, outputOf, type Api } from '../api';
import type { Catalog, LineCompletion, LinePreview, RowsEdited, TakenBack } from '../contracts';
import type { Notify } from '../notices';
import type { Command, CommandDoor, Completion, LineReading, LineReason } from './commands';

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
  const run = (named: (revision: number) => Promise<Invocation | LineReason>): Promise<LineReason | undefined> =>
    page.turns.after(async (revision): Promise<LineReason | undefined> => {
      let told: Told;
      try {
        const call = await named(revision);
        if ('sentence' in call) return call;
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
    enter: (line, shown) => run(async (revision) => {
      const reading = shown ?? await page.api.preview(line, page.workspaceId, revision);
      if (reading.refused !== undefined) return reading.refused;
      if (reading.command === undefined || reading.inputs === undefined) throw new Error('The server read the line and named no command to run.');
      return { ...reading.command, inputs: reading.inputs };
    }),
    invoke: (capabilityId, request) => run(async (revision) => {
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
 * A command per word of each capability that says how it is typed, acting on the workspace of the page in the active
 * panel. Where that panel has no such page the commands are listed and say why they cannot run.
 */
export function servedCommands(catalog: Pick<Catalog, 'capabilities'>, panel: { readonly title: string } | undefined, door: CommandDoor | undefined, notify: Notify): Command[] {
  return catalog.capabilities.flatMap((capability) => (capability.command?.words ?? []).map((word, index): Command => {
    const words = word.toLowerCase();
    return {
      // A capability's first word carries its id, by which its use is counted; another spelling says which it is.
      id: index === 0 ? capability.capabilityId : `${capability.capabilityId}:${words}`,
      words,
      title: capability.title,
      group: 'Workspace',
      ...(door === undefined || panel === undefined || capability.command === undefined
        ? { unavailable: 'This panel has no workspace for it to act on.' }
        : { target: panel.title, page: true, served: { arguments: capability.command.arguments, door } }),
      // From a key or a button there is no line: the words alone are read, and a refusal is a notice.
      run: () => {
        if (door === undefined) throw new Error(`${words} ran with no workspace to act on.`);
        void door.enter(words).then((reason) => { if (reason !== undefined) notify('refused', `${words}: ${reason.sentence}`); });
      },
    };
  }));
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
