import { CommandRefused, type Command, type CommandArgument } from '../platform/commands/commands';
import type { Notify } from '../platform/notices';
import { namedBy, panelLabel, pinnedCount, type Panel, type PanelsClosed, type WorkspaceState } from '../platform/workspace/workspaceStore';

// The shell's verbs over its panels (ADR-0006 §8 and note 7 of 2026-11-12). Each is declared once here, and the command
// line, a tab's menu, the strip's list of panels and the keys of a tab all run that declaration: none of them can do
// what the others cannot (ADR-0006 §1). They act on the screen only, so they are the client's, never logged, never
// undone by `undo`, and never the AI's (ADR-0016 §3: what the AI calls are capabilities, not page automation).

/** What the verbs read and change: the workspace store's panels. */
export type PanelState = Pick<WorkspaceState,
  'panels' | 'activePanelId' | 'closed' | 'activate' | 'cycle' | 'closePanel' | 'closePanels' | 'reopenPanel' | 'movePanel' | 'pinPanel'>;

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
  /** Why it cannot act on this panel now, with these arguments where they are given; absent when it can. */
  readonly unavailable: (state: PanelState, panel: Panel, args?: Args) => string | undefined;
  /** Does it to this panel. Throws `CommandRefused` with the reason where its arguments say what it cannot do. */
  readonly run: (state: PanelState, panel: Panel, notify: Notify, args: Args) => void;
}

/** A verb over the strip as a whole, which no one panel is the object of. */
export interface StripVerb extends VerbNames {
  readonly unavailable: (state: PanelState) => string | undefined;
  readonly run: (state: PanelState) => void;
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

const indexOf = (state: PanelState, panel: Panel) => state.panels.findIndex((p) => p.id === panel.id);
/** The first and last place a panel can take: among the pinned, or among the others. */
const reach = (state: PanelState, panel: Panel): readonly [number, number] => {
  const pinned = pinnedCount(state.panels);
  return panel.pinned === true ? [0, pinned - 1] : [pinned, state.panels.length - 1];
};

const MOVES = ['left', 'right', 'first', 'last'] as const;
type Move = (typeof MOVES)[number];
const moveOf = (args: Args | undefined): Move | undefined => MOVES.find((m) => m === args?.where?.toLowerCase());
/** Where a move puts the panel, or why it cannot. */
function moved(state: PanelState, panel: Panel, move: Move): { readonly to: number } | { readonly reason: string } {
  const at = indexOf(state, panel);
  const [first, last] = reach(state, panel);
  const kind = panel.pinned === true ? 'pinned panel' : 'panel';
  const edge = move === 'left' || move === 'first' ? first : last;
  if (at === edge) return { reason: `This is the ${move === 'left' || move === 'first' ? 'first' : 'last'} ${kind} already.` };
  return { to: move === 'left' ? at - 1 : move === 'right' ? at + 1 : edge };
}

/** Says what a close of several did, and how to have a panel back (ADR-0006's note 2: a command says what it did). */
function closedSaid(notify: Notify, done: { readonly closed: number; readonly pinned: number }): void {
  const kept = done.pinned === 0 ? '' : ` ${done.pinned} pinned ${done.pinned === 1 ? 'panel was' : 'panels were'} kept.`;
  notify('done', `Closed ${done.closed} ${done.closed === 1 ? 'panel' : 'panels'}.${kept} reopen panel brings back the last closed.`);
}

/** Why a close of several takes nothing from this panel's strip; absent when it takes one or more. */
function nothingToClose(state: PanelState, panel: Panel, which: PanelsClosed): string | undefined {
  const named = namedBy(state.panels, which, panel.id);
  if (named.length === 0) return which === 'right' ? 'No panel is to the right of this one.' : 'There is no other panel.';
  return named.every((p) => p.pinned === true) ? `The ${which === 'right' ? 'panels to the right' : which === 'all' ? 'panels' : 'other panels'} are pinned.` : undefined;
}

export const CLOSE: PanelVerb = {
  id: 'panel.close',
  words: 'close panel',
  title: 'Close the panel',
  label: 'Close',
  shortcut: 'mod+shift+X',
  unavailable: () => undefined,
  run: (state, panel) => state.closePanel(panel.id),
};

export const CLOSE_OTHERS: PanelVerb = {
  id: 'panel.closeOthers',
  words: 'close other panels',
  title: 'Close every panel but this one and the pinned',
  label: 'Close others',
  unavailable: (state, panel) => nothingToClose(state, panel, 'others'),
  run: (state, panel, notify) => closedSaid(notify, state.closePanels('others', panel.id)),
};

export const CLOSE_RIGHT: PanelVerb = {
  id: 'panel.closeRight',
  words: 'close panels to the right',
  title: 'Close the panels to the right of this one, but the pinned',
  label: 'Close to the right',
  unavailable: (state, panel) => nothingToClose(state, panel, 'right'),
  run: (state, panel, notify) => closedSaid(notify, state.closePanels('right', panel.id)),
};

export const CLOSE_ALL: PanelVerb = {
  id: 'panel.closeAll',
  words: 'close all panels',
  title: 'Close every panel but the pinned',
  label: 'Close all',
  unavailable: (state, panel) => nothingToClose(state, panel, 'all'),
  run: (state, panel, notify) => closedSaid(notify, state.closePanels('all', panel.id)),
};

export const PIN: PanelVerb = {
  id: 'panel.pin',
  words: 'pin panel',
  title: 'Pin the panel at the start of the strip, where closing several leaves it open',
  label: 'Pin',
  unavailable: (_, panel) => (panel.pinned === true ? 'This panel is pinned already.' : undefined),
  run: (state, panel) => state.pinPanel(panel.id, true),
};

export const UNPIN: PanelVerb = {
  id: 'panel.unpin',
  words: 'unpin panel',
  title: 'Unpin the panel',
  label: 'Unpin',
  unavailable: (_, panel) => (panel.pinned === true ? undefined : 'This panel is not pinned.'),
  run: (state, panel) => state.pinPanel(panel.id, false),
};

export const MOVE: PanelVerb = {
  id: 'panel.move',
  words: 'move panel',
  title: 'Move the panel along the strip',
  label: 'Move',
  args: [{
    name: 'where', required: true, hint: 'left, right, first or last',
    complete: () => MOVES.map((value) => ({ value })),
    check: (value) => (MOVES.some((m) => m === value.toLowerCase()) ? undefined : `one of ${MOVES.join(', ')}`),
  }],
  // With a where given, as a menu's Move left gives one, it says too whether the panel can go there.
  unavailable: (state, panel, args) => {
    const [first, last] = reach(state, panel);
    if (first === last) return `There is no other ${panel.pinned === true ? 'pinned panel' : 'panel'} to move it past.`;
    const move = moveOf(args);
    if (move === undefined) return undefined;
    const to = moved(state, panel, move);
    return 'reason' in to ? to.reason : undefined;
  },
  run: (state, panel, _, args) => {
    const move = moveOf(args);
    if (move === undefined) throw new CommandRefused(`move panel takes where to: ${MOVES.join(', ')}.`);
    const to = moved(state, panel, move);
    if ('reason' in to) throw new CommandRefused(to.reason);
    state.movePanel(panel.id, to.to);
  },
};

/** The verbs over one panel, as the line lists them. */
export const PANEL_VERBS: readonly PanelVerb[] = [CLOSE, CLOSE_OTHERS, CLOSE_RIGHT, CLOSE_ALL, PIN, UNPIN, MOVE];

/** A verb that acts on no one panel: what the line, the list of panels and a tab's menu offer beside the verbs above. */
export const REOPEN: StripVerb = {
  id: 'panel.reopen',
  words: 'reopen panel',
  title: 'Open the panel closed last again, where it stood',
  label: 'Reopen closed panel',
  unavailable: (state) => (state.closed.length === 0 ? 'No panel has been closed.' : undefined),
  run: (state) => { if (state.reopenPanel() === undefined) throw new CommandRefused('No panel has been closed.'); },
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

/** Runs a verb on a panel from a menu or a key. */
export function runVerb(verb: PanelVerb, state: PanelState, panel: Panel, notify: Notify, args: Args = {}): void {
  refusedSaid(verb.words, verb.unavailable(state, panel, args), notify, () => verb.run(state, panel, notify, args));
}

/** Runs a verb over the strip from a menu. */
export function runStripVerb(verb: StripVerb, state: PanelState, notify: Notify): void {
  refusedSaid(verb.words, verb.unavailable(state), notify, () => verb.run(state));
}

/** The line's commands over the panels: each verb on the active panel, `reopen panel`, and moving between panels. */
export function panelCommands(state: PanelState, notify: Notify): Command[] {
  const active = state.panels.find((p) => p.id === state.activePanelId);
  const unavailableOn = (verb: PanelVerb, panel: Panel) => {
    const why = verb.unavailable(state, panel);
    return why === undefined ? {} : { unavailable: why };
  };
  const onActive = (verb: PanelVerb): Command => ({
    id: verb.id,
    words: verb.words,
    title: verb.title,
    group: 'Panels',
    ...(verb.shortcut === undefined ? {} : { shortcut: verb.shortcut }),
    ...(verb.args === undefined ? {} : { args: verb.args }),
    ...(active === undefined ? { unavailable: 'No panel is open.' } : { target: panelLabel(active), ...unavailableOn(verb, active) }),
    run: (args) => {
      // The line's list is made again whenever the panels change, so this is the panel shown as it runs, from a chord too.
      if (active === undefined) throw new CommandRefused('No panel is open.');
      verb.run(state, active, notify, args);
    },
  });
  const noneClosed = REOPEN.unavailable(state);
  return [
    {
      id: 'panel.next',
      words: 'next panel',
      title: 'Show the next panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowRight',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(1),
    },
    {
      id: 'panel.previous',
      words: 'previous panel',
      title: 'Show the previous panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowLeft',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(-1),
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
        // The line runs no command without an argument it requires: one missing here is a defect.
        if (a.panel === undefined) throw new Error('The line ran show panel without its panel.');
        const named = panelTitled(state.panels, a.panel);
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
      run: () => REOPEN.run(state),
    },
  ];
}
