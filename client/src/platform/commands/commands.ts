// One command path for the command line, the shell's few keys and buttons (ADR-0006): each surface looks commands up
// here and runs them, so availability and behaviour cannot differ between surfaces. A command is found by typing the
// English words for what it does; its arguments are completed one slot at a time.

import type { LinePlace, LinePreview, LineView } from '../contracts';

/** A value a slot offers, with what it means. */
export interface Completion {
  readonly value: string;
  readonly detail?: string;
}

export interface CommandArgument {
  /** The slot's name, shown in the line (`currency`); an optional one is typed `name=value`. */
  readonly name: string;
  /** Required arguments are typed in order; optional ones by name, anywhere after the command. */
  readonly required: boolean;
  /** What the slot takes, shown while it is typed (`a tenor such as 10y, or a date`). */
  readonly hint?: string;
  /** Takes the rest of the line as typed, spaces included (the request to the AI). Only a last required argument. */
  readonly rest?: boolean;
  /**
   * With `rest`: the rest of the line is words read in any order (ADR-0008 §3), so completion offers values for the
   * word being typed now, given the whole rest as typed.
   */
  readonly words?: boolean;
  /** With `words`: how the words typed so far fill the command, part by part, for the line to show. */
  readonly parts?: (value: string) => readonly { readonly name: string; readonly value?: string; readonly required: boolean }[];
  /** The values this slot offers, given the arguments before it. The line keeps those that match what is typed. */
  readonly complete?: (given: Readonly<Record<string, string>>) => readonly Completion[];
  /** Why a value is refused; undefined when the slot takes it. */
  readonly check?: (value: string, given: Readonly<Record<string, string>>) => string | undefined;
}

export interface Command {
  readonly id: string;
  /** The English words that name it, lower case: a verb and its object (`price`, `new irs`, `live start`). */
  readonly words: string;
  /** What it does, in a sentence's words. */
  readonly title: string;
  readonly group: string;
  /** What it acts on now: the active panel by default (ADR-0006 §5). */
  readonly target?: string;
  /**
   * It acts on the page in the active panel: an application's own command while its page is open, with that panel's
   * title as its `target`. The empty line lists these first (ADR-0006's note 3 of 2026-11-11).
   */
  readonly page?: boolean;
  readonly args?: readonly CommandArgument[];
  /**
   * Its line is the server's to read (ADR-0016 §5): a command the catalog says how to type. The line reads nothing of
   * what follows its words; it shows the server's reading while that is typed, and Enter has the server run it.
   */
  readonly served?: Served;
  /** A shell chord in Mantine's notation (`mod+shift+G`). Only the shell's commands carry one (ADR-0006 §8). */
  readonly shortcut?: string;
  /** Why the command cannot run now; absent when it can. */
  readonly unavailable?: string;
  /**
   * Does it. It reads its arguments before it does anything, and throws `CommandRefused` with the reason when it cannot
   * read them, so that a refusal has changed nothing and the line can stay open with what was typed (ADR-0006's note 1
   * of 2026-11-11).
   */
  readonly run: (args: Readonly<Record<string, string>>) => void;
}

/** Why the server cannot read a line: its sentence, and where the word at fault stands in the line, where it stands there. */
export interface LineReason {
  readonly sentence: string;
  readonly start?: number;
  readonly end?: number;
}

/**
 * What a page says is on screen at one moment (ADR-0016 notes 16 and 21 of 2026-11-11): the view the line is given, and
 * how the grid it was read of takes a place a view verb's reading names for that view. `put` puts that grid's Focus on
 * the place and the keyboard in the grid; where it cannot, it answers why, and has moved nothing.
 */
export interface Screen {
  readonly view: LineView;
  readonly put: (place: LinePlace) => string | undefined;
}

/** A line that ended with its command having put the keyboard somewhere itself (`find`; ADR-0016 note 21 of 2026-11-11): the line gives none back. */
export interface KeyboardMoved {
  readonly moved: true;
}

/** How a line ended at its page's door: why nothing ran; or nothing, the command done; or done with the keyboard moved by it. */
export type Entered = LineReason | KeyboardMoved | undefined;

/**
 * What a page gives the command line for the workspace it shows (ADR-0016 §5): where the commands the server reads
 * act, and how they are run so that they follow the edits the page has already sent.
 */
export interface CommandDoor {
  readonly workspaceId: string;
  /** Resolves once the edits the page has sent so far are answered: a reading asked for after it has seen them. */
  settled(): Promise<void>;
  /**
   * What the user is looking at (ADR-0016 §9, note 16 of 2026-11-11): the grid the keyboard was last in, its selection
   * and its focused cell. The line reads it once as it opens. None where the page has no such grid, or none was entered.
   */
  view(): LineView | undefined;
  /**
   * Enter on a line, in the page's own turn, after the edits it has sent. The reading the line showed for exactly this
   * text is what is invoked, by its own inputs, which carry the revision it was read at: a workspace that has moved
   * since refuses it (ADR-0016 §2). With none shown the server reads the line at the revision the page then holds, with
   * `view`, the one the line opened on, and that reading is invoked. Resolves once the command is taken or refused: with
   * why nothing ran, and the line stays; or with nothing, the page showing what the command left and its notice said.
   * A view verb's reading invokes nothing (note 21 of 2026-11-11): the page puts the keyboard on the place it names, and
   * the door answers that the keyboard was moved.
   */
  enter(line: string, shown?: LinePreview, view?: LineView): Promise<Entered>;
  /**
   * A command by its id, from a key or a button, which have no line: in the same turn, at the catalog's version and
   * the revision the page then holds. Resolves as `enter` does.
   */
  invoke(capabilityId: string, request: Readonly<Record<string, unknown>>): Promise<LineReason | undefined>;
}

/** What a served command is typed with and run through: its arguments as the catalog names them, and its page's door. */
export interface Served {
  readonly arguments: readonly { readonly name: string; readonly required: boolean }[];
  readonly door: CommandDoor;
}

/**
 * A command's reason for doing nothing, thrown by its `run` before any effect: what was typed cannot be read, or what
 * it would act on is not there. Its message is said to the user as it is. Any other error a command throws is a defect,
 * and is not caught as a reason.
 */
export class CommandRefused extends Error {
  override readonly name = 'CommandRefused';
}

/**
 * Runs a command if it is available. Returns the reason when it is not, or when it refused what it was given, so a
 * caller can show it: a command that does not run never does so in silence. A defect is thrown on, to be reported as
 * one (ADR-0014 §4), never shown as a reason.
 */
export function execute(command: Command, args: Readonly<Record<string, string>> = {}): string | undefined {
  if (command.unavailable !== undefined) return command.unavailable;
  try {
    command.run(args);
  } catch (error) {
    if (error instanceof CommandRefused) return error.message;
    throw error;
  }
  return undefined;
}

/** Commands whose ids, words or chords collide would make the line or a key ambiguous; refuse them. */
export function assertDistinct(commands: readonly Command[]): void {
  for (const [what, key] of [['Command', (c: Command) => c.id], ['The words', (c: Command) => c.words]] as const) {
    const seen = new Map<string, string>();
    for (const command of commands) {
      const k = key(command);
      const other = seen.get(k);
      if (other !== undefined) throw new Error(`${what} ${k} name both ${other} and ${command.id}.`);
      seen.set(k, command.id);
    }
  }
  const shortcuts = new Map<string, string>();
  for (const command of commands) {
    if (command.shortcut === undefined) continue;
    const other = shortcuts.get(command.shortcut.toLowerCase());
    if (other !== undefined) throw new Error(`Shortcut ${command.shortcut} is bound to both ${other} and ${command.id}.`);
    shortcuts.set(command.shortcut.toLowerCase(), command.id);
  }
}

/** How often and how lately the user ran a command: higher first among equal matches (ADR-0006 §6). */
export type UsageScore = (commandId: string) => number;

/**
 * The commands a typed query matches, best first. The words typed in full come first, then words that start with the
 * query, then commands whose words each start with the query's words in order (`op pr` → `open pricing db`), then
 * those whose words or title contain the query. Within a tier, what the user runs most comes first, then what can
 * run now. The empty line and `?` are `listed`'s.
 */
export function rankCommands(query: string, commands: readonly Command[], usage: UsageScore = () => 0): Command[] {
  const q = query.trim().toLowerCase().replace(/\s+/g, ' ');
  const typed = q.split(' ');
  const tier = (command: Command): number => {
    if (command.words === q) return 0;
    if (command.words.startsWith(q)) return 1;
    if (inOrder(typed, command.words.split(' '))) return 2;
    const haystack = `${command.words} ${command.title.toLowerCase()} ${command.group.toLowerCase()}`;
    return typed.every((t) => haystack.includes(t)) ? 3 : -1;
  };
  return order(commands.map((command, index) => ({ command, index, tier: tier(command) })).filter((r) => r.tier >= 0), usage);
}

/** A part of the list the line shows while a command is chosen: its commands, under a heading where it has one. */
export interface ListSection {
  readonly heading: string | undefined;
  readonly commands: readonly Command[];
}

/** The heading of the commands that cannot run now, which the empty line lists last, each with its reason. */
export const NOT_AVAILABLE_NOW = 'Not available now';

/**
 * What the line lists while a command is being chosen (ADR-0006's note 3 of 2026-11-11). For the empty line and `?`:
 * the commands of the page in the active panel first, under the panel's title; then the other commands that can run,
 * a heading per group, in the order the groups were registered; and on the empty line those that cannot run now,
 * last. Under a heading, what the user runs most comes first (ADR-0006 §6), then the order registered. A typed query
 * is one list with no heading, as `rankCommands` ranks it.
 */
export function listed(query: string, commands: readonly Command[], usage: UsageScore = () => 0): readonly ListSection[] {
  const q = query.trim();
  if (q !== '' && q !== '?') {
    const ranked = rankCommands(query, commands, usage);
    return ranked.length === 0 ? [] : [{ heading: undefined, commands: ranked }];
  }
  const byUse = (list: readonly Command[]) => list.map((command, index) => ({ command, index }))
    .sort((a, b) => usage(b.command.id) - usage(a.command.id) || a.index - b.index).map((r) => r.command);
  const sections: ListSection[] = [];
  const can = commands.filter((c) => c.unavailable === undefined);
  const page = can.filter((c) => c.page === true);
  const first = page[0];
  if (first !== undefined) {
    if (first.target === undefined) throw new Error(`Command ${first.id} acts on the page in the active panel and does not say which panel.`);
    sections.push({ heading: first.target, commands: byUse(page) });
  }
  const groups = new Map<string, Command[]>();
  for (const command of can) {
    if (command.page === true) continue;
    const group = groups.get(command.group);
    if (group === undefined) groups.set(command.group, [command]);
    else group.push(command);
  }
  for (const [heading, group] of groups) sections.push({ heading, commands: byUse(group) });
  const cannot = commands.filter((c) => c.unavailable !== undefined);
  if (q === '' && cannot.length > 0) sections.push({ heading: NOT_AVAILABLE_NOW, commands: byUse(cannot) });
  return sections;
}

/** The line's placeholder: the first commands the empty line lists that can run, so it differs by page. */
export function placeholder(sections: readonly ListSection[]): string {
  const first = sections.flatMap((s) => s.commands).filter((c) => c.unavailable === undefined).slice(0, 3).map((c) => c.words);
  return first.length === 0 ? 'Type what you want to do (? lists the commands)' : `Type what you want to do: ${first.join(', ')}… (? lists them)`;
}

/** What the line draws of a list: at most `cap` of it, and how many it leaves out, which it says. Never cut in silence. */
export function drawn<T>(items: readonly T[], cap: number): { readonly shown: readonly T[]; readonly more: number } {
  return { shown: items.slice(0, cap), more: Math.max(items.length - cap, 0) };
}

/** A section as the line draws it, with where its first command stands among all those drawn. */
export interface DrawnSection extends ListSection { readonly start: number }

/** As `drawn`, for a list in sections: the cap counts commands, and a section it leaves nothing of is not drawn. */
export function drawnSections(sections: readonly ListSection[], cap: number): { readonly sections: readonly DrawnSection[]; readonly more: number } {
  const out: DrawnSection[] = [];
  let start = 0;
  let more = 0;
  for (const section of sections) {
    const commands = section.commands.slice(0, Math.max(cap - start, 0));
    more += section.commands.length - commands.length;
    if (commands.length > 0) out.push({ heading: section.heading, commands, start });
    start += commands.length;
  }
  return { sections: out, more };
}

function inOrder(typed: readonly string[], words: readonly string[]): boolean {
  let at = 0;
  for (const t of typed) {
    while (at < words.length && !words[at]!.startsWith(t)) at++;
    if (at === words.length) return false;
    at++;
  }
  return true;
}

function order(rows: { command: Command; index: number; tier: number }[], usage: UsageScore): Command[] {
  return rows
    .sort((a, b) => a.tier - b.tier
      || usage(b.command.id) - usage(a.command.id)
      || Number(a.command.unavailable !== undefined) - Number(b.command.unavailable !== undefined)
      || a.index - b.index)
    .map((r) => r.command);
}

/** What the line holds: a command still being chosen, or a chosen command and its arguments so far. */
export type LineReading =
  | { readonly kind: 'choosing'; readonly query: string }
  | {
    readonly kind: 'arguments';
    readonly command: Command;
    /** The arguments typed in full, by name. */
    readonly given: Readonly<Record<string, string>>;
    /** The slot being typed now, and what is typed in it so far; undefined once every slot is filled. */
    readonly slot: CommandArgument | undefined;
    readonly typed: string;
    /** Values the line cannot place: more than the command takes, or a name it does not have. */
    readonly problems: readonly string[];
  };

/**
 * Reads the line. A command is chosen once its words are typed in full and followed by a space; what follows is its
 * arguments: required ones in order, optional ones as `name=value`.
 */
export function readLine(text: string, commands: readonly Command[]): LineReading {
  const lead = text.replace(/^\s+/, '');
  const tokens = lead.split(/\s+/).filter((t) => t !== '');
  const lower = tokens.map((t) => t.toLowerCase());
  const closed = /\s$/.test(lead);
  let chosen: { command: Command; length: number } | undefined;
  for (const command of commands) {
    const words = command.words.split(' ');
    if (words.length > lower.length || (words.length === lower.length && !closed)) continue;
    if (words.every((w, i) => lower[i] === w) && (chosen === undefined || words.length > chosen.length)) chosen = { command, length: words.length };
  }
  if (chosen === undefined) return { kind: 'choosing', query: lead };

  const { command } = chosen;
  // A served command's line is the server's to read: nothing after its words is placed here.
  if (command.served !== undefined) return { kind: 'arguments', command, given: {}, slot: undefined, typed: '', problems: [] };
  const args = command.args ?? [];
  const rest = tokens.slice(chosen.length);
  const done = closed ? rest : rest.slice(0, -1);
  const typedNow = closed ? '' : rest.at(-1) ?? '';
  const given: Record<string, string> = {};
  const problems: string[] = [];
  const required = args.filter((a) => a.required);
  let next = 0;

  const restArg = required.find((a) => a.rest);
  if (restArg !== undefined) {
    // Everything after the words, as typed: a request to the AI keeps its spaces.
    const after = lead.slice(lead.toLowerCase().indexOf(command.words) + command.words.length).replace(/^\s+/, '');
    const typedRest = restArg.words === true ? typedNow : after;
    return { kind: 'arguments', command, given: after === '' ? {} : { [restArg.name]: after }, slot: restArg, typed: typedRest, problems: [] };
  }

  const place = (token: string): void => {
    const eq = token.indexOf('=');
    if (eq > 0) {
      const name = token.slice(0, eq).toLowerCase();
      const arg = args.find((a) => !a.required && a.name === name);
      if (arg === undefined) problems.push(`${command.words} has no option ${name}.`);
      else given[arg.name] = token.slice(eq + 1);
      return;
    }
    const arg = required[next++];
    if (arg === undefined) problems.push(`${command.words} takes no more values: ${token}.`);
    else given[arg.name] = token;
  };
  for (const token of done) place(token);

  let slot: CommandArgument | undefined;
  let typed = typedNow;
  const eq = typedNow.indexOf('=');
  if (eq > 0) {
    slot = args.find((a) => !a.required && a.name === typedNow.slice(0, eq).toLowerCase());
    typed = typedNow.slice(eq + 1);
  } else slot = required[next];
  return { kind: 'arguments', command, given, slot, typed, problems };
}

/** The values a reading's slot offers once a command is chosen; none while one is being chosen, which `listed` lists. */
export function completionsOf(reading: LineReading): readonly Completion[] {
  if (reading.kind === 'choosing') return [];
  const { command, given, slot, typed } = reading;
  const options = slot === undefined
    // Every required slot is filled: offer the options not given yet.
    ? (command.args ?? []).filter((a) => !a.required && given[a.name] === undefined).map((a) => ({ value: `${a.name}=`, ...(a.hint === undefined ? {} : { detail: a.hint }) }))
    : slot.complete?.(given) ?? [];
  const t = typed.toLowerCase();
  const starts = options.filter((o) => o.value.toLowerCase().startsWith(t));
  const contains = options.filter((o) => !o.value.toLowerCase().startsWith(t) && (o.value.toLowerCase().includes(t) || (o.detail ?? '').toLowerCase().includes(t)));
  return [...starts, ...contains];
}

/** What Enter would run, or every reason it would not: missing arguments by name, refused values, unplaced ones. */
export function finish(reading: Extract<LineReading, { kind: 'arguments' }>): { readonly args: Readonly<Record<string, string>> } | { readonly problems: readonly string[] } {
  const { command, slot, typed } = reading;
  const args: Record<string, string> = { ...reading.given };
  // A rest of words is given whole; what is typed now is only its last word.
  if (slot !== undefined && typed !== '' && slot.words !== true) args[slot.name] = typed;
  const problems = [...reading.problems];
  // A value typed last once every slot is filled is one more than the command takes, as one typed before it is.
  if (slot === undefined && typed !== '') problems.push(`${command.words} takes no more values: ${typed}.`);
  const missing = (command.args ?? []).filter((a) => a.required && (args[a.name] ?? '') === '').map((a) => a.name);
  if (missing.length > 0) problems.push(`Missing: ${missing.join(', ')}.`);
  for (const arg of command.args ?? []) {
    const value = args[arg.name];
    if (value === undefined || value === '') continue;
    const refused = arg.check?.(value, args);
    if (refused !== undefined) problems.push(`${arg.name}: ${refused}`);
  }
  return problems.length > 0 ? { problems } : { args };
}

/** The line once a completion is taken: the command's words, or the slot's value, then a space for the next slot. */
export function take(text: string, reading: LineReading, completion: Command | Completion): string {
  if ('words' in completion) return `${completion.words} `;
  if (reading.kind === 'choosing') return text;
  // A rest taken whole, spaces and all, is replaced whole: a name of several words is not appended to its first ones.
  const whole = reading.slot?.rest === true && reading.slot.words !== true;
  const keep = whole ? text.slice(0, text.length - reading.typed.length) : text.replace(/\S*$/, '');
  const named = reading.slot !== undefined && !reading.slot.required ? `${reading.slot.name}=` : '';
  return completion.value.endsWith('=') ? `${keep}${completion.value}` : `${keep}${named}${completion.value} `;
}

/** A command's arguments as the line shows them: `<currency> <index> [rate=]`. */
export function signature(command: Command): string {
  // A served command's arguments are typed in order, with no name: `[rows] <set>`.
  if (command.served !== undefined) return command.served.arguments.map((a) => (a.required ? `<${a.name}>` : `[${a.name}]`)).join(' ');
  return (command.args ?? []).map((a) => (a.required ? `<${a.name}>` : `[${a.name}=]`)).join(' ');
}
