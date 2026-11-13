import { CommandRefused, given, type Command, type CommandArgument } from '../platform/commands/commands';
import type { Notify } from '../platform/notices';
import { namedBy, panelLabel, pinnedCount, readName, type Panel, type PanelsClosed, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { areaHolding, currentArrangement, FLOAT_PLACES, floatBox, SIDES, type Arrangement, type FloatPlace, type Side } from './arrangement';
import { AREA_MIN, type AreaOf } from './layout';

// The shell's verbs over its panels (ADR-0006 §8 and note 7 of 2026-11-12; ADR-0018 §3). Each is declared once here,
// and the command line, a tab's menu, an area's list of panels and the keys of a tab all run that declaration: none of
// them can do what the others cannot (ADR-0006 §1). They act on the screen only, so they are the client's, never
// logged, never undone by `undo`, and never the AI's (ADR-0016 §3: what the AI calls are capabilities, not page
// automation). Where the panels stand in areas, the verbs reach the arrangement through its port (`arrangement.ts`),
// and note 7's act within the panel's own area (ADR-0006 note 8).

/** What the verbs read and change: the workspace store's panels. */
export type PanelState = Pick<WorkspaceState,
  'panels' | 'activePanelId' | 'closed' | 'activate' | 'closePanel' | 'closePanels' | 'reopenPanel' | 'movePanel' | 'pinPanel' | 'renamePanel'>;

/** What a verb acts on: the store's panels, and the arrangement they stand in. */
export interface On {
  readonly state: PanelState;
  readonly areas: Arrangement;
}

/** How a verb is named: by the line, which lists it, and by a menu. */
interface VerbNames {
  readonly id: string;
  /** The words the line takes. */
  readonly words: string;
  /** What it does, as the line lists it. */
  readonly title: string;
  /** What a menu calls it. */
  readonly label: string;
}

type Args = Readonly<Record<string, string>>;

/** A verb over one panel: the active one on the line, the one a menu was opened on elsewhere. */
export interface PanelVerb extends VerbNames {
  /** A shell chord (ADR-0006 §8): only the verbs that had one before note 7. */
  readonly shortcut?: string;
  readonly args?: readonly CommandArgument[];
  /** Its arguments as the line takes them over these panels, where their completion reads them. */
  readonly argsOn?: (on: On) => readonly CommandArgument[];
  /** Why it cannot act on this panel now, with these arguments where they are given; absent when it can. */
  readonly unavailable: (on: On, panel: Panel, args?: Args) => string | undefined;
  /** Does it to this panel. Throws `CommandRefused` with the reason where its arguments say what it cannot do. */
  readonly run: (on: On, panel: Panel, notify: Notify, args: Args) => void;
}

/** A verb over the panels as a whole, which no one panel is the object of. */
export interface StripVerb extends VerbNames {
  readonly unavailable: (on: On) => string | undefined;
  readonly run: (on: On, notify: Notify) => void;
}

/**
 * The open panel a typed text names: the one labelled so, else the only one whose label contains the text. Two of one
 * title are told apart by their number (`Rates 2026-10-27 · 2`); a part that several contain is named back, never
 * guessed between.
 */
export function panelTitled(panels: readonly Panel[], text: string): { readonly panel: Panel } | { readonly reason: string } {
  const t = text.trim().toLowerCase();
  if (t === '') return { reason: 'Type the title of an open panel.' };
  const exact = panels.filter((p) => panelLabel(p).toLowerCase() === t);
  const matching = exact.length > 0 ? exact : panels.filter((p) => panelLabel(p).toLowerCase().includes(t));
  if (matching.length === 1) return { panel: matching[0]! };
  return matching.length === 0
    ? { reason: `No open panel is titled like "${text.trim()}".` }
    : { reason: `${matching.length} panels match "${text.trim()}": ${matching.map(panelLabel).join(', ')}.` };
}

const open = (state: PanelState, id: string): Panel => {
  const panel = state.panels.find((p) => p.id === id);
  if (panel === undefined) throw new Error(`The arrangement holds ${id}, which is not open.`);
  return panel;
};

/**
 * The panels of the strip a panel stands in, in its order: its area's where the panels stand in areas, else every
 * panel (ADR-0006 note 8). A note 7 verb that names "the others" or "to the right" reads these.
 */
function stripOf(on: On, panel: Panel): readonly Panel[] {
  if (on.areas.strip !== undefined) return on.state.panels;
  const area = areaHolding(on.areas, panel.id);
  if (area === undefined) throw new Error(`No area holds ${panel.id}.`);
  return area.panels.map((id) => open(on.state, id));
}

/** Which area a panel stands in, for the store to follow the active panel in it (ADR-0018 §4). */
const areaOf = (on: On) => (on.areas.strip !== undefined ? undefined : (id: string) => areaHolding(on.areas, id)?.id);

/** The first and last place a panel can take in its strip: among the pinned, or among the others. */
const reach = (strip: readonly Panel[], panel: Panel): readonly [number, number] => {
  const pinned = pinnedCount(strip);
  return panel.pinned === true ? [0, pinned - 1] : [pinned, strip.length - 1];
};

const MOVES = ['left', 'right', 'first', 'last'] as const;
type Move = (typeof MOVES)[number];
const moveOf = (where: string | undefined): Move | undefined => MOVES.find((m) => m === where?.toLowerCase());
/** Where a move puts the panel in its strip, or why it cannot. */
function moved(strip: readonly Panel[], panel: Panel, move: Move): { readonly to: number } | { readonly reason: string } {
  const at = strip.findIndex((p) => p.id === panel.id);
  const [first, last] = reach(strip, panel);
  const kind = panel.pinned === true ? 'pinned panel' : 'panel';
  const edge = move === 'left' || move === 'first' ? first : last;
  if (at === edge) return { reason: `This is the ${move === 'left' || move === 'first' ? 'first' : 'last'} ${kind} already.` };
  return { to: move === 'left' ? at - 1 : move === 'right' ? at + 1 : edge };
}

/** Says what a close of several did, and how to have a panel back (ADR-0006's note 2: a command says what it did). */
function closedSaid(notify: Notify, done: { readonly closed: number; readonly pinned: number }): void {
  const kept = done.pinned === 0 ? '' : ` ${done.pinned} pinned ${done.pinned === 1 ? 'panel was' : 'panels were'} kept.`;
  notify('done', `Closed ${done.closed} ${done.closed === 1 ? 'panel' : 'panels'}.${kept} Bring one back with reopen panel.`);
}

/** The panels a close of several reads: its strip's for the others and the right, every panel for all. */
const closing = (on: On, panel: Panel, which: PanelsClosed): readonly Panel[] => (which === 'all' ? on.state.panels : stripOf(on, panel));

/** Why a close of several takes nothing from this panel's strip; absent when it takes one or more. */
function nothingToClose(on: On, panel: Panel, which: PanelsClosed): string | undefined {
  const named = namedBy(closing(on, panel, which), which, panel.id);
  const where = which === 'all' || on.areas.strip !== undefined ? '' : ' in its area';
  if (named.length === 0) return which === 'right' ? `No panel is to the right of this one${where}.` : `There is no other panel${where}.`;
  return named.every((p) => p.pinned === true) ? `The ${which === 'right' ? 'panels to the right' : which === 'all' ? 'panels' : 'other panels'} are pinned.` : undefined;
}

function closeSeveral(on: On, panel: Panel, notify: Notify, which: PanelsClosed): void {
  const strip = which === 'all' ? undefined : closing(on, panel, which).map((p) => p.id);
  closedSaid(notify, on.state.closePanels(which, panel.id, strip, areaOf(on)));
}

export const CLOSE: PanelVerb = {
  id: 'panel.close',
  words: 'close panel',
  title: 'Close the panel',
  label: 'Close',
  shortcut: 'mod+shift+X',
  unavailable: () => undefined,
  run: (on, panel) => on.state.closePanel(panel.id, areaOf(on)),
};

export const CLOSE_OTHERS: PanelVerb = {
  id: 'panel.closeOthers',
  words: 'close other panels',
  title: 'Close every panel of its area but this one and the pinned',
  label: 'Close others',
  unavailable: (on, panel) => nothingToClose(on, panel, 'others'),
  run: (on, panel, notify) => closeSeveral(on, panel, notify, 'others'),
};

export const CLOSE_RIGHT: PanelVerb = {
  id: 'panel.closeRight',
  words: 'close panels to the right',
  title: 'Close the panels to the right of this one in its area, but the pinned',
  label: 'Close to the right',
  unavailable: (on, panel) => nothingToClose(on, panel, 'right'),
  run: (on, panel, notify) => closeSeveral(on, panel, notify, 'right'),
};

export const CLOSE_ALL: PanelVerb = {
  id: 'panel.closeAll',
  words: 'close all panels',
  title: 'Close every panel but the pinned',
  label: 'Close all',
  unavailable: (on, panel) => nothingToClose(on, panel, 'all'),
  run: (on, panel, notify) => closeSeveral(on, panel, notify, 'all'),
};

/** Pins or unpins, and keeps the panel's area in the same order as the strip: the pinned first (ADR-0006 note 8). */
function pin(on: On, panel: Panel, pinned: boolean): void {
  const area = on.areas.strip === undefined ? areaHolding(on.areas, panel.id) : undefined;
  on.state.pinPanel(panel.id, pinned);
  if (area === undefined) return;
  const others = area.panels.filter((id) => id !== panel.id).map((id) => open(on.state, id));
  on.areas.moveWithin(panel.id, pinnedCount(others));
}

export const PIN: PanelVerb = {
  id: 'panel.pin',
  words: 'pin panel',
  title: 'Pin the panel at the start of its strip, where closing several leaves it open',
  label: 'Pin',
  unavailable: (_, panel) => (panel.pinned === true ? 'This panel is pinned already.' : undefined),
  run: (on, panel) => pin(on, panel, true),
};

export const UNPIN: PanelVerb = {
  id: 'panel.unpin',
  words: 'unpin panel',
  title: 'Unpin the panel',
  label: 'Unpin',
  unavailable: (_, panel) => (panel.pinned === true ? undefined : 'This panel is not pinned.'),
  run: (on, panel) => pin(on, panel, false),
};

export const MOVE: PanelVerb = {
  id: 'panel.move',
  words: 'move panel',
  title: 'Move the panel along its strip',
  label: 'Move',
  args: [{
    name: 'where', required: true, hint: 'left, right, first or last',
    complete: () => MOVES.map((value) => ({ value })),
    check: (value) => (MOVES.some((m) => m === value.toLowerCase()) ? undefined : `one of ${MOVES.join(', ')}`),
  }],
  // With a where given, as a menu's Move left gives one, it says too whether the panel can go there.
  unavailable: (on, panel, args) => {
    const strip = stripOf(on, panel);
    const [first, last] = reach(strip, panel);
    if (first === last) return `There is no other ${panel.pinned === true ? 'pinned panel' : 'panel'} to move it past.`;
    const move = moveOf(args?.where);
    if (move === undefined) return undefined;
    const to = moved(strip, panel, move);
    return 'reason' in to ? to.reason : undefined;
  },
  run: (on, panel, _, args) => {
    // A where the line could not read is a reason; none at all is a defect (`given`).
    const move = moveOf(given(args, 'where'));
    if (move === undefined) throw new CommandRefused(`move panel takes where to: ${MOVES.join(', ')}.`);
    const to = moved(stripOf(on, panel), panel, move);
    if ('reason' in to) throw new CommandRefused(to.reason);
    if (on.areas.strip === undefined) on.areas.moveWithin(panel.id, to.to);
    else on.state.movePanel(panel.id, to.to);
  },
};

/** Why a typed name cannot name this panel, where it cannot (`readName`). */
const nameRefusedFor = (panel: Panel, typed: string): string | undefined => {
  const read = readName(panel.title, typed);
  return 'refused' in read ? read.refused : undefined;
};

export const RENAME: PanelVerb = {
  id: 'panel.rename',
  words: 'rename panel',
  title: 'Give the panel a name of your own, or its page\'s title back',
  label: 'Rename',
  // The rest of the line is the name, read against the active panel, on which the line acts: completed with the page's
  // own title, which gives that back, and refused on the line where it cannot be a name.
  argsOn: (on) => {
    const active = on.state.panels.find((p) => p.id === on.state.activePanelId);
    return [{
      name: 'name', required: true, rest: true, hint: 'a name of your own, or its page\'s title to have that back',
      ...(active === undefined ? {} : {
        complete: () => [{ value: active.title, detail: 'its page\'s title' }],
        check: (typed: string) => nameRefusedFor(active, typed),
      }),
    }];
  },
  unavailable: () => undefined,
  run: (on, panel, _, args) => {
    const typed = given(args, 'name');
    const refused = nameRefusedFor(panel, typed);
    if (refused !== undefined) throw new CommandRefused(refused);
    on.state.renamePanel(panel.id, typed);
  },
};

// ADR-0018 §3: the verbs that arrange. Each is refused in the strip, saying the arrangement comes back with the room.

const sideOf = (text: string | undefined): Side | undefined => SIDES.find((s) => s === text?.trim().toLowerCase());
const placeOf = (text: string | undefined): FloatPlace | undefined => {
  const t = text?.trim().toLowerCase();
  return FLOAT_PLACES.find((p) => p === (t === 'center' ? 'centre' : t));
};

/** The area of a panel while areas are shown; one a verb is given that no area holds is a defect. */
function areaFor(on: On, panel: Panel) {
  const area = areaHolding(on.areas, panel.id);
  if (area === undefined) throw new Error(`No area holds ${panel.id}.`);
  return area;
}

/** The areas of the main window, neither floating nor in another window. */
const docked = (on: On) => on.areas.areas().filter((a) => a.where === 'docked');

/**
 * What a verb that takes a panel out of the main window's areas says of the last panel docked there (§5, §9): the window
 * would be left with none docked. Undefined where the panel is not that one.
 */
function lastDocked(on: On, area: AreaOf, doing: string): string | undefined {
  return area.where === 'docked' && area.panels.length === 1 && docked(on).length === 1
    ? `It is the only panel docked in the window; ${doing} would leave none docked.` : undefined;
}

/** What a verb that arranges inside the main window says of a panel in another window, which is one area there. */
const IN_A_WINDOW = 'It is in a window of its own, which is one area: dock it first.';

export const SPLIT: PanelVerb = {
  id: 'panel.split',
  words: 'split panel',
  title: 'Move the panel into a new area beside its own',
  label: 'Split',
  args: [{
    name: 'side', required: true, hint: 'right, left, down or up',
    complete: () => SIDES.map((value) => ({ value })),
    check: (value) => (sideOf(value) === undefined ? `one of ${SIDES.join(', ')}` : undefined),
  }],
  unavailable: (on, panel, args) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    const area = areaFor(on, panel);
    if (area.where === 'floating') return 'A floating area is one area: dock it first, or float another panel.';
    if (area.where === 'window') return IN_A_WINDOW;
    if (area.panels.length === 1) return 'It is the only panel of its area: send it to another area, or float it.';
    const side = sideOf(args?.side);
    if (side === undefined) return undefined;
    const across = side === 'right' || side === 'left';
    const has = across ? area.width : area.height;
    const min = across ? AREA_MIN.width : AREA_MIN.height;
    if (has < 2 * min) return `Two areas ${across ? 'side by side' : 'one above the other'} need ${2 * min}px ${across ? 'wide' : 'high'}; this one is ${Math.floor(has)}px.`;
    return undefined;
  },
  run: (on, panel, _, args) => {
    const side = sideOf(given(args, 'side'));
    if (side === undefined) throw new CommandRefused(`split panel takes a side: ${SIDES.join(', ')}.`);
    const why = SPLIT.unavailable(on, panel, args);
    if (why !== undefined) throw new CommandRefused(why);
    on.areas.split(panel.id, side);
  },
};

export const SEND: PanelVerb = {
  id: 'panel.send',
  words: 'send panel to',
  title: 'Move the panel into the area of another panel',
  label: 'Send to',
  args: [{ name: 'panel', required: true, rest: true, hint: 'the title of a panel in the area, or a part of it' }],
  // Completed with the other areas, each by the panel it shows, as the menu lists them.
  argsOn: (on) => [{
    name: 'panel', required: true, rest: true, hint: 'the title of a panel in the area, or a part of it',
    complete: () => otherAreas(on, on.state.activePanelId).map((a) => ({ value: panelLabel(open(on.state, a.shown)) })),
  }],
  unavailable: (on, panel, args) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    const own = areaFor(on, panel);
    if (on.areas.areas().length < 2) return 'There is no other area.';
    if (args?.panel === undefined) return undefined;
    const named = panelTitled(on.state.panels, args.panel);
    if ('reason' in named) return named.reason;
    if (own.panels.includes(named.panel.id)) return `${panelLabel(named.panel)} is in this panel's area already.`;
    // Into a floating area or another window, the main window's last docked panel would leave none docked (§5, §9).
    return areaFor(on, named.panel).where === 'docked' ? undefined : lastDocked(on, own, 'sending it there');
  },
  run: (on, panel, _, args) => {
    const why = SEND.unavailable(on, panel, args);
    if (why !== undefined) throw new CommandRefused(why);
    const named = panelTitled(on.state.panels, given(args, 'panel'));
    if ('reason' in named) throw new CommandRefused(named.reason);
    on.areas.sendTo(panel.id, areaFor(on, named.panel).id);
    on.state.activate(panel.id);
  },
};

/** The areas other than the panel's own, each with the panel it shows: where `send panel to` can send it. */
export function otherAreas(on: On, panelId: string | undefined): readonly { readonly id: string; readonly shown: string }[] {
  if (on.areas.strip !== undefined) return [];
  return on.areas.areas().flatMap((a) => (a.shown === undefined || (panelId !== undefined && a.panels.includes(panelId)) ? [] : [{ id: a.id, shown: a.shown }]));
}

export const FLOAT: PanelVerb = {
  id: 'panel.float',
  words: 'float panel',
  title: 'Float the panel over the others, in the middle or over a half',
  label: 'Float',
  // Required, as `split panel`'s side is: the line takes an optional value only by its name (ADR-0006), and the words
  // the menu shows beside Float ▸ Left are the words the line runs.
  args: [{
    name: 'where', required: true, hint: 'centre, left, right, top or bottom',
    complete: () => FLOAT_PLACES.map((value) => ({ value })),
    check: (value) => (placeOf(value) === undefined ? `one of ${FLOAT_PLACES.join(', ')}` : undefined),
  }],
  unavailable: (on, panel) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    const area = areaFor(on, panel);
    if (area.where === 'window') return IN_A_WINDOW;
    return lastDocked(on, area, 'floating it');
  },
  run: (on, panel, _, args) => {
    const place = placeOf(given(args, 'where'));
    if (place === undefined) throw new CommandRefused(`float panel takes where: ${FLOAT_PLACES.join(', ')}.`);
    const why = FLOAT.unavailable(on, panel, args);
    if (why !== undefined) throw new CommandRefused(why);
    on.areas.float(panel.id, floatBox(place, on.areas.room()));
    on.state.activate(panel.id);
  },
};

export const DOCK: PanelVerb = {
  id: 'panel.dock',
  words: 'dock panel',
  title: 'Put a floating panel, or one in another window, back among the main window\'s areas',
  label: 'Dock',
  unavailable: (on, panel) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    const area = areaFor(on, panel);
    return area.where === 'docked' ? 'This panel is neither floating nor in another window.' : undefined;
  },
  run: (on, panel, notify) => {
    const why = DOCK.unavailable(on, panel);
    if (why !== undefined) throw new CommandRefused(why);
    const where = on.areas.dock(panel.id);
    on.state.activate(panel.id);
    notify('done', `Docked ${panelLabel(panel)} ${where}.`);
  },
};

const SHARES = ['25', '33', '50', '67', '75'] as const;
const percentOf = (text: string | undefined): number | undefined => {
  const m = /^\s*(\d{1,2})\s*%?\s*$/.exec(text ?? '');
  const n = m === null ? undefined : Number(m[1]);
  return n !== undefined && n >= 10 && n <= 90 ? n : undefined;
};

export const SIZE: PanelVerb = {
  id: 'panel.size',
  words: 'size panel',
  title: 'Give the panel\'s area a share of its row or column',
  label: 'Size',
  args: [{
    name: 'percent', required: true, hint: 'its share, from 10 to 90%',
    complete: () => SHARES.map((value) => ({ value: `${value}%` })),
    check: (value) => (percentOf(value) === undefined ? 'a whole percent from 10 to 90' : undefined),
  }],
  unavailable: (on, panel, args) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    const area = areaFor(on, panel);
    if (area.where === 'floating') return 'A floating area is sized by its edges, or placed with float panel.';
    if (area.where === 'window') return 'A panel in a window of its own is sized by that window.';
    const line = on.areas.lineOf(area.id);
    if (line === undefined) return 'Its area is alone in its row and its column: there is nothing to share.';
    const percent = percentOf(args?.percent);
    if (percent === undefined) return undefined;
    const pixels = Math.round((line.total * percent) / 100);
    const min = line.axis === 'width' ? AREA_MIN.width : AREA_MIN.height;
    const word = line.axis === 'width' ? 'wide' : 'high';
    if (pixels < min) return `${percent}% is ${pixels}px; an area is at least ${min}px ${word}.`;
    if (line.total - pixels < line.othersNeed) return `${percent}% leaves ${line.total - pixels}px; the others need ${line.othersNeed}px ${word}.`;
    return undefined;
  },
  run: (on, panel, _, args) => {
    const percent = percentOf(given(args, 'percent'));
    if (percent === undefined) throw new CommandRefused('size panel takes a whole percent from 10 to 90.');
    const why = SIZE.unavailable(on, panel, args);
    if (why !== undefined) throw new CommandRefused(why);
    const area = areaFor(on, panel);
    const line = on.areas.lineOf(area.id);
    if (line === undefined) throw new Error(`The area of ${panel.id} has no row to share.`);
    on.areas.size(area.id, Math.round((line.total * percent) / 100));
  },
};

export const MAXIMIZE: PanelVerb = {
  id: 'panel.maximize',
  words: 'maximize panel',
  title: 'Give the panel\'s area the whole of the panels\' space',
  label: 'Maximize',
  unavailable: (on, panel) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    const area = areaFor(on, panel);
    if (area.where === 'floating') return 'A floating area is not maximized; dock it first.';
    if (area.where === 'window') return IN_A_WINDOW;
    if (on.areas.maximized() === area.id) return 'Its area is maximized already.';
    if (docked(on).length < 2) return 'Its area is the only one of the window: it has the whole room already.';
    return undefined;
  },
  run: (on, panel) => {
    const why = MAXIMIZE.unavailable(on, panel);
    if (why !== undefined) throw new CommandRefused(why);
    on.areas.maximize(areaFor(on, panel).id);
  },
};

export const UNMAXIMIZE: PanelVerb = {
  id: 'panel.unmaximize',
  words: 'unmaximize panel',
  title: 'Show every area again',
  label: 'Restore the areas',
  unavailable: (on) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    return on.areas.maximized() === undefined ? 'No area is maximized.' : undefined;
  },
  run: (on, panel) => {
    const why = UNMAXIMIZE.unavailable(on, panel);
    if (why !== undefined) throw new CommandRefused(why);
    on.areas.unmaximize();
  },
};

export const GATHER: PanelVerb = {
  id: 'panel.gather',
  words: 'gather panels',
  title: 'Put every panel back in one area, the panel\'s own',
  label: 'Gather all panels here',
  unavailable: (on, panel) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    if (on.areas.areas().length < 2) return 'The panels are in one area already.';
    // Into the panel's own area, which is the main window's: every other window closes as its panels leave it.
    return areaFor(on, panel).where === 'window' ? 'It is in a window of its own: gather the panels from an area of the main window.' : undefined;
  },
  run: (on, panel) => {
    const why = GATHER.unavailable(on, panel);
    if (why !== undefined) throw new CommandRefused(why);
    const pinned = new Set(on.state.panels.filter((p) => p.pinned === true).map((p) => p.id));
    const read = on.areas.areas().flatMap((a) => a.panels);
    on.areas.gather(areaFor(on, panel).id, [...read.filter((id) => pinned.has(id)), ...read.filter((id) => !pinned.has(id))]);
  },
};

/** Note 7's verbs over one panel, and note 9's `rename panel`, as the line lists them. */
export const PANEL_VERBS: readonly PanelVerb[] = [CLOSE, CLOSE_OTHERS, CLOSE_RIGHT, CLOSE_ALL, PIN, UNPIN, MOVE, RENAME];

/**
 * ADR-0018 §9: moves the panel into an area of its own in a new window of the browser, for another monitor. Run from a
 * key or a press (the line's Enter, a menu), so the browser opens the window; one it does not open is said where the
 * areas hear of it, and the panel stays where it stood.
 */
export const POP_OUT: PanelVerb = {
  id: 'panel.popOut',
  words: 'pop out panel',
  title: 'Move the panel into a new window of the browser, for another monitor',
  label: 'Pop out',
  unavailable: (on, panel) => {
    if (on.areas.strip !== undefined) return on.areas.strip;
    const area = areaFor(on, panel);
    if (area.where === 'window' && area.panels.length === 1) return 'It is in a window of its own already.';
    return lastDocked(on, area, 'popping it out');
  },
  run: (on, panel) => {
    const why = POP_OUT.unavailable(on, panel);
    if (why !== undefined) throw new CommandRefused(why);
    on.areas.popOut(panel.id);
  },
};

/** ADR-0018 §3's verbs that arrange, and §9's, as the line lists them. */
export const ARRANGE_VERBS: readonly PanelVerb[] = [SPLIT, SEND, FLOAT, POP_OUT, DOCK, SIZE, MAXIMIZE, UNMAXIMIZE, GATHER];

/** A verb that acts on no one panel: what the line, the list of panels and a tab's menu offer beside the verbs above. */
export const REOPEN: StripVerb = {
  id: 'panel.reopen',
  words: 'reopen panel',
  title: 'Open the panel closed last again, where it stood',
  label: 'Reopen closed panel',
  unavailable: (on) => (on.state.closed.length === 0 ? 'No panel has been closed.' : undefined),
  run: (on, notify) => {
    const last = on.state.closed.at(-1);
    const panel = on.state.reopenPanel();
    if (panel === undefined || last === undefined) throw new CommandRefused('No panel has been closed.');
    // Back in the area it stood in, where that is still there, else beside the active panel, and the user is told which
    // (ADR-0018 §4), by a panel that stands beside it there. A page open again since it closed is shown where it is.
    if (on.areas.strip !== undefined || last.area === undefined) return;
    /** The title of the panel named, where it is another than the one reopened: the panel it now stands beside. */
    const neighbourTitle = (id: string | undefined): string | undefined => {
      const beside = on.state.panels.find((p) => p.id === id && p.id !== panel.id);
      return beside === undefined ? undefined : panelLabel(beside);
    };
    const now = areaHolding(on.areas, panel.id);
    const there = on.areas.areas().find((a) => a.id === last.area);
    if (there === undefined) {
      const beside = neighbourTitle(on.state.activePanelId);
      notify('done', `Reopened ${panelLabel(panel)}${beside === undefined ? '' : ` beside ${beside}`}: the area it stood in has gone.`);
      return;
    }
    const beside = neighbourTitle(there.shown) ?? neighbourTitle(there.panels.find((id) => id !== panel.id));
    if (now?.id !== there.id) on.areas.sendTo(panel.id, there.id);
    // Back in its area, among others, it is said; in one area there is no other place it could be.
    if (on.areas.areas().length > 1) notify('done', `Reopened ${panelLabel(panel)}${beside === undefined ? '' : ` beside ${beside}`}, in the area it stood in.`);
  },
};

/** Does a verb from a menu or a key: a reason it cannot is a refused notice, as a chord's is (ADR-0006's note 1). */
function refusedSaid(words: string, why: string | undefined, notify: Notify, act: () => void): void {
  if (why !== undefined) { notify('refused', `${words}: ${why}`); return; }
  try {
    act();
  } catch (error) {
    if (!(error instanceof CommandRefused)) throw error;
    notify('refused', `${words}: ${error.message}`);
  }
}

/** The verbs act on the store's panels as they stand, and the arrangement the drawn panels registered. */
export const onNow = (state: PanelState, areas: Arrangement = currentArrangement()): On => ({ state, areas });

/** Runs a verb on a panel from a menu or a key. */
export function runVerb(verb: PanelVerb, on: On, panel: Panel, notify: Notify, args: Args = {}): void {
  refusedSaid(verb.words, verb.unavailable(on, panel, args), notify, () => verb.run(on, panel, notify, args));
}

/** Runs a verb over the panels from a menu. */
export function runStripVerb(verb: StripVerb, on: On, notify: Notify): void {
  refusedSaid(verb.words, verb.unavailable(on), notify, () => verb.run(on, notify));
}

/** The order `next panel` and `previous panel` go through: every panel in reading order (ADR-0018 §3). */
function readingOrder(on: On): readonly string[] {
  return on.areas.strip !== undefined ? on.state.panels.map((p) => p.id) : on.areas.areas().flatMap((a) => a.panels);
}

function step(on: On, by: 1 | -1): void {
  const order = readingOrder(on);
  if (order.length === 0) return;
  const active = on.state.activePanelId;
  // With none active, the first panel is next and the last is previous.
  const at = active === undefined ? (by === 1 ? -1 : order.length) : order.indexOf(active);
  if (at === -1 && active !== undefined) throw new Error(`The active panel ${active} is not in the reading order.`);
  const to = order[(at + by + order.length) % order.length];
  if (to === undefined) throw new Error('The reading order has no panel to step to.');
  on.state.activate(to);
}

/** The line's commands over the panels: each verb on the active panel, `reopen panel`, and moving between panels. */
export function panelCommands(state: PanelState, notify: Notify, areas: Arrangement = currentArrangement()): Command[] {
  const on = onNow(state, areas);
  const active = state.panels.find((p) => p.id === state.activePanelId);
  const unavailableOn = (verb: PanelVerb, panel: Panel) => {
    const why = verb.unavailable(on, panel);
    return why === undefined ? {} : { unavailable: why };
  };
  const onActive = (verb: PanelVerb): Command => ({
    id: verb.id,
    words: verb.words,
    title: verb.title,
    group: 'Panels',
    ...(verb.shortcut === undefined ? {} : { shortcut: verb.shortcut }),
    ...(verb.argsOn !== undefined ? { args: verb.argsOn(on) } : verb.args === undefined ? {} : { args: verb.args }),
    ...(active === undefined ? { unavailable: 'No panel is open.' } : { target: panelLabel(active), ...unavailableOn(verb, active) }),
    run: (args) => {
      // The line's list is made again whenever the panels change, so this is the panel shown as it runs, from a chord too.
      if (active === undefined) throw new CommandRefused('No panel is open.');
      verb.run(on, active, notify, args);
    },
  });
  const noneClosed = REOPEN.unavailable(on);
  return [
    {
      id: 'panel.next',
      words: 'next panel',
      title: 'Show the next panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowRight',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => step(on, 1),
    },
    {
      id: 'panel.previous',
      words: 'previous panel',
      title: 'Show the previous panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowLeft',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => step(on, -1),
    },
    {
      id: 'panel.show',
      words: 'show panel',
      title: 'Show an open panel by its title',
      group: 'Panels',
      args: [{
        name: 'panel', required: true, rest: true, hint: 'its title, or a part of it',
        complete: () => state.panels.map((p) => ({ value: panelLabel(p) })),
        check: (text) => { const named = panelTitled(state.panels, text); return 'reason' in named ? named.reason : undefined; },
      }],
      ...(state.panels.length === 0 ? { unavailable: 'No panel is open.' } : {}),
      run: (a) => {
        const named = panelTitled(state.panels, given(a, 'panel'));
        if ('reason' in named) throw new CommandRefused(named.reason);
        state.activate(named.panel.id);
      },
    },
    ...PANEL_VERBS.map(onActive),
    {
      id: REOPEN.id,
      words: REOPEN.words,
      title: REOPEN.title,
      group: 'Panels',
      ...(noneClosed === undefined ? {} : { unavailable: noneClosed }),
      run: () => REOPEN.run(on, notify),
    },
    ...ARRANGE_VERBS.map(onActive),
  ];
}
