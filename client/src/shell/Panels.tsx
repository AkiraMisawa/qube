import { useCallback, useEffect, useId, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react';
import { Button, CloseButton, Kbd, Menu, ScrollArea, Stack, Tabs, Text, Tooltip } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { IconCheck, IconLayoutList, IconPin } from '@tabler/icons-react';
import { ViewRenderer } from '../platform/views/ViewRenderer';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { panelLabel, type Panel } from '../platform/workspace/workspaceStore';
import { AppPanel } from '../apps';
import { RunPanel } from './RunPanel';
import { registerGridFocus } from './places';
import { CLOSE, CLOSE_ALL, CLOSE_OTHERS, CLOSE_RIGHT, MOVE, PIN, REOPEN, runStripVerb, runVerb, UNPIN, type PanelVerb } from './panelVerbs';

// The panels under their tabs (ADR-0006 note 7 of 2026-11-12). The strip is one row that scrolls sideways, as before,
// and keeps the active tab in sight; the list at its end names every panel, for the panels the row has no room for and
// for a phone. A tab's menu, the list and the keys of a tab run the verbs `panelVerbs` declares, which the command line
// runs too. Only the active panel's page is mounted, so the screen's cost does not grow with the panels open.

/** Where a tab's menu is open: on which panel, and where on the screen its pointer or its tab was. */
interface MenuAt {
  readonly panelId: string;
  readonly x: number;
  readonly y: number;
  /** Opened from the keyboard: the keyboard goes back to the tab when it closes. */
  readonly keyboard: boolean;
}

export function Panels() {
  const panels = useWorkspace((s) => s.panels);
  const activePanelId = useWorkspace((s) => s.activePanelId);
  const workspace = useWorkspaceContext();
  const { store, notify } = workspace;
  const list = useRef<HTMLDivElement>(null);
  const [menuAt, setMenuAt] = useState<MenuAt | undefined>(undefined);
  const [dropOn, setDropOn] = useState<string | undefined>(undefined);
  const dragged = useRef<string | undefined>(undefined);
  // Where the keyboard goes once the strip has drawn what a key did: the tab shown next where Delete closed one, as a
  // tab list's Delete has it (WAI-ARIA); the tab a menu opened from the keyboard was on, or the tab shown next where the
  // menu closed that one. Read after the strip has drawn, so a tab just closed is no longer there to take it.
  const keyboardTo = useRef<{ readonly tab: string | undefined } | undefined>(undefined);
  const viewport = useRef<HTMLDivElement>(null);

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
    const shown = store.getState().activePanelId;
    const tab = (to.tab === undefined ? null : tabOf(to.tab)) ?? (shown === undefined ? null : tabOf(shown));
    tab?.focus();
  });

  const menuLabel = useId();

  const run = useCallback<RunVerb>((v, panel, args = {}) => runVerb(v, store.getState(), panel, notify, args), [store, notify]);

  if (panels.length === 0)
    return (
      <Stack align="center" mt="xl" gap="xs">
        <Text c="dimmed">Nothing open yet.</Text>
        {/* The key is said where a keyboard is likely (docs/design-guidelines.md §8); a phone opens the line from the header. */}
        <Text c="dimmed" size="sm"><Text span inherit visibleFrom="sm">Press <Kbd>Ctrl</Kbd> + <Kbd>K</Kbd></Text><Text span inherit hiddenFrom="sm">Open Commands</Text> and type what you want to do: <Kbd>new rates</Kbd>, <Kbd>new xva</Kbd>, <Kbd>open pricing db</Kbd>, or <Kbd>?</Kbd> for every command.</Text>
      </Stack>
    );

  const onKeyDown = (panel: Panel) => (event: KeyboardEvent<HTMLButtonElement>) => {
    if (event.key === 'Delete') {
      event.preventDefault();
      keyboardTo.current = { tab: undefined };
      run(CLOSE, panel);
    } else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const box = event.currentTarget.getBoundingClientRect();
      setMenuAt({ panelId: panel.id, x: box.left, y: box.bottom, keyboard: true });
    }
  };
  const onContextMenu = (panel: Panel) => (event: MouseEvent<HTMLButtonElement>) => {
    event.preventDefault();
    setMenuAt({ panelId: panel.id, x: event.clientX, y: event.clientY, keyboard: false });
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
                  aria-haspopup="menu" onKeyDown={onKeyDown(p)} onContextMenu={onContextMenu(p)} onMouseDown={onMouseDown}
                  onAuxClick={onAuxClick(p)} onDragStart={onDragStart(p)} onDragOver={onDragOver(p)} onDrop={onDrop(p)} onDragEnd={onDragEnd}
                  leftSection={p.pinned === true ? <IconPin size={16} stroke={1.75} aria-label="pinned" /> : undefined}
                  rightSection={p.pinned === true ? undefined : (
                    <Tooltip label="Close (close panel)">
                      <CloseButton size="xs" component="span" aria-label={`Close ${label}`} onClick={(e) => { e.stopPropagation(); run(CLOSE, p); }} />
                    </Tooltip>
                  )}>
                  <span className="qube-panel-tab-title" title={label}>{label}</span>
                </Tabs.Tab>
              );
            })}
          </Tabs.List>
        </ScrollArea>
        <PanelList panels={panels} activePanelId={activePanelId} run={run} />
      </div>
      {/* The menu is made where it opens, its target already at the pointer or under the tab: one moved after the menu
          has placed itself is not followed, and the menu would stay hidden at the corner it was first placed by. A key
          outside it does not close it, as Mantine's Menu has it by default: the Shift+F10 that opens it is such a key,
          and would close it as it opened. The keyboard is in the menu while it is open: on its first item where a key
          opened it, on none where the pointer did. */}
      {menuAt !== undefined && menuPanel !== undefined && (
        <Menu key={`${menuAt.panelId} ${menuAt.x} ${menuAt.y}`} opened onClose={closeMenu} position="bottom-start" shadow="md" withinPortal
          returnFocus={false} trapFocus withInitialFocusPlaceholder={!menuAt.keyboard} clickOutsideEvents={['mousedown', 'touchstart']}>
          <Menu.Target>
            <div aria-hidden style={{ position: 'fixed', left: menuAt.x, top: menuAt.y, width: 0, height: 0 }} />
          </Menu.Target>
          {/* Named by its label, which says on which panel it acts: its target is a mark of where it opened, with no name. */}
          {/* The menu key opens the browser's own menu as it is let go (Windows), where the keyboard is in this one. */}
          <Menu.Dropdown aria-labelledby={menuLabel} onContextMenu={(e) => e.preventDefault()}>
            <Menu.Label id={menuLabel}>{panelLabel(menuPanel)}</Menu.Label>
            <VerbItems panel={menuPanel} run={run} fromKeyboard={menuAt.keyboard} />
          </Menu.Dropdown>
        </Menu>
      )}
      {panels.map((p) => (
        <Tabs.Panel key={p.id} value={p.id} pt="sm"><PanelContent panel={p} /></Tabs.Panel>
      ))}
    </Tabs>
  );
}

/**
 * Marks a menu item `aria-disabled` while it cannot act, and takes the mark off when it can. Mantine's item writes that
 * attribute from its own `disabled` alone, which would also take the item out of the arrows' reach; a new function at
 * each drawing is called with the item each time, so the mark follows the reason.
 */
const unavailableMark = (why: string | undefined) => (item: HTMLButtonElement | null) => {
  if (item === null) return;
  if (why === undefined) item.removeAttribute('aria-disabled');
  else item.setAttribute('aria-disabled', 'true');
};

/** Runs a verb on a panel, a refusal said in a notice. */
type RunVerb = (v: PanelVerb, panel: Panel, args?: Readonly<Record<string, string>>) => void;

/**
 * The verbs of one panel as menu items, each with the words the line takes for it, so the menu teaches the command
 * (docs/design-guidelines.md §5). An item that cannot act now is shown disabled and says why: in its title to the
 * pointer, and in a refused notice when it is chosen. It stays in the keys' reach (WAI-ARIA's menu pattern keeps a
 * disabled item focusable), so the keyboard and a touch screen are told why too, where a disabled Mantine item is
 * skipped by the arrows and has no hover on a phone.
 */
function VerbItems({ panel, run, fromKeyboard = false }: {
  readonly panel: Panel;
  readonly run: RunVerb;
  /** Opened from the keyboard: the first item takes it, as a menu opened with Shift+F10 does. */
  readonly fromKeyboard?: boolean;
}) {
  const { store, notify } = useWorkspaceContext();
  const state = store.getState();
  const item = (key: string, label: string, words: string, why: string | undefined, act: () => void, autofocus = false): ReactNode => (
    <Menu.Item key={key} ref={unavailableMark(why)} className={why === undefined ? undefined : 'qube-menu-unavailable'}
      title={why} data-autofocus={autofocus ? true : undefined}
      rightSection={<Text size="xs" c="dimmed" ff="monospace" ml="md">{words}</Text>} onClick={act}>
      {label}
    </Menu.Item>
  );
  const onPanel = (v: PanelVerb, label = v.label, args?: Readonly<Record<string, string>>) =>
    item(`${v.id}${args?.where ?? ''}`, label, args?.where === undefined ? v.words : `${v.words} ${args.where}`, v.unavailable(state, panel, args), () => run(v, panel, args), fromKeyboard && v === CLOSE);
  return (
    <>
      {[CLOSE, CLOSE_OTHERS, CLOSE_RIGHT, CLOSE_ALL].map((v) => onPanel(v))}
      <Menu.Divider />
      {onPanel(panel.pinned === true ? UNPIN : PIN)}
      {onPanel(MOVE, 'Move left', { where: 'left' })}
      {onPanel(MOVE, 'Move right', { where: 'right' })}
      <Menu.Divider />
      {item(REOPEN.id, REOPEN.label, REOPEN.words, REOPEN.unavailable(state), () => runStripVerb(REOPEN, store.getState(), notify))}
    </>
  );
}

/**
 * Every open panel, at the end of the strip: the panels it has no room for, on a phone most of them. Choosing one shows
 * it; under the list are the active panel's verbs, which a touch screen has no right click for.
 */
function PanelList({ panels, activePanelId, run }: { readonly panels: readonly Panel[]; readonly activePanelId: string | undefined; readonly run: RunVerb }) {
  const { store } = useWorkspaceContext();
  const active = panels.find((p) => p.id === activePanelId);
  // The tooltip names the button until the list opens over where it would be.
  const [opened, setOpened] = useState(false);
  return (
    <div className="qube-panel-list">
      {/* Named by its button, as a menu button's menu is. */}
      <Menu opened={opened} onChange={setOpened} position="bottom-end" shadow="md" withinPortal>
        <Tooltip label="All panels (show panel)" disabled={opened}>
          <Menu.Target>
            <Button size="xs" variant="subtle" color="gray" aria-label={`All panels (${panels.length})`}
              leftSection={<IconLayoutList size={16} stroke={1.75} aria-hidden />}>
              {panels.length}
            </Button>
          </Menu.Target>
        </Tooltip>
        <Menu.Dropdown>
          <Menu.Label>Panels</Menu.Label>
          <ScrollArea.Autosize mah="50vh" type="auto">
            {panels.map((p) => (
              <Menu.Item key={p.id} onClick={() => store.getState().activate(p.id)} aria-current={p.id === activePanelId ? 'page' : undefined}
                leftSection={p.id === activePanelId ? <IconCheck size={14} stroke={1.75} aria-hidden /> : <span className="qube-panel-list-mark" />}
                rightSection={p.pinned === true ? <IconPin size={16} stroke={1.75} aria-label="pinned" /> : undefined}>
                {panelLabel(p)}
              </Menu.Item>
            ))}
          </ScrollArea.Autosize>
          {active !== undefined && (
            <>
              <Menu.Divider />
              <Menu.Label>{panelLabel(active)}</Menu.Label>
              <VerbItems panel={active} run={run} />
            </>
          )}
        </Menu.Dropdown>
      </Menu>
    </div>
  );
}

function PanelContent({ panel }: { readonly panel: Panel }) {
  const registerFocus = useCallback((focus: (() => void) | undefined) => registerGridFocus(panel.id, focus), [panel.id]);
  if (panel.kind === 'run') return <RunPanel panel={panel} />;
  if (panel.kind === 'app') return <AppPanel panel={panel} />;
  return <ViewRenderer view={panel.view} outputs={{}} registerFocus={registerFocus} />;
}
