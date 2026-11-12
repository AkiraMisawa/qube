import { createStore } from 'zustand/vanilla';
import type { ContractDefinition, ViewDefinition } from '../contracts';
import { validate, type FieldIssue } from '../forms';

// One store per workspace (ADR-0003 §1): the context it works in and the panels it shows. Server data
// lives in TanStack Query; a panel holds references to it, never copies of rows.

export type Panel = PanelDraft & {
  readonly id: string;
  /** Kept at the start of the strip, and left open by the closes that take several panels (ADR-0006 note 7). */
  readonly pinned?: true;
  /**
   * The name the user gave it (`rename panel`, ADR-0006 note 9), shown in place of its page's title; the page's own
   * title is kept, and the panel has none of its own until it is renamed.
   */
  readonly name?: string;
  /**
   * Which of the open panels of one name this is, from 2, given when it opens or is renamed and kept while it is open:
   * the strip and the line name it `<name> · <copy>` (ADR-0006 note 7). The first of a name has none.
   */
  readonly copy?: number;
};

/** What a panel shows, which is all a draft of one says. */
export type PanelDraft =
  | { readonly kind: 'run'; readonly title: string; readonly runId: string; readonly viewKey: string | undefined }
  | { readonly kind: 'data'; readonly title: string; readonly view: ViewDefinition }
  /** A fixed application page: an app registered in `src/apps` draws it from its parameters. */
  | { readonly kind: 'app'; readonly title: string; readonly app: string; readonly params: Readonly<Record<string, string>> };

/** What a panel is called before its number: the name the user gave it, else its page's title. */
export const panelName = (panel: Pick<Panel, 'title' | 'name'>): string => panel.name ?? panel.title;

/** A panel as the strip and the line name it: its name, and its number where another open panel has that name. */
export const panelLabel = (panel: Pick<Panel, 'title' | 'name' | 'copy'>): string => (panel.copy === undefined ? panelName(panel) : `${panelName(panel)} · ${panel.copy}`);

/** The number the strip gives a panel, at the end of its label: a name the user gives cannot end so. */
const NUMBERED = /·\s*\d+$/;

/** The longest name a panel takes: a tab shows about half of it, and every request to the AI carries each panel's name. */
export const NAME_LONGEST = 60;

/**
 * What a typed name gives a panel of this title (ADR-0006 note 9), the one reading of a name the store, the line and a
 * browser tab's kept panels share: its spaces trimmed and made single; no name of its own where it is empty or is the
 * title, in any case, which gives the title back; or why it cannot be a name.
 */
export function readName(title: string, typed: string): { readonly name: string | undefined } | { readonly refused: string } {
  const spaced = (text: string) => text.trim().replace(/\s+/g, ' ');
  const name = spaced(typed);
  if (name === '' || name.toLowerCase() === spaced(title).toLowerCase()) return { name: undefined };
  if (NUMBERED.test(name)) return { refused: 'A name cannot end in · and a number: the strip numbers panels of one name itself.' };
  // Counted as a reader counts them: an emoji is one character, not the two halves JavaScript stores it as.
  const characters = [...name].length;
  if (characters > NAME_LONGEST) return { refused: `A name is at most ${NAME_LONGEST} characters; this one is ${characters}.` };
  return { name };
}

/** A panel that was closed, as `reopenPanel` brings it back: what it showed and where it stood. */
export interface ClosedPanel {
  readonly page: PanelDraft;
  readonly index: number;
  readonly pinned: boolean;
  /** The name the user gave it, which it comes back with (ADR-0006 note 9). */
  readonly name?: string;
  /** Its number among the panels of its name: it comes back with it, where no open panel has taken it since. */
  readonly copy: number | undefined;
  /** The area it stood in, where the panels stood in areas (ADR-0018 §4): `reopen panel` puts it back there. */
  readonly area?: string;
}

/**
 * Which area a panel stands in, where the panels stand in areas (ADR-0018 §4): closing the active panel then shows the
 * panel its area showed before, and a panel closed is kept with its area.
 */
export type AreaOfPanel = (panelId: string) => string | undefined;

/** What a browser tab keeps of its panels across a reload (ADR-0018 §7), as the store holds them. */
export interface KeptPanels {
  readonly panels: readonly Panel[];
  readonly activePanelId: string | undefined;
  readonly recent: readonly string[];
  readonly closed: readonly ClosedPanel[];
}

/** How many closed panels `reopenPanel` can bring back, the last closed first. */
export const CLOSED_KEPT = 20;

/** A panel's id as this store gives it, `panel-<n>`, with its number. */
export const PANEL_ID = /^panel-(\d+)$/;

/** Which panels a close of several takes, beside the one it is given (ADR-0006 note 7). Pinned panels stay. */
export type PanelsClosed = 'others' | 'right' | 'all';

/** The open panel of this id; one that is not open is a defect of the caller, never a panel quietly left alone. */
function requireOpen(panels: readonly Panel[], panelId: string): Panel {
  const panel = panels.find((p) => p.id === panelId);
  if (panel === undefined) throw new Error(`No open panel is ${panelId}.`);
  return panel;
}

/** The panels a close of several names beside this one, the pinned among them: the line, the menus and the store read it alike. */
export function namedBy(panels: readonly Panel[], which: PanelsClosed, panelId: string): readonly Panel[] {
  const at = panels.indexOf(requireOpen(panels, panelId));
  return panels.filter((p, i) => (which === 'all' || p.id !== panelId) && (which !== 'right' || i > at));
}

/** How many panels are pinned: they stand first, so this is where the others begin. */
export const pinnedCount = (panels: readonly Panel[]): number => panels.filter((p) => p.pinned === true).length;

export interface ContextState {
  /** What the user asked for, possibly invalid or incomplete. */
  readonly requested: Readonly<Record<string, unknown>>;
  /** The last requested context that met its contract. Submissions use this, never `requested`. */
  readonly prepared: Readonly<Record<string, unknown>> | undefined;
  readonly issues: readonly FieldIssue[];
}

export interface WorkspaceState {
  readonly context: ContextState;
  /** In the order the strip shows them: the pinned first. */
  readonly panels: readonly Panel[];
  readonly activePanelId: string | undefined;
  /** The open panels by when they were last shown, the latest first: closing the active one shows the next of these. */
  readonly recent: readonly string[];
  /** The panels closed while this page was loaded, the last closed last, at most `CLOSED_KEPT`. */
  readonly closed: readonly ClosedPanel[];
  /** The workflow whose input form is open, by `definitionId@version`. */
  readonly runDialog: string | undefined;
  /** The app dialog that is open, by the name its app registered. */
  readonly appDialog: string | undefined;
  requestContext(contract: ContractDefinition, changes: Readonly<Record<string, unknown>>): void;
  /**
   * Opens a panel next to the active one and shows it. A Run that is open already is shown in its panel, in the view
   * asked for, and not opened twice.
   */
  openPanel(panel: PanelDraft): string;
  /** Shows a Run panel's result in this view. Throws for a panel that is not open. */
  setPanelView(panelId: string, viewKey: string): void;
  /** Closes a panel; where the panels stand in areas, `areaOf` says which, and the active one is followed in its area. */
  closePanel(panelId: string, areaOf?: AreaOfPanel): void;
  /**
   * Closes the panels `which` names beside this one among `strip` (every panel, or its area's), the pinned left open;
   * answers how many it closed and kept.
   */
  closePanels(which: PanelsClosed, panelId: string, strip?: readonly string[], areaOf?: AreaOfPanel): { readonly closed: number; readonly pinned: number };
  /**
   * Opens the panel closed last where it stood, and shows it; answers that panel, or undefined when none was closed. A
   * page open again since it closed is shown in its panel instead, and not opened twice.
   */
  reopenPanel(): Panel | undefined;
  /** Moves a panel to this place in the strip, kept among the pinned or the unpinned as it is. */
  movePanel(panelId: string, index: number): void;
  /**
   * Puts the open panels in this order, which names each of them once: the strip's order while areas are shown, which
   * is their reading order with the pinned first (ADR-0018 §5).
   */
  setOrder(ids: readonly string[]): void;
  /** Takes up what a browser tab kept of its panels (ADR-0018 §7), in place of none. Throws where a panel is open. */
  restore(kept: KeptPanels): void;
  /** Pins a panel at the end of the pinned ones, or unpins it to the start of the others. */
  pinPanel(panelId: string, pinned: boolean): void;
  /**
   * Names a panel as `readName` reads what was typed (ADR-0006 note 9): with an empty name, or its page's title, it has
   * that title again. It is numbered anew among the panels of its new name. Throws for a name `readName` refuses, and
   * for a panel that is not open.
   */
  renamePanel(panelId: string, typed: string): void;
  /** Shows this panel. Throws for a panel that is not open. */
  activate(panelId: string): void;
  openRunDialog(workflowKey: string | undefined): void;
  openAppDialog(dialog: string | undefined): void;
  /** Activates the app panel with these parameters if one is open, else opens it. */
  showApp(app: string, title: string, params: Readonly<Record<string, string>>): string;
  /**
   * Opens the app panel with these parameters behind the active one, or finds it open, without activating it; it is
   * active only when no panel was (ADR-0007 §7: what the AI opens never takes the user's place). Answers the panel, so
   * what names it can name it as the strip does.
   */
  openPanelBehind(app: string, title: string, params: Readonly<Record<string, string>>): Panel;
}

export type WorkspaceStore = ReturnType<typeof createWorkspaceStore>;

export function createWorkspaceStore(initialContext: Readonly<Record<string, unknown>>, contract: ContractDefinition) {
  let next = 0;
  const initialIssues = validate(contract, initialContext);
  return createStore<WorkspaceState>()((set, get) => ({
    context: { requested: initialContext, prepared: initialIssues.length === 0 ? initialContext : undefined, issues: initialIssues },
    panels: [],
    activePanelId: undefined,
    recent: [],
    closed: [],
    runDialog: undefined,
    appDialog: undefined,

    requestContext(contextContract, changes) {
      const requested = { ...get().context.requested, ...changes };
      const issues = validate(contextContract, requested);
      set({ context: { requested, prepared: issues.length === 0 ? requested : get().context.prepared, issues } });
    },

    openPanel(draft) {
      if (draft.kind === 'run') {
        const open = get().panels.find((p) => shows(p, draft));
        if (open !== undefined) {
          if (draft.viewKey !== undefined) get().setPanelView(open.id, draft.viewKey);
          get().activate(open.id);
          return open.id;
        }
      }
      const id = `panel-${++next}`;
      set((state) => ({ panels: placed(state.panels, newPanel(state.panels, draft, id), besideActive(state)), activePanelId: id, recent: [id, ...state.recent] }));
      return id;
    },

    setPanelView(panelId, viewKey) {
      requireOpen(get().panels, panelId);
      set((state) => ({
        panels: state.panels.map((p) => (p.id === panelId && p.kind === 'run' ? { ...p, viewKey } : p)),
      }));
    },

    closePanel(panelId, areaOf) {
      requireOpen(get().panels, panelId);
      set((state) => withClosed(state, [panelId], areaOf));
    },

    closePanels(which, panelId, strip, areaOf) {
      const panels = get().panels;
      const within = strip === undefined ? panels : strip.map((id) => requireOpen(panels, id));
      const named = namedBy(within, which, panelId);
      const taken = named.filter((p) => p.pinned !== true).map((p) => p.id);
      if (taken.length > 0) set((state) => withClosed(state, taken, areaOf));
      return { closed: taken.length, pinned: named.length - taken.length };
    },

    reopenPanel() {
      const last = get().closed.at(-1);
      if (last === undefined) return undefined;
      const open = get().panels.find((p) => shows(p, last.page));
      if (open !== undefined) {
        set((state) => ({ closed: state.closed.slice(0, -1) }));
        get().activate(open.id);
        return open;
      }
      const id = `panel-${++next}`;
      const panel = newPanel(get().panels, last.page, id, { pinned: last.pinned, ...(last.name === undefined ? {} : { name: last.name }), copy: last.copy });
      set((state) => ({
        panels: placed(state.panels, panel, last.index),
        activePanelId: id,
        recent: [id, ...state.recent],
        closed: state.closed.slice(0, -1),
      }));
      return panel;
    },

    movePanel(panelId, index) {
      set((state) => {
        const panel = requireOpen(state.panels, panelId);
        return { panels: placed(state.panels.filter((p) => p.id !== panelId), panel, index) };
      });
    },

    setOrder(ids) {
      const panels = get().panels;
      if (ids.length !== panels.length || new Set(ids).size !== ids.length) throw new Error(`An order of ${ids.length} names the ${panels.length} open panels.`);
      const ordered = ids.map((id) => requireOpen(panels, id));
      if (ordered.every((p, i) => p === panels[i])) return;
      set({ panels: ordered });
    },

    restore(kept) {
      if (get().panels.length > 0) throw new Error('Panels kept by the browser tab are taken up only before any is open.');
      for (const p of kept.panels) {
        const n = PANEL_ID.exec(p.id);
        if (n === null) throw new Error(`A kept panel's id ${p.id} is not one this store gives.`);
        next = Math.max(next, Number(n[1]));
      }
      const open = new Set(kept.panels.map((p) => p.id));
      const active = kept.activePanelId !== undefined && open.has(kept.activePanelId) ? kept.activePanelId : kept.panels[0]?.id;
      set({
        panels: kept.panels,
        activePanelId: active,
        recent: kept.recent.filter((id) => open.has(id)),
        closed: kept.closed.slice(-CLOSED_KEPT),
      });
    },

    pinPanel(panelId, pinned) {
      set((state) => {
        const panel = requireOpen(state.panels, panelId);
        if ((panel.pinned === true) === pinned) return {};
        const rest = state.panels.filter((p) => p.id !== panelId);
        const { pinned: _, ...unpinned } = panel;
        // Pinned, it goes after the pinned panels; unpinned, before the others: next to where it was in either case.
        return { panels: placed(rest, pinned ? { ...unpinned, pinned: true } : unpinned, pinnedCount(rest)) };
      });
    },

    renamePanel(panelId, typed) {
      const panel = requireOpen(get().panels, panelId);
      const read = readName(panel.title, typed);
      if ('refused' in read) throw new Error(read.refused);
      if (read.name === panel.name) return;
      const { name: _, copy: __, ...titled } = panel;
      const renamed: Panel = { ...titled, ...(read.name === undefined ? {} : { name: read.name }) };
      set((state) => {
        const copy = freeCopy(state.panels.filter((p) => p.id !== panelId), panelName(renamed));
        return { panels: state.panels.map((p) => (p.id === panelId ? { ...renamed, ...(copy === 1 ? {} : { copy }) } : p)) };
      });
    },

    activate(panelId) {
      requireOpen(get().panels, panelId);
      set((state) => ({ activePanelId: panelId, recent: [panelId, ...state.recent.filter((id) => id !== panelId)] }));
    },

    openRunDialog(workflowKey) {
      set({ runDialog: workflowKey });
    },

    openAppDialog(dialog) {
      set({ appDialog: dialog });
    },

    showApp(app, title, params) {
      const open = get().panels.find((p) => shows(p, { kind: 'app', title, app, params }));
      if (open !== undefined) {
        get().activate(open.id);
        return open.id;
      }
      return get().openPanel({ kind: 'app', title, app, params });
    },

    openPanelBehind(app, title, params) {
      const open = get().panels.find((p) => shows(p, { kind: 'app', title, app, params }));
      if (open !== undefined) return open;
      const id = `panel-${++next}`;
      const panel = newPanel(get().panels, { kind: 'app', title, app, params }, id);
      set((state) => ({
        panels: placed(state.panels, panel, besideActive(state)),
        activePanelId: state.activePanelId ?? id,
        recent: state.activePanelId === undefined ? [id, ...state.recent] : [...state.recent, id],
      }));
      return panel;
    },
  }));
}

/**
 * The number a panel of this name takes among these: the one it asks for where that is free, else the lowest free, 1
 * being none. Names are told apart regardless of case, as `show panel` reads them.
 */
function freeCopy(panels: readonly Panel[], name: string, had?: number): number {
  const taken = new Set(panels.filter((p) => panelName(p).toLowerCase() === name.toLowerCase()).map((p) => p.copy ?? 1));
  let copy = had !== undefined && !taken.has(had) ? had : 1;
  while (taken.has(copy)) copy += 1;
  return copy;
}

/**
 * A new panel of this page: numbered where an open panel has its name (ADR-0006 note 7). A panel reopened comes back
 * with the name the user gave it, and asks for the number it had, which it keeps where that is free.
 */
function newPanel(panels: readonly Panel[], page: PanelDraft, id: string, was: { readonly pinned?: boolean; readonly name?: string; readonly copy?: number | undefined } = {}): Panel {
  const copy = freeCopy(panels, was.name ?? page.title, was.copy);
  return { ...page, id, ...(was.name === undefined ? {} : { name: was.name }), ...(copy === 1 ? {} : { copy }), ...(was.pinned === true ? { pinned: true as const } : {}) };
}

/** Whether an open panel shows this page: a Run by its id, an application by its parameters. A data view is never the same. */
function shows(panel: Panel, page: PanelDraft): boolean {
  if (page.kind === 'run') return panel.kind === 'run' && panel.runId === page.runId;
  if (page.kind === 'app') return panel.kind === 'app' && panel.app === page.app && JSON.stringify(panel.params) === JSON.stringify(page.params);
  return false;
}

/** Where a new panel opens: after the active one, or first after the pinned ones where the active one is pinned. */
function besideActive(state: Pick<WorkspaceState, 'panels' | 'activePanelId'>): number {
  const at = state.panels.findIndex((p) => p.id === state.activePanelId);
  return at < 0 ? state.panels.length : at + 1;
}

/** The panels with this one put at `index`, which is kept among the pinned or the unpinned as the panel is. */
function placed(panels: readonly Panel[], panel: Panel, index: number): readonly Panel[] {
  const edge = pinnedCount(panels);
  const at = panel.pinned === true ? Math.min(Math.max(index, 0), edge) : Math.min(Math.max(index, edge), panels.length);
  return [...panels.slice(0, at), panel, ...panels.slice(at)];
}

/**
 * The state with these panels closed: each kept to be reopened; the active one, where it goes, followed
 * by the panel shown before it (ADR-0006 note 7).
 */
function withClosed(state: WorkspaceState, ids: readonly string[], areaOf?: AreaOfPanel): Partial<WorkspaceState> {
  const gone = new Set(ids);
  const kept: ClosedPanel[] = [];
  // Kept from the right, so the leftmost is reopened first: each then goes back where it stood, the strip to its left
  // already as it was, and a close of several comes back in its order.
  state.panels.forEach((p, index) => {
    if (!gone.has(p.id)) return;
    const { id, pinned, name, copy, ...page } = p;
    const area = areaOf?.(id);
    kept.unshift({ page, index, pinned: pinned === true, ...(name === undefined ? {} : { name }), copy, ...(area === undefined ? {} : { area }) });
  });
  const panels = state.panels.filter((p) => !gone.has(p.id));
  const recent = state.recent.filter((id) => !gone.has(id));
  // The active panel gone, the panel shown before it follows: in its own area where the panels stand in areas, so the
  // area shows what it showed before and keeps the line's attention (ADR-0018 §4), else the one shown last anywhere.
  const was = state.activePanelId;
  const area = was !== undefined && gone.has(was) ? areaOf?.(was) : undefined;
  const followed = area === undefined ? undefined : recent.find((id) => areaOf?.(id) === area);
  const active = was !== undefined && gone.has(was) ? followed ?? recent[0] : was;
  return { panels, activePanelId: active, recent, closed: [...state.closed, ...kept].slice(-CLOSED_KEPT) };
}

export const keyOf = (definition: { readonly definitionId: string; readonly version: string }): string =>
  `${definition.definitionId}@${definition.version}`;
