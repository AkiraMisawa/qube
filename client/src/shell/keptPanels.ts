import { useLayoutEffect } from 'react';
import { createStore } from 'zustand/vanilla';
import { useStore } from 'zustand';
import type { SerializedDockview } from 'dockview-react';
import type { Notify } from '../platform/notices';
import { useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import type { KeptPanels, WorkspaceStore } from '../platform/workspace/workspaceStore';

// What a browser tab keeps of its panels across a reload (ADR-0018 §7): the panels, what each shows, the active one,
// the ones shown last and closed, and the arrangement. It is the tab's session storage, under the user's id, so two
// tabs keep their own and a new tab starts with none. A value this version cannot read is not applied, and the shell
// says so.

interface KeptArrangement {
  /** The last arrangement dockview drew while it fitted the panels' space (ADR-0018 §5): the strip waits with it. */
  readonly layout: SerializedDockview | undefined;
  keep(layout: SerializedDockview | undefined): void;
}

export const keptArrangement = createStore<KeptArrangement>()((set) => ({
  layout: undefined,
  keep: (layout) => set({ layout }),
}));

/** The kept arrangement, for what chooses between the strip and the areas. */
export const useKeptLayout = (): SerializedDockview | undefined => useStore(keptArrangement, (s) => s.layout);

/** The version of what is kept: a value of another one is refused, never read as this one. */
const VERSION = 1;

/** The session storage key of a user's panels. */
export const keptKey = (userId: string): string => `qube.panels.v${VERSION}:${userId}`;

/** What a browser tab keeps. */
export interface KeptTab {
  readonly version: typeof VERSION;
  readonly panels: KeptPanels;
  readonly layout: SerializedDockview | undefined;
}

class Unreadable extends Error {}

const record = (value: unknown, path: string): Readonly<Record<string, unknown>> => {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Unreadable(`${path} is not an object`);
  return value as Readonly<Record<string, unknown>>;
};
const list = (value: unknown, path: string): readonly unknown[] => {
  if (!Array.isArray(value)) throw new Unreadable(`${path} is not a list`);
  return value;
};
const text = (value: unknown, path: string): string => {
  if (typeof value !== 'string') throw new Unreadable(`${path} is not text`);
  return value;
};

/** Checks what a panel shows as the store holds it; the page itself is read anew from the server when it is drawn. */
function checkPage(value: unknown, path: string): void {
  const page = record(value, path);
  text(page.title, `${path}.title`);
  if (page.kind === 'run') text(page.runId, `${path}.runId`);
  else if (page.kind === 'app') { text(page.app, `${path}.app`); record(page.params, `${path}.params`); }
  else if (page.kind === 'data') record(page.view, `${path}.view`);
  else throw new Unreadable(`${path}.kind is not a kind of panel`);
}

/**
 * Reads what a browser tab kept: nothing where it kept nothing, or why it cannot be read. Never a part of it: a value
 * read in part would bring back panels that are not the ones the user left.
 */
export function readKept(stored: string | null): { readonly kept: KeptTab | undefined } | { readonly refused: string } {
  if (stored === null) return { kept: undefined };
  try {
    const value = record(JSON.parse(stored) as unknown, 'the value');
    if (value.version !== VERSION) throw new Unreadable(`it is of version ${String(value.version)}, and this workbench reads ${VERSION}`);
    const kept = record(value.panels, 'panels');
    list(kept.panels, 'panels.panels').forEach((p, i) => {
      checkPage(p, `panels.panels[${i}]`);
      if (!/^panel-\d+$/.test(text(record(p, '').id, `panels.panels[${i}].id`))) throw new Unreadable(`panels.panels[${i}].id is not a panel's id`);
    });
    if (kept.activePanelId !== undefined) text(kept.activePanelId, 'panels.activePanelId');
    list(kept.recent, 'panels.recent').forEach((id, i) => text(id, `panels.recent[${i}]`));
    list(kept.closed, 'panels.closed').forEach((c, i) => {
      const closed = record(c, `panels.closed[${i}]`);
      checkPage(closed.page, `panels.closed[${i}].page`);
      if (typeof closed.index !== 'number' || typeof closed.pinned !== 'boolean') throw new Unreadable(`panels.closed[${i}] has no place`);
    });
    if (value.layout !== undefined) record(record(value.layout, 'layout').grid, 'layout.grid');
    return { kept: value as unknown as KeptTab };
  } catch (error) {
    if (error instanceof SyntaxError || error instanceof Unreadable) return { refused: error.message };
    throw error;
  }
}

/** The tab's session storage, or why there is none to keep in (a browser can refuse it). */
function sessionStorageOf(): Storage | { readonly reason: string } {
  try {
    return window.sessionStorage;
  } catch (error) {
    return { reason: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * Takes up what the tab kept, as the page starts, and keeps the panels and the arrangement as they change. `userId` is
 * who the page is for; undefined where that could not be read, and then nothing is kept, which the shell says.
 */
export function keepPanels(store: WorkspaceStore, notify: Notify, userId: string | undefined, why?: string): () => void {
  if (userId === undefined) {
    notify('refused', `Your panels are not kept across a reload of this tab: who you are could not be read${why === undefined ? '' : ` (${why})`}.`, { stays: true });
    return () => {};
  }
  const storage = sessionStorageOf();
  if ('reason' in storage) {
    notify('refused', `Your panels are not kept across a reload of this tab: the browser keeps nothing for it (${storage.reason}).`, { stays: true });
    return () => {};
  }
  const key = keptKey(userId);
  const read = readKept(storage.getItem(key));
  if ('refused' in read) {
    notify('refused', `The panels this tab kept could not be read, so it starts with none: ${read.refused}.`, { stays: true });
  } else if (read.kept !== undefined && store.getState().panels.length === 0) {
    keptArrangement.getState().keep(read.kept.layout);
    store.getState().restore(read.kept.panels);
  }
  const write = () => {
    const { panels, activePanelId, recent, closed } = store.getState();
    const tab: KeptTab = { version: VERSION, panels: { panels, activePanelId, recent, closed }, layout: keptArrangement.getState().layout };
    storage.setItem(key, JSON.stringify(tab));
  };
  write();
  const stops = [
    store.subscribe((now, before) => {
      if (now.panels !== before.panels || now.activePanelId !== before.activePanelId || now.recent !== before.recent || now.closed !== before.closed) write();
    }),
    keptArrangement.subscribe((now, before) => { if (now.layout !== before.layout) write(); }),
  ];
  return () => stops.forEach((stop) => stop());
}

/** Keeps the panels of the page's workspace by the browser tab, from before the panels are first drawn. */
export function KeepPanels({ userId, why }: { readonly userId: string | undefined; readonly why?: string }) {
  const { store, notify } = useWorkspaceContext();
  useLayoutEffect(() => keepPanels(store, notify, userId, why), [store, notify, userId, why]);
  return null;
}
