import { useCallback, useId, useMemo, useState, type ReactNode } from 'react';
import { Button, Group, Menu, ScrollArea, Text, Tooltip, useMantineTheme, type MenuProps } from '@mantine/core';
import { IconCheck, IconChevronRight, IconLayoutList, IconPin } from '@tabler/icons-react';
import { ViewRenderer } from '../platform/views/ViewRenderer';
import { useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { panelLabel, panelName, type Panel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { AppPanel } from '../apps';
import { RunPanel } from './RunPanel';
import { registerGridFocus } from './places';
import { ICON, STROKE } from './icons';
import { FLOAT_PLACES, SIDES, useArrangementVersion } from './arrangement';
import {
  CLOSE, CLOSE_ALL, CLOSE_OTHERS, CLOSE_RIGHT, DOCK, FLOAT, GATHER, MAXIMIZE, MOVE, onNow, otherAreas, PIN, POP_OUT, RENAME, REOPEN, runStripVerb,
  runVerb, SEND, SIZE, SPLIT, UNMAXIMIZE, UNPIN, type On, type PanelVerb,
} from './panelVerbs';
import { openLineWith } from './CommandPalette';
import { UnavailableMenuItem } from './UnavailableMenuItem';
import { PanelPage } from '../platform/panelPage';
import { allWindows } from './windows';
import { pixels } from '../platform/themeLength';

// What the strip and the areas share (ADR-0006 note 7, ADR-0018 §3): a panel's page, the verbs of a panel as menu
// items, the list of a strip's panels, and a tab's menu. Each item runs a verb of `panelVerbs`, so a menu can do no
// more and no less than the line.

type Args = Readonly<Record<string, string>>;

/** Runs a verb on a panel, a refusal said in a notice. */
export type RunVerb = (v: PanelVerb, panel: Panel, args?: Args) => void;

/** Runs the verbs on the store's panels as they stand and the arrangement drawn now, as the line does. */
export function useRunVerb(): RunVerb {
  const { store, notify } = useWorkspaceContext();
  return useCallback<RunVerb>((v, panel, args = {}) => runVerb(v, onNow(store.getState()), panel, notify, args), [store, notify]);
}

/** Where a tab's menu is open: on which panel, and where on the screen its pointer or its tab was. */
export interface MenuAt {
  readonly panelId: string;
  readonly x: number;
  readonly y: number;
  /** Opened from the keyboard: the keyboard goes back to the tab when it closes. */
  readonly keyboard: boolean;
}

/**
 * A panel's tab, in the strip or in an area: what the keyboard goes back to once the line has named the panel. Rename is
 * reached from a tab, or from the list of the panel an area or the strip shows, so its tab is drawn.
 */
function tabOf(panelId: string): HTMLElement {
  // A tab in another window is in that window's document (ADR-0018 §9).
  const tab = allWindows()
    .map((w) => w.document.querySelector(`[data-panel="${CSS.escape(panelId)}"]`)?.closest<HTMLElement>('[role="tab"]'))
    .find((t) => t !== null && t !== undefined);
  if (tab === null || tab === undefined) throw new Error(`The tab of ${panelId} is not drawn.`);
  return tab;
}

/**
 * Opens the line on `rename panel` with the panel's name on it and selected, as a field for a name opens on the name
 * (ADR-0006 note 9): typing replaces it, and Enter names the panel. The panel is shown first, as the line acts on the
 * panel shown, and the keyboard goes back to its tab as the line closes. A tab's Rename, its double click and its F2
 * all open it so, and the line is where the name is typed: there is no field of a tab's own.
 */
export function renameOnLine(state: Pick<WorkspaceState, 'activate'>, panel: Panel): void {
  // The tab first: a tab not drawn is a defect, which leaves no other panel shown than before.
  const returnTo = tabOf(panel.id);
  state.activate(panel.id);
  const words = `${RENAME.words} `;
  const name = panelName(panel);
  openLineWith(`${words}${name}`, { selected: { start: words.length, end: words.length + name.length }, returnTo });
}

const SHARES = ['25%', '33%', '50%', '67%', '75%'] as const;

/**
 * A panel's menu is held to the height its window has below or above where it opens, the theme's `sm` short of its
 * edge, and scrolls inside it (`SCROLLS`): the menu of every verb is taller than a short window, which another window of
 * the browser often is (ADR-0018 §9), and its last items were out of reach there.
 */
function useWithinTheWindow(): NonNullable<MenuProps['middlewares']> {
  const edge = pixels(useMantineTheme().spacing.sm);
  return useMemo(() => ({ flip: true, shift: true, size: { padding: edge } }), [edge]);
}
const SCROLLS = { overflowY: 'auto' } as const;
const capital = (word: string) => `${word.charAt(0).toUpperCase()}${word.slice(1)}`;

/**
 * The verbs of one panel as menu items, each with the words the line takes for it, so the menu teaches the command
 * (docs/design-guidelines.md §5). An item that cannot act now is dimmed with why under it, and stays in the arrows'
 * reach: a phone reads the reason without a hover, and no notice is raised over the strip to say it. Where the panels
 * stand in areas, the verbs that arrange follow note 7's, those that take a choice as a submenu of its choices.
 */
export function VerbItems({ panel, run, fromKeyboard = false }: {
  readonly panel: Panel;
  readonly run: RunVerb;
  /** Opened from the keyboard: the first item takes it, as a menu opened with Shift+F10 does. */
  readonly fromKeyboard?: boolean;
}) {
  const { store, notify } = useWorkspaceContext();
  // Drawn again as the arrangement changes under an open menu, so what it says can be done can be.
  useArrangementVersion();
  const on: On = onNow(store.getState());
  const said = (words: string) => <Text size="xs" c="dimmed" ff="monospace" ml="md">{words}</Text>;
  const item = (key: string, label: string, words: string, why: string | undefined, act: () => void, autofocus = false): ReactNode =>
    why === undefined
      ? <Menu.Item key={key} data-autofocus={autofocus ? true : undefined} rightSection={said(words)} onClick={act}>{label}</Menu.Item>
      : <UnavailableMenuItem key={key} why={why} rightSection={said(words)}>{label}</UnavailableMenuItem>;
  const onPanel = (v: PanelVerb, label = v.label, args?: Args) => {
    const given = args === undefined ? undefined : Object.values(args).join(' ');
    return item(`${v.id}${given ?? ''}`, label, given === undefined ? v.words : `${v.words} ${given}`, v.unavailable(on, panel, args), () => run(v, panel, args), fromKeyboard && v === CLOSE);
  };
  // A verb that takes a choice: its choices in a submenu, or the verb dimmed with why where it cannot act at all.
  const choosing = (v: PanelVerb, choices: readonly { readonly label: string; readonly args: Args }[]) => {
    const why = v.unavailable(on, panel);
    if (why !== undefined) return <UnavailableMenuItem key={v.id} why={why} rightSection={said(v.words)}>{v.label}</UnavailableMenuItem>;
    return (
      <Menu.Sub key={v.id}>
        <Menu.Sub.Target>
          <Menu.Sub.Item rightSection={<Group gap="xs" wrap="nowrap">{said(v.words)}<IconChevronRight size={ICON} stroke={STROKE} aria-hidden /></Group>}>
            {v.label}
          </Menu.Sub.Item>
        </Menu.Sub.Target>
        <Menu.Sub.Dropdown>{choices.map((c) => onPanel(v, c.label, c.args))}</Menu.Sub.Dropdown>
      </Menu.Sub>
    );
  };
  const arranging = on.areas.strip === undefined;
  const away = arranging && on.areas.areas().some((a) => a.where !== 'docked' && a.panels.includes(panel.id));
  return (
    <>
      {[CLOSE, CLOSE_OTHERS, CLOSE_RIGHT, CLOSE_ALL].map((v) => onPanel(v))}
      <Menu.Divider />
      {onPanel(panel.pinned === true ? UNPIN : PIN)}
      {onPanel(MOVE, 'Move left', { where: 'left' })}
      {onPanel(MOVE, 'Move right', { where: 'right' })}
      {/* Opens the line with the name to type over: the words beside it are those it puts there (ADR-0006 note 9). */}
      {item(RENAME.id, `${RENAME.label}…`, RENAME.words, RENAME.unavailable(on, panel), () => renameOnLine(store.getState(), panel))}
      {arranging && (
        <>
          <Menu.Divider />
          {choosing(SPLIT, SIDES.map((side) => ({ label: capital(side), args: { side } })))}
          {choosing(SEND, otherAreas(on, panel.id).map((a) => {
            const shown = store.getState().panels.find((p) => p.id === a.shown);
            if (shown === undefined) throw new Error(`The area ${a.id} shows ${a.shown}, which is not open.`);
            return { label: panelLabel(shown), args: { panel: panelLabel(shown) } };
          }))}
          {/* A floating panel's Float places its area (ADR-0018 §3): the menu holds every verb, as a finger has no drag. */}
          {choosing(FLOAT, FLOAT_PLACES.map((where) => ({ label: capital(where), args: { where } })))}
          {onPanel(POP_OUT)}
          {away && onPanel(DOCK)}
          {choosing(SIZE, SHARES.map((percent) => ({ label: percent, args: { percent } })))}
          {on.areas.maximized() === undefined ? onPanel(MAXIMIZE) : onPanel(UNMAXIMIZE, 'Restore')}
          {onPanel(GATHER)}
        </>
      )}
      <Menu.Divider />
      {item(REOPEN.id, REOPEN.label, REOPEN.words, REOPEN.unavailable(on), () => runStripVerb(REOPEN, onNow(store.getState()), notify))}
    </>
  );
}

/**
 * The panels of one strip, at its end: the panels it has no room for, on a phone most of them. Choosing one shows it;
 * under the list are the verbs of the panel the strip shows, which a touch screen has no right click for.
 */
export function PanelList({ panels, shown, run, name }: {
  readonly panels: readonly Panel[];
  /** The panel the strip shows. */
  readonly shown: Panel | undefined;
  readonly run: RunVerb;
  /** What the list is of: every panel, or an area's. */
  readonly name: string;
}) {
  const { store } = useWorkspaceContext();
  // The tooltip names the button until the list opens over where it would be.
  const [opened, setOpened] = useState(false);
  const withinTheWindow = useWithinTheWindow();
  return (
    <div className="qube-panel-list">
      {/* Named by its button, as a menu button's menu is. */}
      {/*
        Closed, it gives the keyboard back to its button, 10ms on, where nothing has taken it meanwhile: the line its
        Rename opens has, as its field takes the keyboard at once (Mantine's focus trap, on the next turn).
      */}
      <Menu opened={opened} onChange={setOpened} position="bottom-end" shadow="md" withinPortal middlewares={withinTheWindow}>
        <Tooltip label={`${name} (show panel)`} disabled={opened}>
          <Menu.Target>
            <Button size="xs" variant="subtle" color="gray" aria-label={`${name} (${panels.length})`}
              leftSection={<IconLayoutList size={ICON} stroke={STROKE} aria-hidden />}>
              {panels.length}
            </Button>
          </Menu.Target>
        </Tooltip>
        <Menu.Dropdown style={SCROLLS}>
          <Menu.Label>Panels</Menu.Label>
          {/* A long list scrolls on its own, so that the verbs below it are in reach without scrolling past it. */}
          <ScrollArea.Autosize mah="50vh" type="auto">
            {panels.map((p) => (
              <Menu.Item key={p.id} onClick={() => store.getState().activate(p.id)} aria-current={p.id === shown?.id ? 'page' : undefined}
                leftSection={p.id === shown?.id ? <IconCheck size={ICON} stroke={STROKE} aria-hidden /> : <span style={{ display: 'inline-block', width: ICON }} />}
                rightSection={p.pinned === true ? <IconPin size={ICON} stroke={STROKE} aria-label="pinned" /> : undefined}>
                {panelLabel(p)}
              </Menu.Item>
            ))}
          </ScrollArea.Autosize>
          {shown !== undefined && (
            <>
              <Menu.Divider />
              <Menu.Label>{panelLabel(shown)}</Menu.Label>
              <VerbItems panel={shown} run={run} />
            </>
          )}
        </Menu.Dropdown>
      </Menu>
    </div>
  );
}

/**
 * A tab's menu, made where it opens (its parent keys it by where), its target already at the pointer or under the tab: one moved after the menu has
 * placed itself is not followed, and the menu would stay hidden at the corner it was first placed by. A key outside it
 * does not close it, as Mantine's Menu has it by default: the Shift+F10 that opens it is such a key, and would close it
 * as it opened. The keyboard is in the menu while it is open: on its first item where a key opened it, on none where the
 * pointer did. Its target is never hidden by a scroll, being fixed where it opened: one on the window's left edge (the
 * first tab of an area there, at Shift+F10) counts as wholly clipped to Mantine's check of a detached target, which would
 * draw the menu hidden, so the menu does not make that check.
 */
export function TabMenu({ at, panel, run, onClose }: {
  readonly at: MenuAt;
  readonly panel: Panel;
  readonly run: RunVerb;
  readonly onClose: () => void;
}) {
  const label = useId();
  const withinTheWindow = useWithinTheWindow();
  return (
    <Menu opened onClose={onClose} position="bottom-start" shadow="md" withinPortal hideDetached={false} middlewares={withinTheWindow}
      returnFocus={false} trapFocus withInitialFocusPlaceholder={!at.keyboard} clickOutsideEvents={['mousedown', 'touchstart']}>
      <Menu.Target>
        <div aria-hidden style={{ position: 'fixed', left: at.x, top: at.y, width: 0, height: 0 }} />
      </Menu.Target>
      {/* Named by its label, which says on which panel it acts: its target is a mark of where it opened, with no name. */}
      {/* The menu key opens the browser's own menu as it is let go (Windows), where the keyboard is already in this one. */}
      <Menu.Dropdown aria-labelledby={label} onContextMenu={(e) => e.preventDefault()} style={SCROLLS}>
        <Menu.Label id={label}>{panelLabel(panel)}</Menu.Label>
        <VerbItems panel={panel} run={run} fromKeyboard={at.keyboard} />
      </Menu.Dropdown>
    </Menu>
  );
}

/** A panel's page: a Run, an app's page, or a view of data, laid out by the panel's width (ADR-0018 §5). */
export function PanelContent({ panel }: { readonly panel: Panel }) {
  return <PanelPage panelId={panel.id}><PageOf panel={panel} /></PanelPage>;
}

function PageOf({ panel }: { readonly panel: Panel }) {
  const registerFocus = useCallback((focus: (() => void) | undefined) => registerGridFocus(panel.id, focus), [panel.id]);
  if (panel.kind === 'run') return <RunPanel panel={panel} />;
  if (panel.kind === 'app') return <AppPanel panel={panel} />;
  return <ViewRenderer view={panel.view} outputs={{}} registerFocus={registerFocus} />;
}
