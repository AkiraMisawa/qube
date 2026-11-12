import { createStore } from 'zustand/vanilla';
import { useStore } from 'zustand';
import { Orientation, type SerializedDockview, type SerializedGridObject } from 'dockview-react';
import type { Context } from '@ag-ui/core';
import { CommandRefused, given, type Command } from '../platform/commands/commands';
import type { Notify } from '../platform/notices';
import type { Api } from '../platform/api';
import { panelLabel, type Panel, type PanelDraft, type WorkspaceState, type WorkspaceStore } from '../platform/workspace/workspaceStore';
import { areaHolding, currentArrangement, FLOAT_PLACES, floatBox, nextAreaId, SIDES, type Arrangement, type FloatPlace, type Side } from './arrangement';
import { keptArrangement, type FloatedFrom } from './keptPanels';
import { AREA_MIN, AREAS_FROM, oneArea, openPanels, panelState, reconciled, type Size } from './layout';

// The AI's proposals of an arrangement (ADR-0018 §8, CONFIRMED by the owner: the AI proposes, the user applies). The
// kernel's `layout.propose` checks a proposal's shape and answers it; here it is numbered for the page's life, drawn as
// a plan on its card, and applied by `arrange <n>` or the card's Apply, which are one verb. `arrange back` puts back
// what the last one replaced. Like the other verbs over the panels these are the client's: never logged, never the AI's.

/** An area as a proposal says it: placed beside an area of the window listed before it, or floating at a place. */
export interface ProposedArea {
  readonly area: string;
  readonly beside?: string;
  readonly side?: Side;
  readonly share?: number;
  readonly float?: FloatPlace;
}

/** What one place of an area holds: an open panel by its id and title, a Run, or an application page. */
export interface ProposedPlace {
  readonly area: string;
  readonly panel?: string;
  readonly title?: string;
  readonly run?: string;
  readonly app?: string;
  readonly workspace?: string;
  readonly shown?: boolean;
}

/** A proposal as the server answered it, numbered on its card. */
export interface Proposal {
  readonly n: number;
  readonly areas: readonly ProposedArea[];
  readonly places: readonly ProposedPlace[];
  /** The least panels' space it fits, as the server reckoned it from each area's share. */
  readonly needs: Size;
}

type Fields = Readonly<Record<string, unknown>>;

const record = (value: unknown, what: string): Fields => {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Error(`layout.propose answered ${what} that is not a record.`);
  return value as Fields;
};
const optional = (r: Fields, name: string): string | undefined => {
  const value = r[name];
  if (value === undefined) return undefined;
  if (typeof value !== 'string') throw new Error(`layout.propose answered a ${name} that is not text.`);
  return value;
};
const text = (r: Fields, name: string): string => {
  const value = optional(r, name);
  if (value === undefined) throw new Error(`layout.propose answered no ${name}.`);
  return value;
};
const whole = (r: Fields, name: string): number => {
  const value = r[name];
  if (typeof value !== 'number') throw new Error(`layout.propose answered no ${name}.`);
  return value;
};
const oneOf = <T extends string>(value: string | undefined, choices: readonly T[], name: string): T | undefined => {
  if (value === undefined) return undefined;
  if (!(choices as readonly string[]).includes(value)) throw new Error(`layout.propose answered a ${name} it does not take: ${value}.`);
  return value as T;
};

/** Reads a proposal as `layout.propose` answers it: its contract is the server's, so another shape is a defect. */
export function readProposal(outputs: Readonly<Record<string, unknown>>, n: number): Proposal {
  const answer = record(outputs.answer, 'an answer');
  const list = (name: string): readonly Fields[] => {
    const value = answer[name];
    if (!Array.isArray(value)) throw new Error(`layout.propose answered no ${name}.`);
    return value.map((v) => record(v, `an item of ${name}`));
  };
  const areas = list('areas').map((a): ProposedArea => {
    const share = a.share === undefined ? undefined : whole(a, 'share');
    const side = oneOf(optional(a, 'side'), SIDES, 'side');
    const float = oneOf(optional(a, 'float'), FLOAT_PLACES, 'float');
    const beside = optional(a, 'beside');
    return {
      area: text(a, 'area'),
      ...(beside === undefined ? {} : { beside }), ...(side === undefined ? {} : { side }),
      ...(share === undefined ? {} : { share }), ...(float === undefined ? {} : { float }),
    };
  });
  const places = list('places').map((p): ProposedPlace => {
    const fields = Object.fromEntries((['panel', 'title', 'run', 'app', 'workspace'] as const).flatMap((name) => {
      const value = optional(p, name);
      return value === undefined ? [] : [[name, value]];
    }));
    const named = ['panel', 'run', 'app'].filter((name) => name in fields);
    if (named.length !== 1) throw new Error(`layout.propose answered a place naming ${named.length === 0 ? 'no panel, Run or page' : named.join(' and ')}.`);
    if ('panel' in fields !== 'title' in fields) throw new Error('layout.propose answered a panel without its title, or a title without a panel.');
    return { area: text(p, 'area'), ...fields, ...(p.shown === true ? { shown: true } : {}) };
  });
  return { n, areas, places, needs: { width: whole(answer, 'width'), height: whole(answer, 'height') } };
}

// The proposals of this page, by the tool call that answered each, numbered as they arrive (ADR-0018 §8).
const proposals = createStore<{ readonly byCall: Readonly<Record<string, Proposal>>; readonly count: number }>()(() => ({ byCall: {}, count: 0 }));

/** Numbers a proposal as its answer arrives; the same call answered again keeps its number. */
export function takeProposal(call: string, outputs: Readonly<Record<string, unknown>>): Proposal {
  const had = proposals.getState().byCall[call];
  if (had !== undefined) return had;
  const n = proposals.getState().count + 1;
  const proposal = readProposal(outputs, n);
  proposals.setState((s) => ({ byCall: { ...s.byCall, [call]: proposal }, count: n }));
  return proposal;
}

/** The proposal a card's tool call answered. */
export function useProposal(call: string): Proposal | undefined {
  return useStore(proposals, (s) => s.byCall[call]);
}

/** The proposals of this page, in their numbers' order. */
export function proposalsMade(): readonly Proposal[] {
  return Object.values(proposals.getState().byCall).sort((a, b) => a.n - b.n);
}

/** An application page a proposal may open where it is not open (ADR-0018 §8), as its app registers it. */
export interface PageOpener {
  /** The app's name, as a proposal's `app` names it. */
  readonly app: string;
  /** What the page is, as the agent reads it in the context. */
  readonly title: string;
  /** It shows a workspace, named by its id; otherwise it takes none. */
  readonly workspace: boolean;
  /** The page's draft, its title read from the server where it is a workspace's. */
  open(workspace: string | undefined): Promise<PanelDraft>;
}

const openers = new Map<string, PageOpener>();

/** Registered when an app's module loads, as its AI effects are. */
export function registerPageOpener(opener: PageOpener): void {
  openers.set(opener.app, opener);
}

/** How a Run is opened in its panel: its record read for its workflow's title. */
export type RunOpener = (runId: string) => Promise<PanelDraft>;

// A part of the window: an area, or a row or column of parts with each one's share of it, as the server reckons the
// room a proposal needs (`LayoutPropose.Needs`).
type Part = { readonly area: string } | Row;
interface Row { readonly row: boolean; readonly parts: readonly { readonly part: Part; readonly share: number }[] }
type Mutable = { area: string; parent?: Branch } | Branch;
interface Branch { row: boolean; parts: { part: Mutable; share: number }[]; parent?: Branch }

const isArea = (part: Part): part is { readonly area: string } => 'area' in part;
const isLeaf = (part: Mutable): part is { area: string; parent?: Branch } => 'area' in part;

/** The window's areas of a proposal as a tree of rows and columns, each area placed beside one listed before it. */
export function partsOf(areas: readonly ProposedArea[]): Part | undefined {
  let root: Mutable | undefined;
  const leaves = new Map<string, { area: string; parent?: Branch }>();
  for (const a of areas.filter((x) => x.float === undefined)) {
    const leaf: { area: string; parent?: Branch } = { area: a.area };
    leaves.set(a.area, leaf);
    if (root === undefined) { root = leaf; continue; }
    const beside = a.beside === undefined ? undefined : leaves.get(a.beside);
    if (beside === undefined || a.side === undefined || a.share === undefined)
      throw new Error(`The proposal's area ${a.area} is not placed beside an area listed before it; the server checks that.`);
    const row = a.side === 'right' || a.side === 'left';
    const before = a.side === 'left' || a.side === 'up';
    const share = a.share / 100;
    const parent = beside.parent;
    if (parent !== undefined && parent.row === row) {
      const at = parent.parts.findIndex((p) => p.part === beside);
      const whole = parent.parts[at]!.share;
      parent.parts[at] = { part: beside, share: whole * (1 - share) };
      parent.parts.splice(before ? at : at + 1, 0, { part: leaf, share: whole * share });
      leaf.parent = parent;
      continue;
    }
    const split: Branch = { row, parts: before ? [{ part: leaf, share }, { part: beside, share: 1 - share }] : [{ part: beside, share: 1 - share }, { part: leaf, share }] };
    if (parent !== undefined) {
      split.parent = parent;
      const at = parent.parts.findIndex((p) => p.part === beside);
      parent.parts[at] = { part: split, share: parent.parts[at]!.share };
    } else {
      root = split;
    }
    beside.parent = split;
    leaf.parent = split;
  }
  const frozen = (part: Mutable): Part => (isLeaf(part) ? { area: part.area } : { row: part.row, parts: part.parts.map((p) => ({ part: frozen(p.part), share: p.share })) });
  return root === undefined ? undefined : frozen(root);
}

/** An area of a proposal on its plan: where it stands in the panels' space, as fractions of it. */
export interface PlannedArea {
  readonly area: string;
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
  readonly floating: boolean;
}

/** Where each area of a proposal stands, as fractions of the panels' space: what its card draws. */
export function planOf(proposal: Pick<Proposal, 'areas'>): readonly PlannedArea[] {
  const planned: PlannedArea[] = [];
  const walk = (part: Part, left: number, top: number, width: number, height: number) => {
    if (isArea(part)) { planned.push({ area: part.area, left, top, width, height, floating: false }); return; }
    let at = 0;
    for (const p of part.parts) {
      if (part.row) walk(p.part, left + at * width, top, p.share * width, height);
      else walk(p.part, left, top + at * height, width, p.share * height);
      at += p.share;
    }
  };
  const root = partsOf(proposal.areas);
  if (root !== undefined) walk(root, 0, 0, 1, 1);
  // A floating area at its place in a square space: the plan scales it with the space it is drawn in.
  const unit = { width: 1000, height: 1000 };
  for (const a of proposal.areas) {
    if (a.float === undefined) continue;
    const box = floatBox(a.float, unit);
    planned.push({ area: a.area, left: box.left / unit.width, top: box.top / unit.height, width: box.width / unit.width, height: box.height / unit.height, floating: true });
  }
  return planned;
}

type GroupView = { readonly id: string; readonly views: readonly string[]; readonly activeView?: string };

/**
 * A proposal as dockview draws it in this room: each area a new area, holding its places' panels in order and showing
 * the one marked shown; the open panels it does not place join its first area of the window, after its own. `ids` is
 * the panel of each place, in the proposal's order.
 */
export function proposalLayout(proposal: Pick<Proposal, 'areas' | 'places'>, ids: readonly string[], titles: Readonly<Record<string, string>>,
  others: readonly string[], room: Size, active: string | undefined, newAreaId: () => string = nextAreaId): SerializedDockview {
  const root = partsOf(proposal.areas);
  if (root === undefined) throw new Error('A proposal has an area of the window; the server checks that.');
  const first = firstWindowArea({ ...proposal, n: 0 });
  const groups = new Map<string, GroupView>(proposal.areas.map((a) => {
    const placed = proposal.places.flatMap((p, i) => {
      if (p.area !== a.area) return [];
      const id = ids[i];
      if (id === undefined) throw new Error(`Place ${i} of the proposal has no panel.`);
      return [{ id, shown: p.shown === true }];
    });
    const views = [...placed.map((p) => p.id), ...(a.area === first ? others : [])];
    const shown = placed.find((p) => p.shown)?.id;
    return [a.area, { id: newAreaId(), views, ...(shown === undefined ? {} : { activeView: shown }) }];
  }));
  const group = (area: string): GroupView => {
    const found = groups.get(area);
    if (found === undefined) throw new Error(`The proposal places a panel in ${area}, which it does not list.`);
    return found;
  };
  type Node = SerializedGridObject<GroupView>;
  const node = (part: Part, box: Size, along: 'width' | 'height'): Node => {
    if (isArea(part)) return { type: 'leaf', size: Math.round(box[along]), data: group(part.area) };
    return {
      type: 'branch',
      size: Math.round(box[along]),
      data: part.parts.map((p) => node(p.part, part.row ? { width: box.width * p.share, height: box.height } : { width: box.width, height: box.height * p.share }, part.row ? 'width' : 'height')),
    };
  };
  const top: Row = isArea(root) ? { row: true, parts: [{ part: root, share: 1 }] } : root;
  const grid = {
    root: { type: 'branch', size: Math.round(top.row ? room.height : room.width), data: node(top, room, top.row ? 'height' : 'width').data } as Node,
    width: room.width,
    height: room.height,
    orientation: top.row ? Orientation.HORIZONTAL : Orientation.VERTICAL,
  };
  const floating = proposal.areas.flatMap((a) => (a.float === undefined ? [] : [{ data: group(a.area), position: floatBox(a.float, room) }]));
  const panels = [...groups.values()].flatMap((g) => g.views);
  const activeGroup = [...groups.values()].find((g) => active !== undefined && g.activeView === active)?.id ?? group(first).id;
  return {
    grid: grid as SerializedDockview['grid'],
    panels: Object.fromEntries(panels.map((id) => {
      const title = titles[id];
      if (title === undefined) throw new Error(`Panel ${id} is placed but has no title.`);
      return [id, panelState(id, title)];
    })),
    ...(floating.length === 0 ? {} : { floatingGroups: floating as NonNullable<SerializedDockview['floatingGroups']> }),
    activeGroup,
  };
}

/** What the verbs read and change of the store to apply a proposal and put it back. */
export type ArrangingState = Pick<WorkspaceState, 'panels' | 'activePanelId' | 'recent' | 'openPanel' | 'closePanel' | 'activate'>;

/** What `arrange` needs beside the store: the arrangement drawn, and how pages not open are opened. */
export interface Arranging {
  readonly state: () => ArrangingState;
  readonly areas: () => Arrangement;
  readonly openRun: RunOpener;
}

/** What `arrange` acts on in a page: its workspace's panels, the arrangement drawn, and its Runs read by the server. */
export function arrangingOf(workspace: { readonly store: WorkspaceStore; readonly api: Pick<Api, 'run'> }): Arranging {
  return {
    state: () => workspace.store.getState(),
    areas: currentArrangement,
    openRun: (runId) => workspace.api.run(runId).then((run): PanelDraft => ({ kind: 'run', title: run.workflow.title, runId, viewKey: undefined })),
  };
}

// What the last `arrange` replaced, for the one `arrange back` (ADR-0018 §8): the arrangement before it with the area
// each of its floating panels came from, and the pages it opened, which `arrange back` closes.
let last: {
  readonly n: number;
  readonly before: SerializedDockview | undefined;
  readonly floatedFrom: FloatedFrom;
  readonly opened: readonly string[];
} | undefined;

/** Forgets what `arrange back` would put back: for tests, which start each page anew. */
export function forgetArranged(): void {
  last = undefined;
}

/** How the card and the line name a place: its panel's title, its Run, or its page. */
export function placeLabel(place: ProposedPlace): string {
  // `readProposal` holds a place to one of a panel with its title, a Run, or a page.
  if (place.panel !== undefined) {
    if (place.title === undefined) throw new Error(`The proposal names panel ${place.panel} without its title.`);
    return place.title;
  }
  if (place.run !== undefined) return `Run ${place.run}`;
  if (place.app === undefined) throw new Error('The proposal has a place that names no panel, Run or page.');
  // A page no application opens here is named by the word the proposal gave it: applying refuses it by that name.
  const opener = openers.get(place.app);
  const page = opener === undefined ? place.app : opener.title;
  return place.workspace === undefined ? page : `${page} ${place.workspace}`;
}

/** A page a proposal opens, where it is not open, and how. */
type Opening = { readonly place: number } & ({ readonly run: string } | { readonly opener: PageOpener; readonly workspace: string | undefined });

/** The panel open now that shows what a place names, if one does. */
function openFor(place: ProposedPlace, panels: readonly Panel[]): Panel | undefined {
  if (place.panel !== undefined) return panels.find((p) => p.id === place.panel);
  if (place.run !== undefined) return panels.find((p) => p.kind === 'run' && p.runId === place.run);
  return panels.find((p) => p.kind === 'app' && p.app === place.app && p.params.workspaceId === place.workspace);
}

/**
 * Checks that a proposal can be applied now, without changing anything: it names open panels still open and so titled,
 * pages the workbench opens, and fits the panels' space. Throws `CommandRefused` with the reason.
 */
function checked(proposal: Proposal, panels: readonly Panel[], room: Size): readonly Opening[] {
  const n = proposal.n;
  const openings: Opening[] = [];
  proposal.places.forEach((place, i) => {
    if (place.panel !== undefined) {
      const open = panels.find((p) => p.id === place.panel);
      if (open === undefined) throw new CommandRefused(`Proposal ${n} places ${place.title ?? place.panel}, which has been closed since; nothing was moved.`);
      if (panelLabel(open) !== place.title) throw new CommandRefused(`Proposal ${n} places ${place.panel} as ${place.title ?? 'untitled'}, and it is ${panelLabel(open)} now; nothing was moved.`);
      return;
    }
    if (openFor(place, panels) !== undefined) return;
    if (place.run !== undefined) { openings.push({ place: i, run: place.run }); return; }
    const opener = place.app === undefined ? undefined : openers.get(place.app);
    if (opener === undefined)
      throw new CommandRefused(`Proposal ${n} opens ${place.app ?? 'a page'}, which is no page the workbench opens (${[...openers.keys()].join(', ')}); nothing was moved.`);
    if (opener.workspace !== (place.workspace !== undefined))
      throw new CommandRefused(`Proposal ${n} opens ${opener.title} ${opener.workspace ? 'without the workspace it shows' : 'with a workspace, which it takes none of'}; nothing was moved.`);
    openings.push({ place: i, opener, workspace: place.workspace });
  });
  const width = Math.max(AREAS_FROM, proposal.needs.width);
  if (room.width < width || room.height < proposal.needs.height)
    throw new CommandRefused(`Proposal ${n} needs a panels' space of ${width} by ${proposal.needs.height}px, and it is ${Math.floor(room.width)} by ${Math.floor(room.height)}px now; nothing was moved.`);
  return openings;
}

/** Why `arrange <n>` cannot apply proposal `n` now; absent when it can. */
export function arrangeUnavailable(on: Arranging, n: number): string | undefined {
  const proposal = proposalsMade().find((p) => p.n === n);
  if (proposal === undefined) return `There is no proposal ${n}: the AI's proposals are numbered on their cards.`;
  try {
    checked(proposal, on.state().panels, on.areas().room());
    return undefined;
  } catch (error) {
    if (error instanceof CommandRefused) return error.message;
    throw error;
  }
}

/**
 * Applies proposal `n` (ADR-0018 §8): the pages it names that are not open are opened, then the arrangement is made as
 * proposed, whole or not at all. Refusals that need no server are thrown at once, as `CommandRefused`; what is said once
 * it is applied, or why it could not be after reading a page's title, is notified.
 */
export function arrange(on: Arranging, n: number, notify: Notify): void {
  const proposal = proposalsMade().find((p) => p.n === n);
  if (proposal === undefined) throw new CommandRefused(`There is no proposal ${n}: the AI's proposals are numbered on their cards.`);
  const openings = checked(proposal, on.state().panels, on.areas().room());
  if (openings.length === 0) { notify('done', applied(on, proposal, [])); return; }
  // The pages' titles are read first, all of them: one that cannot be read opens none. A defect in applying it is
  // thrown, never said as a refusal.
  void Promise.all(openings.map((o) => ('run' in o ? on.openRun(o.run) : o.opener.open(o.workspace)).then((draft) => ({ place: o.place, draft })))).then(
    (drafts) => {
      // The panels may have changed while the titles were read: checked again, and refused whole.
      try {
        checked(proposal, on.state().panels, on.areas().room());
      } catch (error) {
        if (!(error instanceof CommandRefused)) throw error;
        notify('refused', `arrange ${n}: ${error.message}`);
        return;
      }
      notify('done', applied(on, proposal, drafts));
    },
    (error: unknown) => notify('refused', `arrange ${n}: a page it opens could not be read (${error instanceof Error ? error.message : String(error)}); nothing was moved.`),
  );
}

/** The first area of the window a proposal lists: the server refuses a proposal without one. */
function firstWindowArea(proposal: Pick<Proposal, 'areas' | 'n'>): string {
  const first = proposal.areas.find((a) => a.float === undefined);
  if (first === undefined) throw new Error(`Proposal ${proposal.n} has no area of the window; the server refuses one without.`);
  return first.area;
}

/** Makes the arrangement as proposed, the pages it opens opened; answers what is said of it. */
function applied(on: Arranging, proposal: Proposal, drafts: readonly { readonly place: number; readonly draft: PanelDraft }[]): string {
  const { layout: before, floatedFrom } = keptArrangement.getState();
  const activeBefore = on.state().activePanelId;
  const opened: string[] = [];
  const ids = proposal.places.map((place, i) => {
    const open = openFor(place, on.state().panels);
    if (open !== undefined) return open.id;
    const draft = drafts.find((d) => d.place === i)?.draft;
    if (draft === undefined) throw new Error(`Place ${i} of proposal ${proposal.n} has no page to open.`);
    const id = on.state().openPanel(draft);
    opened.push(id);
    return id;
  });
  const state = on.state();
  const placed = new Set(ids);
  const others = state.panels.map((p) => p.id).filter((id) => !placed.has(id));
  const shown = new Set(proposal.places.flatMap((p, i) => (p.shown === true ? [ids[i]!] : [])));
  // The panel in use stays in use where the proposal shows it; else the first area's is.
  const firstArea = firstWindowArea(proposal);
  const firstShown = ids[proposal.places.findIndex((p) => p.area === firstArea && p.shown === true)];
  if (firstShown === undefined) throw new Error(`Proposal ${proposal.n} shows nothing in its first area; the server refuses that.`);
  const active = activeBefore !== undefined && shown.has(activeBefore) ? activeBefore : firstShown;
  if (state.activePanelId !== active) state.activate(active);
  const titles = Object.fromEntries(on.state().panels.map((p) => [p.id, panelLabel(p)]));
  const layout = proposalLayout(proposal, ids, titles, others, on.areas().room(), active);
  const areas = on.areas();
  // A proposal's floating area came from no area of the window: `dock panel` puts it at the right.
  keepDrawn(areas, layout, {});
  last = { n: proposal.n, before, floatedFrom, opened };
  const count = proposal.areas.length;
  const unplaced = others.length === 0 ? ''
    : ` ${others.length === 1 ? '1 panel the proposal did not place is' : `${others.length} panels the proposal did not place are`} in the area of ${titles[firstShown]}, the first in reading order.`;
  const openedSaid = opened.length === 0 ? '' : ` Opened ${opened.map((id) => titles[id]).join(', ')}.`;
  return `Arranged as proposal ${proposal.n}: ${count === 1 ? '1 area' : `${count} areas`}.${openedSaid}${unplaced} To put back what it replaced: arrange back.`;
}

/**
 * Keeps an arrangement for the browser tab with the area each of its floating panels came from, and draws it where the
 * panels stand in areas; in the strip it is drawn when the room comes back (ADR-0018 §5, §7).
 */
function keepDrawn(areas: Arrangement, layout: SerializedDockview, floatedFrom: FloatedFrom): void {
  keptArrangement.getState().keep(layout);
  if (areas.strip === undefined) areas.replace(layout);
  // After the drawing, which dockview may tell as moves of the panels it draws.
  keptArrangement.getState().keepFloatedFrom(floatedFrom);
}

/** Why `arrange back` cannot act now; absent when it can. */
export function arrangeBackUnavailable(): string | undefined {
  return last === undefined ? 'No proposal has been applied since the page was loaded, or it has been put back already.' : undefined;
}

/**
 * Puts back the arrangement the last `arrange` replaced, once (ADR-0018 §8), and closes the pages it opened, which
 * `reopen panel` brings back. A panel closed since is left out of it, and one opened since joins it as the kept
 * arrangement takes a panel opened in the strip.
 */
export function arrangeBack(on: Arranging, notify: Notify): void {
  const was = last;
  if (was === undefined) throw new CommandRefused('No proposal has been applied since the page was loaded, or it has been put back already.');
  const closing = on.state().panels.filter((p) => was.opened.includes(p.id));
  const titles = closing.map(panelLabel);
  for (const panel of closing) on.state().closePanel(panel.id, (panelId) => areaHolding(on.areas(), panelId)?.id);
  const open = openPanels(on.state());
  // What the proposal replaced, with the panels open now; where it was one area, or held none of them, one area.
  const before = was.before === undefined ? undefined : reconciled(was.before, open);
  const layout = before ?? oneArea(open, nextAreaId(), on.areas().room());
  const stillOpen = new Set(open.order);
  const floatedFrom = before === undefined ? {} : Object.fromEntries(Object.entries(was.floatedFrom).filter(([panelId]) => stillOpen.has(panelId)));
  keepDrawn(on.areas(), layout, floatedFrom);
  last = undefined;
  const put = was.before !== undefined && before === undefined
    ? `None of the panels the arrangement before proposal ${was.n} held is open, so the panels stand in one area.`
    : `Put back the arrangement proposal ${was.n} replaced.`;
  notify('done', `${put}${titles.length === 0 ? '' : ` Closed ${titles.join(', ')}, which it opened: reopen panel brings back the last.`}`);
}

/** The line's `arrange <n>` and `arrange back` (ADR-0018 §3, §8). */
export function arrangeCommands(on: Arranging, notify: Notify): Command[] {
  const made = proposalsMade();
  const back = arrangeBackUnavailable();
  const number = (text: string): number | undefined => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : undefined);
  return [
    {
      id: 'panel.arrange',
      words: 'arrange',
      title: 'Arrange the panels as the AI proposed, by the proposal\'s number on its card',
      group: 'Panels',
      args: [{
        name: 'proposal', required: true, hint: 'its number on the AI\'s card',
        complete: () => made.map((p) => ({ value: String(p.n), detail: `${p.areas.length === 1 ? '1 area' : `${p.areas.length} areas`}: ${p.places.filter((x) => x.shown === true).map(placeLabel).join(', ')}` })),
        check: (text) => {
          const n = number(text);
          return n === undefined ? 'a proposal\'s number, such as 1' : arrangeUnavailable(on, n);
        },
      }],
      ...(made.length === 0 ? { unavailable: 'The AI has proposed no arrangement on this page.' } : {}),
      run: (a) => {
        const n = number(given(a, 'proposal'));
        if (n === undefined) throw new CommandRefused('arrange takes a proposal\'s number, such as arrange 1.');
        arrange(on, n, notify);
      },
    },
    {
      id: 'panel.arrangeBack',
      words: 'arrange back',
      title: 'Put back the arrangement the last arrange replaced',
      group: 'Panels',
      ...(back === undefined ? {} : { unavailable: back }),
      run: () => arrangeBack(on, notify),
    },
  ];
}

/** What a panel shows, as a proposal's place names it. */
function shows(panel: Panel): Readonly<Record<string, string>> {
  if (panel.kind === 'run') return { run: panel.runId };
  if (panel.kind === 'app') return { app: panel.app, ...(panel.params.workspaceId === undefined ? {} : { workspace: panel.params.workspaceId }) };
  return {};
}

/**
 * The context every request carries of the panels (ADR-0018 §8, ADR-0007 §8): each open panel by its id and title,
 * what it shows and the area it stands in; the areas and the panels' space; and the pages a proposal may open. It is a
 * description, so that the agent can name panels in `layout.propose`; the agent moves none.
 */
export function panelsContext(state: Pick<WorkspaceState, 'panels' | 'activePanelId'>, areas: Arrangement = currentArrangement()): Context {
  const drawn = areas.strip === undefined ? areas.areas() : [];
  const room = areas.room();
  return {
    description: 'The panels open in the browser, the areas they stand in, and the pages layout.propose may open where they are not open',
    value: JSON.stringify({
      kind: 'qube.panels',
      panels: state.panels.map((p) => {
        const area = drawn.find((a) => a.panels.includes(p.id));
        return { panel: p.id, title: panelLabel(p), ...shows(p), ...(area === undefined ? {} : { area: area.id, shown: area.shown === p.id }), ...(p.id === state.activePanelId ? { active: true } : {}) };
      }),
      areas: drawn.map((a) => ({ area: a.id, floating: a.floating, width: Math.round(a.width), height: Math.round(a.height) })),
      ...(areas.strip === undefined ? {} : { strip: areas.strip }),
      room: { width: Math.round(room.width), height: Math.round(room.height) },
      minimum: AREA_MIN,
      opens: [...openers.values()].map((o) => ({ app: o.app, title: o.title, workspace: o.workspace })),
    }),
  };
}
