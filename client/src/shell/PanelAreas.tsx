import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type PointerEvent } from 'react';
import { CloseButton, Tooltip } from '@mantine/core';
import { IconPin } from '@tabler/icons-react';
import {
  DockviewReact, type AnchoredBox, type DockviewApi, type DockviewGroupPanel, type DockviewPanelApi, type DockviewReadyEvent, type DockviewTheme,
  type DockviewWillShowOverlayLocationEvent, type IDockviewHeaderActionsProps, type IDockviewPanelHeaderProps, type IDockviewPanelProps, type SerializedDockview,
} from 'dockview-react';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { panelLabel, type Panel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { ICON, STROKE } from './icons';
import { arrangementChanged, currentArrangement, nextAreaId, registerArrangement, sawAreas, useArrangementVersion } from './arrangement';
import { dockArrangement } from './dockArrangement';
import { keptArrangement } from './keptPanels';
import { AREA_MIN, areasOf, fits, needs, oneArea, openPanels, PAGE_COMPONENT, reconciled, stripOrder, TAB_COMPONENT, type Size } from './layout';
import { CLOSE, FLOAT, onNow } from './panelVerbs';
import { PanelContent, PanelList, TabMenu, useRunVerb, type MenuAt } from './panelMenus';

// The panels in areas (ADR-0018), drawn by dockview from a panels' space of 768px. The workspace store stays what each
// panel shows, which one is active, and which were closed; dockview holds where each stands, and the verbs reach that
// through the port `dockArrangement` registers. Each area is under a strip of the shell's own tabs, with note 7's menu
// and list; only the page each area shows is mounted, and a page moved between areas stays mounted.

/** The shell's dockview theme: its colours and sizes are the shell's tokens (`shell.css`). */
const THEME: DockviewTheme = { name: 'qube', className: 'dockview-theme-qube' };

/** Whether a page's panel is the one its area shows: only that page is drawn (ADR-0018 §6). */
function useShownInArea(api: DockviewPanelApi): boolean {
  const [shown, setShown] = useState(() => api.group.activePanel?.id === api.id);
  useEffect(() => {
    // Read again as the area shows another panel, as the panel moves to another area, and as its area is hidden by a
    // maximize, which leaves it the panel its area shows.
    const update = () => setShown(api.group.activePanel?.id === api.id);
    update();
    const watched = [api.onDidVisibilityChange(update), api.onDidGroupChange(update), api.onDidActiveChange(update)];
    return () => watched.forEach((w) => w.dispose());
  }, [api]);
  return shown;
}

function Page({ api, params }: IDockviewPanelProps<{ readonly panelId: string }>) {
  const panel = useWorkspace((s) => s.panels.find((p) => p.id === params.panelId));
  const shown = useShownInArea(api);
  if (panel === undefined || !shown) return null;
  // The page of the tab its area shows. dockview's content holding it is the area's tab panel, named by that tab, as the
  // strip's tab panel is: one more here would be a tab panel inside a tab panel.
  return <div className="qube-area-page"><PanelContent panel={panel} /></div>;
}

/**
 * Keeps the tab of the panel its area shows in sight in the area's strip, as note 7's strip keeps its active tab
 * (ADR-0018 §1). dockview scrolls a tab into view as it becomes active, but before its title is drawn, so a title that
 * then widens the tab past the strip's end leaves it partly hidden; and it does not scroll for a tab moved, or a strip
 * narrowed. Read again as the tab's and the strip's widths change, as the browser reports them, and as the arrangement
 * changes. Only the strip scrolls, sideways.
 */
function useTabInSight(api: DockviewPanelApi, self: { readonly current: HTMLElement | null }): void {
  const shown = useShownInArea(api);
  const arrangement = useArrangementVersion();
  useEffect(() => {
    const tab = self.current?.closest<HTMLElement>('.dv-tab');
    const strip = tab?.parentElement;
    if (!shown || tab === undefined || tab === null || strip === undefined || strip === null) return;
    const keep = () => {
      const [t, v] = [tab.getBoundingClientRect(), strip.getBoundingClientRect()];
      if (t.left < v.left) strip.scrollLeft -= v.left - t.left;
      else if (t.right > v.right) strip.scrollLeft += t.right - v.right;
    };
    keep();
    const sizes = new ResizeObserver(keep);
    sizes.observe(tab);
    sizes.observe(strip);
    return () => sizes.disconnect();
  }, [shown, arrangement, self]);
}

/** A tab of an area: dockview's `role="tab"` element holds it, and takes the keys; the areas' root takes the rest. */
function AreaTab({ api, params }: IDockviewPanelHeaderProps<{ readonly panelId: string }>) {
  const panel = useWorkspace((s) => s.panels.find((p) => p.id === params.panelId));
  const run = useRunVerb();
  const self = useRef<HTMLDivElement>(null);
  // The tab dockview draws opens note 7's menu, and says so.
  useEffect(() => { self.current?.closest('.dv-tab')?.setAttribute('aria-haspopup', 'menu'); }, []);
  useTabInSight(api, self);
  if (panel === undefined) return null;
  const label = panelLabel(panel);
  return (
    <div ref={self} className="qube-area-tab" data-panel={panel.id} {...(panel.pinned === true ? { 'data-pinned': true } : {})}>
      {panel.pinned === true && <IconPin size={ICON} stroke={STROKE} aria-label="pinned" />}
      <span className="qube-panel-tab-title" title={label}>{label}</span>
      {panel.pinned !== true && (
        <Tooltip label="Close (close panel)">
          <CloseButton size="xs" component="span" className="qube-area-tab-close" aria-label={`Close ${label}`}
            onClick={(e) => { e.stopPropagation(); run(CLOSE, panel); }} />
        </Tooltip>
      )}
    </div>
  );
}

/** An area's list of its panels, at the end of its strip, with the verbs of the panel it shows. */
function AreaActions({ panels: held, activePanel }: IDockviewHeaderActionsProps) {
  const panels = useWorkspace((s) => s.panels);
  const run = useRunVerb();
  useArrangementVersion();
  const areas = currentArrangement().areas().length;
  // Read from the store as dockview holds them; a panel the store has just closed is not listed while dockview lets go.
  const mine = held.flatMap((d) => panels.filter((p) => p.id === d.id));
  return <PanelList panels={mine} shown={panels.find((p) => p.id === activePanel?.id)} run={run} name={areas > 1 ? 'Panels in this area' : 'All panels'} />;
}

const COMPONENTS = { [PAGE_COMPONENT]: Page };
const TABS = { [TAB_COMPONENT]: AreaTab };

/** The tab element and the open panel it is of, where an event happened on a tab. */
function tabAt(target: EventTarget | null, panels: readonly Panel[]): { readonly element: HTMLElement; readonly panel: Panel } | undefined {
  if (!(target instanceof Element)) return undefined;
  const element = target.closest<HTMLElement>('.dv-tab');
  const id = element?.querySelector('[data-panel]')?.getAttribute('data-panel');
  const panel = panels.find((p) => p.id === id);
  return element === null || panel === undefined ? undefined : { element, panel };
}

/** Where a floating area is anchored, from its box as dockview writes it. */
function anchorOf(box: AnchoredBox): AnchoredBox {
  const across = 'left' in box ? { left: box.left } : { right: box.right };
  const down = 'top' in box ? { top: box.top } : { bottom: box.bottom };
  return { ...across, ...down, width: box.width, height: box.height } as AnchoredBox;
}

/**
 * Whether a drop dockview is about to offer would leave an area under its minimum, no area docked in the window, or a
 * floating area split in two (a floating area is one area: ADR-0018 §3's note of 2026-11-12): its overlay is not
 * shown, so the drop is not there to make (ADR-0018 §5).
 */
function dropRefused(dock: DockviewApi, event: DockviewWillShowOverlayLocationEvent): boolean {
  const data = event.getData();
  const from = data === undefined ? undefined : dock.groups.find((g) => g.id === data.groupId);
  const docked = dock.groups.filter((g) => g.api.location.type === 'grid');
  const leavesNone = from !== undefined && from.api.location.type === 'grid' && docked.length === 1
    && (data?.panelId === null || from.panels.length === 1);
  if (leavesNone && event.group !== undefined && event.group.api.location.type !== 'grid') return true;
  if (event.kind === 'tab' || event.kind === 'header_space' || event.position === 'center') return false;
  if (event.kind === 'content' && event.group !== undefined && event.group.api.location.type !== 'grid') return true;
  const across = event.position === 'left' || event.position === 'right';
  if (event.kind === 'edge') {
    const need = needs(dock.toJSON());
    return across ? dock.width < need.width + AREA_MIN.width : dock.height < need.height + AREA_MIN.height;
  }
  const target = event.group;
  if (target === undefined) return false;
  return across ? target.width < 2 * AREA_MIN.width : target.height < 2 * AREA_MIN.height;
}

/** How far a key moves a splitter or a floating area, or resizes one (ADR-0018 §3, docs/design-guidelines.md §6). */
const STEP = 24;

const ARROWS: Readonly<Record<string, { readonly dx: number; readonly dy: number }>> = {
  ArrowLeft: { dx: -STEP, dy: 0 }, ArrowRight: { dx: STEP, dy: 0 }, ArrowUp: { dx: 0, dy: -STEP }, ArrowDown: { dx: 0, dy: STEP },
};

/**
 * Makes each splitter a focusable separator, and each floating area's title bar a focusable handle, named as such:
 * dockview draws them for the pointer only. Run whenever dockview has drawn the arrangement anew.
 */
function keyboardHandles(root: HTMLElement): void {
  for (const sash of root.querySelectorAll<HTMLElement>('.dv-sash')) {
    const split = sash.closest('.dv-split-view-container');
    const across = split?.classList.contains('dv-horizontal') === true;
    sash.setAttribute('role', 'separator');
    sash.setAttribute('aria-orientation', across ? 'vertical' : 'horizontal');
    sash.setAttribute('aria-label', across ? 'Splitter between areas side by side' : 'Splitter between areas one above the other');
    sash.tabIndex = 0;
    // A focusable separator says where it stands (WAI-ARIA): the share, in percent, of the row or column before it,
    // read from the sizes dockview wrote on its views, never from the layout the browser made of them.
    const views = [...(split?.querySelectorAll<HTMLElement>(':scope > .dv-view-container > .dv-view') ?? [])];
    const end = (view: HTMLElement | undefined): number => {
      if (view === undefined) throw new Error('A splitter stands beside no area.');
      const at = parseFloat(across ? view.style.left : view.style.top);
      const size = parseFloat(across ? view.style.width : view.style.height);
      if (!Number.isFinite(at) || !Number.isFinite(size)) throw new Error('dockview drew an area without its size.');
      return at + size;
    };
    const before = sash.parentElement === null ? -1 : [...sash.parentElement.children].indexOf(sash);
    sash.setAttribute('aria-valuemin', '0');
    sash.setAttribute('aria-valuemax', '100');
    sash.setAttribute('aria-valuenow', String(Math.round((100 * end(views[before])) / end(views.at(-1)))));
  }
  for (const bar of root.querySelectorAll<HTMLElement>('.dv-floating-titlebar')) {
    const shown = bar.closest('.dv-resize-container')?.querySelector('.dv-tab.dv-active-tab')?.getAttribute('aria-label');
    bar.setAttribute('role', 'button');
    bar.setAttribute('aria-roledescription', 'move handle');
    bar.setAttribute('aria-label', `Floating area${shown === null || shown === undefined ? '' : ` of ${shown}`}: the arrows move it, Shift with the arrows resizes it`);
    bar.tabIndex = 0;
  }
}

/**
 * The keys of a splitter and of a floating area's title bar: the arrows move a splitter by 24px, as a drag of it would,
 * within the areas' minimum; they move a floating area by 24px, and with Shift resize it, never under the minimum.
 * Answers whether the key was one of these.
 */
function handleKey(event: KeyboardEvent<HTMLElement>, dock: DockviewApi, root: HTMLElement, room: Size): boolean {
  const arrow = ARROWS[event.key];
  if (arrow === undefined || !(event.target instanceof HTMLElement)) return false;
  const target = event.target;
  if (target.classList.contains('dv-sash')) {
    const across = target.getAttribute('aria-orientation') === 'vertical';
    const by = across ? arrow.dx : arrow.dy;
    if (by === 0) return false;
    event.preventDefault();
    event.stopPropagation();
    // dockview moves a splitter only as the pointer drags it: the key is that drag, 24px long, so dockview's own
    // limits hold it at each area's minimum.
    const box = target.getBoundingClientRect();
    const at = { clientX: box.left + box.width / 2, clientY: box.top + box.height / 2, bubbles: true, isPrimary: true, pointerId: 1 };
    target.dispatchEvent(new PointerEvent('pointerdown', at));
    const to = { ...at, clientX: at.clientX + (across ? by : 0), clientY: at.clientY + (across ? 0 : by) };
    target.ownerDocument.dispatchEvent(new PointerEvent('pointermove', to));
    target.ownerDocument.dispatchEvent(new PointerEvent('pointerup', to));
    return true;
  }
  if (!target.classList.contains('dv-floating-titlebar')) return false;
  const container = target.closest('.dv-resize-container');
  const group = dock.groups.find((g) => container?.contains(g.element) === true);
  const floating = dock.toJSON().floatingGroups?.find((f) => f.data?.id === group?.id);
  if (group === undefined || floating === undefined) throw new Error('A floating title bar took a key with no floating area under it.');
  event.preventDefault();
  event.stopPropagation();
  const box = floating.position;
  const width = Math.max(AREA_MIN.width, box.width + (event.shiftKey ? arrow.dx : 0));
  const height = Math.max(AREA_MIN.height, box.height + (event.shiftKey ? arrow.dy : 0));
  const left = 'left' in box ? box.left : room.width - box.right - box.width;
  const top = 'top' in box ? box.top : room.height - box.bottom - box.height;
  const moved = event.shiftKey ? { left, top } : {
    left: Math.min(Math.max(0, left + arrow.dx), Math.max(0, room.width - width)),
    top: Math.min(Math.max(0, top + arrow.dy), Math.max(0, room.height - height)),
  };
  dock.addFloatingGroup(group, { position: moved, width, height });
  // dockview draws the area again: the keyboard stays on its title bar.
  keyboardHandles(root);
  group.element.closest('.dv-resize-container')?.querySelector<HTMLElement>('.dv-floating-titlebar')?.focus();
  return true;
}

export function PanelAreas({ room }: { readonly room: Size }) {
  const panels = useWorkspace((s) => s.panels);
  const { store, notify } = useWorkspaceContext();
  const run = useRunVerb();
  const root = useRef<HTMLDivElement>(null);
  const [menuAt, setMenuAt] = useState<MenuAt | undefined>(undefined);
  const [areaCount, setAreaCount] = useState(1);
  // Where the keyboard goes once the areas have drawn what a key did (as in the strip): the tab shown next where Delete
  // closed one, or the tab a menu opened from the keyboard was on.
  const keyboardTo = useRef<{ readonly tab: string | undefined } | undefined>(undefined);
  const roomNow = useRef(room);
  roomNow.current = room;
  const disposeAll = useRef<(() => void) | undefined>(undefined);
  const dockNow = useRef<DockviewApi | undefined>(undefined);

  const onReady = useCallback((event: DockviewReadyEvent) => {
    const dock = event.api;
    dockNow.current = dock;
    const { port, stop } = dockArrangement(dock);
    let syncing = false;
    let pending = false;

    // Each area is at least 320 by 240px (ADR-0018 §5), so a splitter stops there. A floating area's edges are
    // dockview's, which stop at 20px: one drawn smaller is put back at the minimum once the edge is let go.
    const constrain = (g: DockviewGroupPanel) => g.api.setConstraints({ minimumWidth: AREA_MIN.width, minimumHeight: AREA_MIN.height });
    const atLeastMinimum = (layout: SerializedDockview): boolean => {
      for (const f of layout.floatingGroups ?? []) {
        const box = f.position;
        if (f.data === undefined || (box.width >= AREA_MIN.width && box.height >= AREA_MIN.height)) continue;
        const g = dock.groups.find((x) => x.id === f.data?.id);
        if (g === undefined) throw new Error(`The floating area ${f.data.id} is not drawn.`);
        quietly(() => dock.addFloatingGroup(g, { position: anchorOf(box), width: Math.max(box.width, AREA_MIN.width), height: Math.max(box.height, AREA_MIN.height) }));
        return false;
      }
      return true;
    };

    // What the arrangement is now, told to the store, the kept arrangement and the verbs: the strip's order is the
    // reading order with the pinned first, and an area keeps its pinned panels first (ADR-0006 note 8).
    const arranged = (): void => {
      const layout = dock.toJSON();
      if (!atLeastMinimum(layout)) { arranged(); return; }
      const state = store.getState();
      const pinned = new Set(state.panels.filter((p) => p.pinned === true).map((p) => p.id));
      for (const area of areasOf(layout)) {
        const want = [...area.panels.filter((id) => pinned.has(id)), ...area.panels.filter((id) => !pinned.has(id))];
        if (want.every((id, i) => area.panels[i] === id)) continue;
        quietly(() => want.forEach((id, i) => port.moveWithin(id, i)));
        arranged();
        return;
      }
      const order = stripOrder(layout, (id) => pinned.has(id));
      if (order.length === state.panels.length && order.some((id, i) => state.panels[i]?.id !== id)) state.setOrder(order);
      if (fits(layout, roomNow.current)) keptArrangement.getState().keep(layout);
      setAreaCount(areasOf(layout).length);
      if (root.current !== null) keyboardHandles(root.current);
      arrangementChanged();
    };
    /** Changes dockview without hearing back each step, and tells what changed once at the end. */
    const quietly = (change: () => void): void => {
      const outer = syncing;
      syncing = true;
      try {
        change();
      } finally {
        syncing = outer;
      }
    };

    // The store's panels drawn: one opened joins the area of the panel before it in the strip, beside it, as it opened
    // beside the active panel (ADR-0018 §4); one closed leaves; the active one is the one shown.
    const sync = (): void => {
      quietly(() => {
        const { panels: open, activePanelId } = store.getState();
        open.forEach((p, i) => {
          const title = panelLabel(p);
          const there = dock.getPanel(p.id);
          if (there !== undefined) {
            if (there.title !== title) there.api.setTitle(title);
            return;
          }
          const before = open.slice(0, i).reverse().map((q) => dock.getPanel(q.id)).find((d) => d !== undefined);
          const first = dock.groups.find((g) => g.api.location.type === 'grid');
          dock.addPanel({
            id: p.id, component: PAGE_COMPONENT, tabComponent: TAB_COMPONENT, title, params: { panelId: p.id }, inactive: true,
            ...(before !== undefined
              ? { position: { referencePanel: before, direction: 'within', index: before.group.panels.indexOf(before) + 1 } }
              : first !== undefined ? { position: { referenceGroup: first, direction: 'within' } } : {}),
          });
        });
        for (const d of dock.panels) if (!open.some((p) => p.id === d.id)) dock.removePanel(d);
        if (activePanelId !== undefined && dock.activePanel?.id !== activePanelId) dock.getPanel(activePanelId)?.api.setActive();
      });
      arranged();
    };

    const watched = [
      dock.onDidAddGroup(constrain),
      dock.onDidActivePanelChange(({ panel }) => {
        if (syncing || panel === undefined) return;
        if (store.getState().activePanelId !== panel.id) store.getState().activate(panel.id);
      }),
      dock.onDidLayoutChange(() => {
        if (syncing) { pending = true; return; }
        pending = false;
        arranged();
      }),
      dock.onWillShowOverlay((e) => { if (dropRefused(dock, e)) e.preventDefault(); }),
    ];

    // Drawn first from the kept arrangement, made to agree with the panels open, or every panel in one area.
    quietly(() => {
      const open = openPanels(store.getState());
      const kept = keptArrangement.getState().layout;
      dock.fromJSON((kept === undefined ? undefined : reconciled(kept, open)) ?? oneArea(open, nextAreaId(), roomNow.current));
      dock.groups.forEach(constrain);
      // An arrangement kept across a reload holds ids from before it: a new area takes none of them.
      sawAreas(dock.groups.map((g) => g.id));
    });
    const unsubscribe = store.subscribe(sync);
    const unregister = registerArrangement(port);
    sync();
    if (pending) arranged();
    disposeAll.current = () => {
      stop();
      unregister();
      unsubscribe();
      watched.forEach((w) => w.dispose());
    };
  }, [store]);
  useEffect(() => () => disposeAll.current?.(), []);

  const tabElement = useCallback((panelId: string | undefined) =>
    (panelId === undefined ? null : root.current?.querySelector(`[data-panel="${panelId}"]`)?.closest<HTMLElement>('.dv-tab')) ?? null, []);
  useEffect(() => {
    const to = keyboardTo.current;
    if (to === undefined) return;
    keyboardTo.current = undefined;
    (tabElement(to.tab) ?? tabElement(store.getState().activePanelId))?.focus();
  });

  // The keys of a tab, before dockview's own: Delete and Backspace close through `close panel`, which follows the
  // active panel in its area; Shift+F10 and the menu key open note 7's menu.
  const onKeyDownCapture = (event: KeyboardEvent<HTMLDivElement>) => {
    const dock = dockNow.current;
    if (dock !== undefined && root.current !== null && handleKey(event, dock, root.current, roomNow.current)) return;
    const tab = tabAt(event.target, store.getState().panels);
    if (tab === undefined || event.target !== tab.element) return;
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      event.stopPropagation();
      keyboardTo.current = { tab: undefined };
      run(CLOSE, tab.panel);
    } else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      event.stopPropagation();
      const box = tab.element.getBoundingClientRect();
      setMenuAt({ panelId: tab.panel.id, x: box.left, y: box.bottom, keyboard: true });
    }
  };
  const onContextMenu = (event: MouseEvent<HTMLDivElement>) => {
    const tab = tabAt(event.target, store.getState().panels);
    if (tab === undefined) return;
    event.preventDefault();
    // The menu key on Windows also sends this as it is let go, after its keydown opened the menu (as in the strip).
    setMenuAt((open) => (open?.keyboard === true && open.panelId === tab.panel.id
      ? open
      : { panelId: tab.panel.id, x: event.clientX, y: event.clientY, keyboard: false }));
  };
  const onPointerDownCapture = (event: PointerEvent<HTMLDivElement>) => {
    if (!(event.target instanceof Element)) return;
    // A press on a tab's close button closes it; it does not first show it, as dockview's press on a tab would.
    if (event.target.closest('.qube-area-tab-close') !== null) { event.stopPropagation(); return; }
    if (!event.shiftKey) return;
    // Shift with a press on a tab floats it (dockview's gesture): refused, as `float panel` is, where it would leave no
    // area docked.
    const tab = tabAt(event.target, store.getState().panels);
    if (tab !== undefined) {
      const why = FLOAT.unavailable(onNow(store.getState()), tab.panel);
      if (why === undefined) return;
      event.preventDefault();
      notify('refused', `${FLOAT.words}: ${why}`);
      return;
    }
    // Shift with a press on the empty end of a strip floats its whole area (dockview's gesture too): refused where that
    // area is the only one docked.
    if (event.target.closest('.dv-void-container') === null) return;
    const dock = dockNow.current;
    if (dock === undefined) throw new Error('A strip was pressed before dockview drew the areas.');
    const target = event.target;
    const pressed = dock.groups.find((g) => g.element.contains(target));
    if (pressed === undefined) throw new Error('A strip was pressed that is in no area dockview holds.');
    const docked = onNow(store.getState()).areas.areas().filter((a) => !a.floating);
    if (docked.length !== 1 || docked[0]?.id !== pressed.id) return;
    event.preventDefault();
    notify('refused', `${FLOAT.words}: It is the only area in the window; floating it would leave none docked.`);
  };
  // The middle button closes a tab, as a browser's does; held down it would start the browser's own scrolling.
  const onMouseDown = (event: MouseEvent<HTMLDivElement>) => {
    if (event.button === 1 && tabAt(event.target, store.getState().panels) !== undefined) event.preventDefault();
  };
  const onAuxClick = (event: MouseEvent<HTMLDivElement>) => {
    const tab = event.button === 1 ? tabAt(event.target, store.getState().panels) : undefined;
    if (tab === undefined) return;
    event.preventDefault();
    run(CLOSE, tab.panel);
  };

  const menuPanel = menuAt === undefined ? undefined : panels.find((p) => p.id === menuAt.panelId);
  const closeMenu = () => {
    if (menuAt?.keyboard === true) keyboardTo.current = { tab: menuAt.panelId };
    setMenuAt(undefined);
  };

  return (
    // The tabs' and the areas' own events reach dockview through here: the keys and presses above are taken first.
    <div ref={root} className="qube-areas" data-areas={areaCount} onKeyDownCapture={onKeyDownCapture} onContextMenu={onContextMenu}
      onPointerDownCapture={onPointerDownCapture} onMouseDown={onMouseDown} onAuxClick={onAuxClick}>
      <DockviewReact components={COMPONENTS} tabComponents={TABS} defaultTabComponent={AreaTab} rightHeaderActionsComponent={AreaActions}
        onReady={onReady} theme={THEME} disableTabsOverflowList groupIdGenerator={nextAreaId} floatingGroupBounds="boundedWithinViewport" />
      {menuAt !== undefined && menuPanel !== undefined && (
        <TabMenu key={`${menuAt.panelId} ${menuAt.x} ${menuAt.y}`} at={menuAt} panel={menuPanel} run={run} onClose={closeMenu} />
      )}
    </div>
  );
}
