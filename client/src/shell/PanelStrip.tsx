import { useCallback, useEffect, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent } from 'react';
import { CloseButton, ScrollArea, Tabs, Tooltip } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { IconPin } from '@tabler/icons-react';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { panelLabel, type Panel } from '../platform/workspace/workspaceStore';
import { ICON, STROKE } from './icons';
import { registerArrangement, stripArrangement } from './arrangement';
import type { Size } from './layout';
import { CLOSE } from './panelVerbs';
import { PanelContent, PanelList, renameOnLine, TabMenu, useRunVerb, type MenuAt } from './panelMenus';
import { lineIsOpen } from './CommandPalette';

// The panels under one strip of tabs (ADR-0006 note 7 of 2026-11-12): what a phone shows, and what a wider window shows
// where its arrangement of areas has no room (ADR-0018 §5). The strip is one row that scrolls sideways and keeps the
// active tab in sight; the list at its end names every panel. A tab's menu, the list and the keys of a tab run the verbs
// `panelVerbs` declares, which the command line runs too; those that arrange are refused here, saying why. Only the
// active panel's page is mounted, so the screen's cost does not grow with the panels open.

export function PanelStrip({ room, why }: {
  readonly room: Size;
  /** Why the panels are one strip, said where a verb that arranges is refused. */
  readonly why: string;
}) {
  const panels = useWorkspace((s) => s.panels);
  const activePanelId = useWorkspace((s) => s.activePanelId);
  const { store } = useWorkspaceContext();
  const run = useRunVerb();
  const list = useRef<HTMLDivElement>(null);
  const [menuAt, setMenuAt] = useState<MenuAt | undefined>(undefined);
  const [dropOn, setDropOn] = useState<string | undefined>(undefined);
  const dragged = useRef<string | undefined>(undefined);
  // Where the keyboard goes once the strip has drawn what a key did: the tab shown next where Delete closed one, as a
  // tab list's Delete has it (WAI-ARIA); the tab a menu opened from the keyboard was on, or the tab shown next where the
  // menu closed that one. Read after the strip has drawn, so a tab just closed is no longer there to take it.
  const keyboardTo = useRef<{ readonly tab: string | undefined } | undefined>(undefined);
  const viewport = useRef<HTMLDivElement>(null);

  // The verbs read this strip's port while it is drawn: there are no areas to change.
  const { width, height } = room;
  useEffect(() => registerArrangement(stripArrangement({ width, height }, why)), [width, height, why]);

  // The strip's width, as the browser reports it when it changes: a narrower window keeps the active tab in sight too.
  const { ref: stripRef, width: stripWidth } = useElementSize<HTMLDivElement>();
  const tabOf = useCallback((panelId: string) => list.current?.querySelector<HTMLElement>(`[data-panel="${panelId}"]`) ?? null, []);
  const order = panels.map((p) => p.id).join(' ');

  // The active tab is kept in sight: a panel just opened, shown from the list or by `next panel`, or moved, may be past
  // the edge, and so may the active tab of a strip the window has narrowed. Only the strip scrolls, sideways: the page
  // under it stays where the user scrolled it.
  useEffect(() => {
    const tab = activePanelId === undefined ? null : tabOf(activePanelId);
    const strip = viewport.current;
    if (tab === null || strip === null) return;
    const [t, v] = [tab.getBoundingClientRect(), strip.getBoundingClientRect()];
    if (t.left < v.left) strip.scrollLeft -= v.left - t.left;
    else if (t.right > v.right) strip.scrollLeft += t.right - v.right;
  }, [activePanelId, order, stripWidth, tabOf]);

  useEffect(() => {
    const to = keyboardTo.current;
    if (to === undefined) return;
    keyboardTo.current = undefined;
    // A menu's Rename gave the keyboard to the line, which gives it back to the tab as it closes.
    if (lineIsOpen()) return;
    const shown = store.getState().activePanelId;
    const tab = (to.tab === undefined ? null : tabOf(to.tab)) ?? (shown === undefined ? null : tabOf(shown));
    tab?.focus();
  });

  const onKeyDown = (panel: Panel) => (event: KeyboardEvent<HTMLButtonElement>) => {
    if (event.key === 'Delete') {
      event.preventDefault();
      keyboardTo.current = { tab: undefined };
      run(CLOSE, panel);
    } else if (event.key === 'F2') {
      // F2 renames, as it does a file or a symbol (ADR-0006 note 9).
      event.preventDefault();
      renameOnLine(store.getState(), panel);
    } else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const box = event.currentTarget.getBoundingClientRect();
      setMenuAt({ panelId: panel.id, x: box.left, y: box.bottom, keyboard: true });
    }
  };
  const onContextMenu = (panel: Panel) => (event: MouseEvent<HTMLButtonElement>) => {
    event.preventDefault();
    // The menu key on Windows also sends this as it is let go, after its keydown opened the menu from the keyboard; the
    // menu that key opened stays, whether or not the keyboard has reached it yet.
    setMenuAt((open) => (open?.keyboard === true && open.panelId === panel.id
      ? open
      : { panelId: panel.id, x: event.clientX, y: event.clientY, keyboard: false }));
  };
  // A double click on a tab renames it, as a sheet's tab is renamed (ADR-0006 note 9); not one on its close button.
  const onDoubleClick = (panel: Panel) => (event: MouseEvent<HTMLButtonElement>) => {
    if (event.target instanceof Element && event.target.closest('.qube-panel-tab-close') !== null) return;
    renameOnLine(store.getState(), panel);
  };
  // The middle button closes a tab, as a browser's does; held down it would start the browser's own scrolling.
  const onMouseDown = (event: MouseEvent<HTMLButtonElement>) => { if (event.button === 1) event.preventDefault(); };
  const onAuxClick = (panel: Panel) => (event: MouseEvent<HTMLButtonElement>) => {
    if (event.button !== 1) return;
    event.preventDefault();
    run(CLOSE, panel);
  };

  // A tab dragged onto another takes its place; the store keeps the pinned and the others apart.
  const onDragStart = (panel: Panel) => (event: DragEvent<HTMLButtonElement>) => {
    dragged.current = panel.id;
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', panelLabel(panel));
  };
  const onDragOver = (panel: Panel) => (event: DragEvent<HTMLButtonElement>) => {
    if (dragged.current === undefined) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
    if (dropOn !== panel.id) setDropOn(panel.id);
  };
  const onDrop = (panel: Panel) => (event: DragEvent<HTMLButtonElement>) => {
    const moving = dragged.current;
    dragged.current = undefined;
    setDropOn(undefined);
    if (moving === undefined || moving === panel.id) return;
    event.preventDefault();
    const to = store.getState().panels.findIndex((p) => p.id === panel.id);
    if (to < 0) throw new Error(`A tab was dropped on ${panel.id}, which is not open.`);
    store.getState().movePanel(moving, to);
  };
  const onDragEnd = () => { dragged.current = undefined; setDropOn(undefined); };

  const menuPanel = menuAt === undefined ? undefined : panels.find((p) => p.id === menuAt.panelId);
  const closeMenu = () => {
    // Opened from the keyboard, the menu gives it back to its tab, or to the tab shown next where it closed its own.
    if (menuAt?.keyboard === true) keyboardTo.current = { tab: menuAt.panelId };
    setMenuAt(undefined);
  };

  return (
    <Tabs value={activePanelId ?? null} onChange={(id) => id !== null && store.getState().activate(id)} keepMounted={false} variant="outline">
      <div className="qube-panel-strip" ref={stripRef}>
        <ScrollArea type="auto" scrollbarSize={6} className="qube-panel-strip-scroll" viewportRef={viewport}>
          <Tabs.List ref={list} style={{ flexWrap: 'nowrap' }}>
            {panels.map((p) => {
              const label = panelLabel(p);
              return (
                <Tabs.Tab key={p.id} value={p.id} data-panel={p.id} className="qube-panel-tab" draggable
                  {...(dropOn === p.id ? { 'data-drop': true } : {})} {...(p.pinned === true ? { 'data-pinned': true } : {})}
                  aria-haspopup="menu" onKeyDown={onKeyDown(p)} onContextMenu={onContextMenu(p)} onDoubleClick={onDoubleClick(p)} onMouseDown={onMouseDown}
                  onAuxClick={onAuxClick(p)} onDragStart={onDragStart(p)} onDragOver={onDragOver(p)} onDrop={onDrop(p)} onDragEnd={onDragEnd}
                  leftSection={p.pinned === true ? <IconPin size={ICON} stroke={STROKE} aria-label="pinned" /> : undefined}
                  rightSection={p.pinned === true ? undefined : (
                    <Tooltip label="Close (close panel)">
                      <CloseButton size="xs" component="span" className="qube-panel-tab-close" aria-label={`Close ${label}`} onClick={(e) => { e.stopPropagation(); run(CLOSE, p); }} />
                    </Tooltip>
                  )}>
                  <span className="qube-panel-tab-title" title={label}>{label}</span>
                </Tabs.Tab>
              );
            })}
          </Tabs.List>
        </ScrollArea>
        <PanelList panels={panels} shown={panels.find((p) => p.id === activePanelId)} run={run} name="All panels" />
      </div>
      {menuAt !== undefined && menuPanel !== undefined && (
        <TabMenu key={`${menuAt.panelId} ${menuAt.x} ${menuAt.y}`} at={menuAt} panel={menuPanel} run={run} onClose={closeMenu} />
      )}
      {panels.map((p) => (
        <Tabs.Panel key={p.id} value={p.id} pt="sm"><PanelContent panel={p} /></Tabs.Panel>
      ))}
    </Tabs>
  );
}
