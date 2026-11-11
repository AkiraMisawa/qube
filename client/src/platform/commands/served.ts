import type { Catalog, LineCompletion, LinePreview } from '../contracts';
import type { Notify } from '../notices';
import type { Command, Completion, LineReading, LineReason } from './commands';

// The commands the catalog says how to type (ADR-0016 §4, §5): the line builds each from the catalog and reads nothing
// of its line itself. The server reads the line, while it is typed and at Enter; what the line does with the answer is
// here. Nothing here knows a table, a field or an application.

/**
 * What a page gives the command line for the workspace it shows: where the served commands act, and how they are run
 * so that they follow the edits the page has already sent.
 */
export interface CommandDoor {
  readonly workspaceId: string;
  /**
   * In the page's own turn, after the edits it has sent: has the server read the line at the revision the page then
   * holds, and invokes that reading. Resolves once the line is read: with why it cannot be, and then nothing ran; or
   * with nothing, and the command runs on, its result the page's to say.
   */
  enter(line: string): Promise<LineReason | undefined>;
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
        : { target: panel.title, page: true, served: { arguments: capability.command.arguments, workspaceId: door.workspaceId, enter: door.enter } }),
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
