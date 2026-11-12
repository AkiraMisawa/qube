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
   * Which of the open panels of one title this is, from 2, given when it opens and kept while it is open: the strip and
   * the line name it `<title> · <copy>` (ADR-0006 note 7). The first of a title has none.
   */
  readonly copy?: number;
};

/** What a panel shows, which is all a draft of one says. */
export type PanelDraft =
  | { readonly kind: 'run'; readonly title: string; readonly runId: string; readonly viewKey: string | undefined }
  | { readonly kind: 'data'; readonly title: string; readonly view: ViewDefinition }
  /** A fixed application page: an app registered in `src/apps` draws it from its parameters. */
  | { readonly kind: 'app'; readonly title: string; readonly app: string; readonly params: Readonly<Record<string, string>> };

/** A panel as the strip and the line name it: its title, and its number where another open panel has that title. */
export const panelLabel = (panel: Pick<Panel, 'title' | 'copy'>): string => (panel.copy === undefined ? panel.title : `${panel.title} · ${panel.copy}`);

/** A panel that was closed, as `reopenPanel` brings it back: what it showed and where it stood. */
export interface ClosedPanel {
  readonly page: PanelDraft;
  readonly index: number;
  readonly pinned: boolean;
  /** Its number among the panels of its title: it comes back with it, where no open panel has taken it since. */
  readonly copy: number | undefined;
}

/** How many closed panels `reopenPanel` can bring back, the last closed first. */
export const CLOSED_KEPT = 20;

/** Which panels a close of several takes, beside the one it is given (ADR-0006 note 7). Pinned panels stay. */
export type PanelsClosed = 'others' | 'right' | 'all';

/** The panels a close of several names beside this one, the pinned among them: the line, the menus and the store read it alike. */
export function namedBy(panels: readonly Panel[], which: PanelsClosed, panelId: string): readonly Panel[] {
  const at = panels.findIndex((p) => p.id === panelId);
  if (at < 0) throw new Error(`No open panel is ${panelId}.`);
  return panels.filter((p, i) => (which === 'all' || p.id !== panelId) && (which !== 'right' || i > at));
}

/** The open panel of this id; one that is not open is a defect of the caller, never a panel quietly left alone. */
function openOne(panels: readonly Panel[], panelId: string): Panel {
  const panel = panels.find((p) => p.id === panelId);
  if (panel === undefined) throw new Error(`No open panel is ${panelId}.`);
  return panel;
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
  setPanelView(panelId: string, viewKey: string): void;
  closePanel(panelId: string): void;
  /** Closes the panels `which` names beside this one, the pinned left open; answers how many it closed and kept. */
  closePanels(which: PanelsClosed, panelId: string): { readonly closed: number; readonly pinned: number };
  /**
   * Opens the panel closed last where it stood, and shows it; undefined when none was closed. A page
   * open again since it closed is shown in its panel instead, and not opened twice.
   */
  reopenPanel(): string | undefined;
  /** Moves a panel to this place in the strip, kept among the pinned or the unpinned as it is. */
  movePanel(panelId: string, index: number): void;
  /** Pins a panel at the end of the pinned ones, or unpins it to the start of the others. */
  pinPanel(panelId: string, pinned: boolean): void;
  activate(panelId: string): void;
  cycle(step: 1 | -1): void;
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
      set((state) => ({
        panels: state.panels.map((p) => (p.id === panelId && p.kind === 'run' ? { ...p, viewKey } : p)),
      }));
    },

    closePanel(panelId) {
      openOne(get().panels, panelId);
      set((state) => withClosed(state, [panelId]));
    },

    closePanels(which, panelId) {
      const named = namedBy(get().panels, which, panelId);
      const taken = named.filter((p) => p.pinned !== true).map((p) => p.id);
      if (taken.length > 0) set((state) => withClosed(state, taken));
      return { closed: taken.length, pinned: named.length - taken.length };
    },

    reopenPanel() {
      const last = get().closed.at(-1);
      if (last === undefined) return undefined;
      const open = get().panels.find((p) => shows(p, last.page));
      if (open !== undefined) {
        set((state) => ({ closed: state.closed.slice(0, -1) }));
        get().activate(open.id);
        return open.id;
      }
      const id = `panel-${++next}`;
      set((state) => {
        const panel = newPanel(state.panels, last.page, id, last.pinned, last.copy);
        return {
          panels: placed(state.panels, panel, last.index),
          activePanelId: id,
          recent: [id, ...state.recent],
          closed: state.closed.slice(0, -1),
        };
      });
      return id;
    },

    movePanel(panelId, index) {
      set((state) => {
        const panel = openOne(state.panels, panelId);
        return { panels: placed(state.panels.filter((p) => p.id !== panelId), panel, index) };
      });
    },

    pinPanel(panelId, pinned) {
      set((state) => {
        const panel = openOne(state.panels, panelId);
        if ((panel.pinned === true) === pinned) return {};
        const rest = state.panels.filter((p) => p.id !== panelId);
        const { pinned: _, ...unpinned } = panel;
        // Pinned, it goes after the pinned panels; unpinned, before the others: next to where it was in either case.
        return { panels: placed(rest, pinned ? { ...unpinned, pinned: true } : unpinned, pinnedCount(rest)) };
      });
    },

    activate(panelId) {
      if (get().panels.some((p) => p.id === panelId))
        set((state) => ({ activePanelId: panelId, recent: [panelId, ...state.recent.filter((id) => id !== panelId)] }));
    },

    cycle(step) {
      const { panels, activePanelId } = get();
      if (panels.length === 0) return;
      const index = panels.findIndex((p) => p.id === activePanelId);
      const target = panels[(index + step + panels.length) % panels.length];
      if (target !== undefined) get().activate(target.id);
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
 * A new panel of this page: numbered where an open panel has its title (ADR-0006 note 7). A panel reopened asks for the
 * number it had, which it keeps where that is free.
 */
function newPanel(panels: readonly Panel[], page: PanelDraft, id: string, pinned = false, had?: number): Panel {
  const taken = new Set(panels.filter((p) => p.title === page.title).map((p) => p.copy ?? 1));
  let copy = had !== undefined && !taken.has(had) ? had : 1;
  while (taken.has(copy)) copy += 1;
  return { ...page, id, ...(copy === 1 ? {} : { copy }), ...(pinned ? { pinned: true as const } : {}) };
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
function withClosed(state: WorkspaceState, ids: readonly string[]): Partial<WorkspaceState> {
  const gone = new Set(ids);
  const kept: ClosedPanel[] = [];
  // Kept from the right, so the leftmost is reopened first: each then goes back where it stood, the strip to its left
  // already as it was, and a close of several comes back in its order.
  state.panels.forEach((p, index) => {
    if (!gone.has(p.id)) return;
    const { id: _, pinned, copy, ...page } = p;
    kept.unshift({ page, index, pinned: pinned === true, copy });
  });
  const panels = state.panels.filter((p) => !gone.has(p.id));
  const recent = state.recent.filter((id) => !gone.has(id));
  const active = state.activePanelId !== undefined && gone.has(state.activePanelId) ? recent[0] : state.activePanelId;
  return { panels, activePanelId: active, recent, closed: [...state.closed, ...kept].slice(-CLOSED_KEPT) };
}

export const keyOf = (definition: { readonly definitionId: string; readonly version: string }): string =>
  `${definition.definitionId}@${definition.version}`;
