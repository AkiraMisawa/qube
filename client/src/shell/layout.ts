import { Orientation, type SerializedDockview, type SerializedGridObject } from 'dockview-react';
import { SM } from '../platform/panelPage';
import { panelLabel, type WorkspaceState } from '../platform/workspace/workspaceStore';

// The arrangement as dockview serialises it (ADR-0018 §2): what the strip waits with, what the browser tab keeps
// (§7), and what fits the panels' space (§5). Pure functions over dockview's own layout, so a test needs no browser.

/** The smallest an area may be, the narrowest phone a page is laid out for (ADR-0018 §5). */
export const AREA_MIN = { width: 320, height: 240 } as const;

/** The panels' space from which the panels stand in areas, the theme's `sm`; below it they are one strip (ADR-0018 §5). */
export const AREAS_FROM = SM;

/** The component dockview draws a panel's page with, and its tab with: the shell's own (`PanelAreas`). */
export const PAGE_COMPONENT = 'page';
export const TAB_COMPONENT = 'tab';

/** The width and height of something, in CSS pixels. */
export interface Size {
  readonly width: number;
  readonly height: number;
}

/**
 * Where an area stands: docked among the main window's areas, floating over them, or in another window of the browser
 * (ADR-0018 §9).
 */
export type AreaWhere = 'docked' | 'floating' | 'window';

/** An area as the verbs and the strip read it: its panels in tab order, the one it shows, and where it stands. */
export interface AreaOf {
  readonly id: string;
  readonly panels: readonly string[];
  readonly shown: string | undefined;
  readonly where: AreaWhere;
}

type Floating = NonNullable<SerializedDockview['floatingGroups']>[number];
type Popout = NonNullable<SerializedDockview['popoutGroups']>[number];
/** One area as dockview writes it: its id, its panels in tab order and the one it shows. */
type GroupPanelViewState = NonNullable<Floating['data']>;
type Node = SerializedGridObject<GroupPanelViewState>;

const isLeaf = (node: Node): node is Node & { readonly data: GroupPanelViewState } => node.type === 'leaf';
/**
 * The place dockview keeps in the window for a whole area that left it for another window: hidden, holding no panel,
 * so that the area comes back where it stood. It is no area: it holds nothing, takes no room and is not listed. (An
 * area hidden by a maximize holds its panels, and is one.)
 */
const isPlaceKept = (node: Node): boolean => isLeaf(node) && node.visible === false && node.data.views.length === 0;
const children = (node: Node): readonly Node[] => (isLeaf(node) ? [] : (node.data as Node[]));
const flip = (o: Orientation): Orientation => (o === Orientation.HORIZONTAL ? Orientation.VERTICAL : Orientation.HORIZONTAL);

/**
 * The room an arrangement needs: an area is `AREA_MIN`; areas side by side add their widths, one above another their
 * heights. Floating areas are drawn inside the space wherever it is, so they need only an area's own size.
 */
export function needs(layout: SerializedDockview): Size {
  const grid = needsOf(layout.grid.root, layout.grid.orientation);
  const floats = (layout.floatingGroups ?? []).length > 0 ? AREA_MIN : { width: 0, height: 0 };
  return { width: Math.max(grid.width, floats.width), height: Math.max(grid.height, floats.height) };
}

/** The room a part of the window's tree needs, its children laid along `o`. */
function needsOf(node: Node, o: Orientation): Size {
  if (node.visible === false) return { width: 0, height: 0 };
  if (isLeaf(node)) return AREA_MIN;
  const parts = children(node).map((c) => needsOf(c, flip(o)));
  if (parts.length === 0) return { width: 0, height: 0 };
  return o === Orientation.HORIZONTAL
    ? { width: sum(parts.map((p) => p.width)), height: Math.max(...parts.map((p) => p.height)) }
    : { width: Math.max(...parts.map((p) => p.width)), height: sum(parts.map((p) => p.height)) };
}

/**
 * The row or column an area of the window shares with its neighbours: along which axis, how much room the row has, and
 * the least the others need of it.
 */
export interface Line {
  readonly axis: 'width' | 'height';
  readonly total: number;
  readonly othersNeed: number;
}

/**
 * The row or column `size panel` shares out for this area (ADR-0018 §3): the nearest part of the window's tree that
 * holds the area beside another. Undefined for a floating area, or one alone in the window.
 */
export function lineOf(layout: SerializedDockview, areaId: string): Line | undefined {
  const holds = (node: Node): boolean => (isLeaf(node) ? node.data.id === areaId : children(node).some(holds));
  const find = (node: Node, o: Orientation): Line | undefined => {
    const parts = children(node).filter((p) => !isPlaceKept(p));
    const at = parts.findIndex(holds);
    const inner = at < 0 ? undefined : parts[at];
    if (inner === undefined) return undefined;
    const deeper = find(inner, flip(o));
    if (deeper !== undefined || parts.length < 2) return deeper;
    const axis = o === Orientation.HORIZONTAL ? 'width' : 'height';
    return {
      axis,
      total: sum(parts.map(sizeOf)),
      othersNeed: sum(parts.filter((_, i) => i !== at).map((p) => needsOf(p, flip(o))[axis])),
    };
  };
  return find(layout.grid.root, layout.grid.orientation);
}

const sum = (values: readonly number[]) => values.reduce((a, b) => a + b, 0);

/** A part's size along its row or column, as dockview writes it for each part of a row. */
const sizeOf = (node: Node): number => {
  if (node.size === undefined) throw new Error('dockview wrote a part of a row with no size.');
  return node.size;
};

/** Whether the panels' space shows areas for this arrangement, or the one strip (ADR-0018 §5). */
export function fits(layout: SerializedDockview | undefined, room: Size): boolean {
  if (room.width < AREAS_FROM || room.height < AREA_MIN.height) return false;
  if (layout === undefined) return true;
  const needed = needs(layout);
  return needed.width <= room.width && needed.height <= room.height;
}

/**
 * The areas in reading order (ADR-0018 §1): the window's areas in the order of their splits, the left or upper part
 * first and all of it before the other part, then the floating areas in the order they were floated, then the areas of
 * other windows in the order they were opened.
 */
export function areasOf(layout: SerializedDockview): readonly AreaOf[] {
  const grid: AreaOf[] = [];
  const walk = (node: Node) => {
    if (isLeaf(node)) { if (!isPlaceKept(node)) grid.push(area(node.data, 'docked')); }
    else children(node).forEach(walk);
  };
  walk(layout.grid.root);
  return [
    ...grid,
    ...(layout.floatingGroups ?? []).flatMap((f) => groupsOf(f).map((g) => area(g, 'floating'))),
    ...(layout.popoutGroups ?? []).flatMap((p) => groupsOf(p).map((g) => area(g, 'window'))),
  ];
}

function area(group: GroupPanelViewState, where: AreaWhere): AreaOf {
  return { id: group.id, panels: [...group.views], shown: group.activeView, where };
}

/** The areas a floating area or another window holds: one, or a tree of them. */
function groupsOf(f: Floating | Popout): readonly GroupPanelViewState[] {
  if (f.data !== undefined) return [f.data];
  if (f.grid === undefined) return [];
  const groups: GroupPanelViewState[] = [];
  const walk = (node: Node) => { if (isLeaf(node)) groups.push(node.data); else children(node).forEach(walk); };
  walk(f.grid.root);
  return groups;
}

/** Panels of another window put back into the main window: which, and into which area. */
export interface Returned {
  readonly panels: readonly string[];
  readonly into: string;
  /** Whether that area is the one they came from. */
  readonly cameFrom: boolean;
}

/**
 * The arrangement with its other windows folded into the main window (ADR-0018 §9's note of 2026-11-13): a browser
 * opens a window only on a key or a press, so one kept across a reload, or past the strip, is not opened again. Each
 * window's panels go back into the area they came from where it is there, hidden or shown, else into the last area of
 * the main window, after its own panels; that area shows the panel it showed. Answers what went where, to be said.
 */
export function withWindowsDocked(layout: SerializedDockview): { readonly layout: SerializedDockview; readonly returned: readonly Returned[] } {
  const windows = layout.popoutGroups ?? [];
  const { popoutGroups: _, ...rest } = layout;
  if (windows.length === 0) return { layout: rest, returned: [] };
  const joining = new Map<string, string[]>();
  const returned: Returned[] = [];
  const leaves: (Node & { readonly data: GroupPanelViewState })[] = [];
  const collect = (node: Node) => { if (isLeaf(node)) leaves.push(node); else children(node).forEach(collect); };
  collect(layout.grid.root);
  const last = leaves.filter((l) => !isPlaceKept(l)).at(-1);
  for (const w of windows) {
    const panels = groupsOf(w).flatMap((g) => g.views);
    if (panels.length === 0) continue;
    const from = w.gridReferenceGroup === undefined ? undefined : leaves.find((l) => l.data.id === w.gridReferenceGroup);
    const to = from ?? last;
    if (to === undefined) throw new Error('The arrangement has no area in the main window to put another window\'s panels into.');
    joining.set(to.data.id, [...(joining.get(to.data.id) ?? []), ...panels]);
    returned.push({ panels, into: to.data.id, cameFrom: from !== undefined });
  }
  const nodeOf = (node: Node): Node => {
    if (!isLeaf(node)) return { ...node, data: children(node).map(nodeOf) };
    const more = joining.get(node.data.id);
    if (more === undefined) return node;
    const views = [...node.data.views, ...more];
    const { visible: _hidden, ...shown } = node;
    const activeView = node.data.activeView ?? views[0];
    return { ...shown, data: { ...node.data, views, ...(activeView === undefined ? {} : { activeView }) } };
  };
  return { layout: { ...rest, grid: { ...layout.grid, root: nodeOf(layout.grid.root) } }, returned };
}

/** Every panel of the arrangement in reading order: `next panel` goes through them so (ADR-0018 §3). */
export function readingOrder(layout: SerializedDockview): readonly string[] {
  return areasOf(layout).flatMap((a) => a.panels);
}

/** The panels as the workspace store holds them, which an arrangement is made to agree with. */
export interface OpenPanels {
  /** In the strip's order: the pinned first. */
  readonly order: readonly string[];
  /** Each panel's label, as its tab says it. */
  readonly titles: Readonly<Record<string, string>>;
  /** The panels by when they were last shown, the latest first. */
  readonly recent: readonly string[];
  readonly activePanelId: string | undefined;
}

/** The store's panels as an arrangement is made to agree with them. */
export function openPanels(state: Pick<WorkspaceState, 'panels' | 'recent' | 'activePanelId'>): OpenPanels {
  return {
    order: state.panels.map((p) => p.id),
    titles: Object.fromEntries(state.panels.map((p) => [p.id, panelLabel(p)])),
    recent: state.recent,
    activePanelId: state.activePanelId,
  };
}

/** dockview's state of one panel: the shell's page and tab, which find the panel by its id. */
export function panelState(id: string, title: string): SerializedDockview['panels'][string] {
  return { id, contentComponent: PAGE_COMPONENT, tabComponent: TAB_COMPONENT, title, params: { panelId: id } };
}

/** Every open panel in one area, in the strip's order: the arrangement of today, and what `gather panels` makes. */
export function oneArea(open: OpenPanels, id = 'area-1', size: Size = { width: 1000, height: 1000 }): SerializedDockview {
  const views = [...open.order];
  const shown = open.activePanelId !== undefined && views.includes(open.activePanelId) ? open.activePanelId : views[0];
  return {
    grid: {
      root: { type: 'branch', size: size.height, data: views.length === 0 ? [] : [{ type: 'leaf', size: size.width, data: { id, views, ...(shown === undefined ? {} : { activeView: shown }) } }] },
      width: size.width,
      height: size.height,
      orientation: Orientation.HORIZONTAL,
    },
    panels: Object.fromEntries(views.map((v) => [v, panelState(v, titleOf(open, v))])),
    ...(views.length === 0 ? {} : { activeGroup: id }),
  };
}

const titleOf = (open: OpenPanels, id: string): string => {
  const title = open.titles[id];
  if (title === undefined) throw new Error(`Panel ${id} is in the strip's order but has no title.`);
  return title;
};

/**
 * The kept arrangement made to agree with the panels open now (ADR-0018 §5, spec "The kept arrangement"): a panel
 * closed since is taken out, and an area left empty goes; a panel opened since joins the area of the panel before it
 * in the strip, else the first area of the window; each area's panels follow the strip's order. Undefined where no
 * area of the window is left, so the panels start again in one area.
 */
export function reconciled(layout: SerializedDockview, open: OpenPanels): SerializedDockview | undefined {
  const at = new Map(open.order.map((id, i) => [id, i]));
  const place = (id: string): number => {
    const i = at.get(id);
    if (i === undefined) throw new Error(`Panel ${id} is not in the strip's order.`);
    return i;
  };
  const areas = areasOf(layout);
  const kept = new Set(areas.flatMap((a) => a.panels).filter((id) => at.has(id)));
  const areaOfPanel = new Map(areas.flatMap((a) => a.panels.map((p) => [p, a.id] as const)));
  const firstArea = areas.find((a) => a.where !== 'floating' && a.panels.some((p) => kept.has(p)))?.id;
  // Where each new panel goes: the area of the nearest panel before it in the strip that the arrangement keeps.
  const joining = new Map<string, string[]>();
  open.order.forEach((id, i) => {
    if (kept.has(id)) return;
    const before = open.order.slice(0, i).reverse().find((p) => kept.has(p));
    const to = before === undefined ? firstArea : areaOfPanel.get(before);
    if (to !== undefined) joining.set(to, [...(joining.get(to) ?? []), id]);
  });
  const groupOf = (group: GroupPanelViewState): GroupPanelViewState | undefined => {
    const views = [...group.views.filter((v) => kept.has(v)), ...(joining.get(group.id) ?? [])].sort((a, b) => place(a) - place(b));
    if (views.length === 0) return undefined;
    const shown = group.activeView !== undefined && views.includes(group.activeView)
      ? group.activeView
      : open.recent.find((id) => views.includes(id)) ?? views[0];
    const { tabGroups: _, activeView: _shown, ...rest } = group;
    return { ...rest, views, ...(shown === undefined ? {} : { activeView: shown }) };
  };
  const nodeOf = (node: Node): Node | undefined => {
    if (isLeaf(node)) {
      const group = groupOf(node.data);
      return group === undefined ? undefined : { ...node, data: group };
    }
    const parts = children(node).map(nodeOf).filter((n): n is Node => n !== undefined);
    return parts.length === 0 ? undefined : { ...node, data: parts };
  };
  const root = nodeOf(layout.grid.root);
  if (root === undefined) return undefined;
  const floating = (layout.floatingGroups ?? []).flatMap((f): Floating[] => {
    if (f.data !== undefined) {
      const group = groupOf(f.data);
      return group === undefined ? [] : [{ position: f.position, data: group }];
    }
    if (f.grid === undefined) return [];
    const inner = nodeOf(f.grid.root);
    return inner === undefined ? [] : [{ position: f.position, grid: { ...f.grid, root: inner } }];
  });
  const made: SerializedDockview = {
    grid: { ...layout.grid, root: root.type === 'branch' ? root : { type: 'branch', size: layout.grid.height, data: [root] } },
    panels: {},
    ...(floating.length === 0 ? {} : { floatingGroups: floating }),
  };
  const ids = readingOrder(made);
  const active = open.activePanelId;
  const activeGroup = active === undefined ? undefined : areasOf(made).find((a) => a.panels.includes(active))?.id;
  return {
    ...made,
    panels: Object.fromEntries(ids.map((id) => [id, panelState(id, titleOf(open, id))])),
    ...(activeGroup === undefined ? {} : { activeGroup }),
  };
}

/** The strip's order for the panels of an arrangement: the pinned first, each kind in reading order (ADR-0018 §5). */
export function stripOrder(layout: SerializedDockview, pinned: (id: string) => boolean): readonly string[] {
  const read = readingOrder(layout);
  return [...read.filter(pinned), ...read.filter((id) => !pinned(id))];
}
