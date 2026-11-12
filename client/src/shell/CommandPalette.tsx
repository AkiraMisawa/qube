import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Badge, Box, Group, Kbd, Modal, Stack, Text, TextInput, UnstyledButton, useMantineColorScheme } from '@mantine/core';
import { useHotkeys } from '@mantine/hooks';
import { IconCheck, IconX } from '@tabler/icons-react';
import {
  assertDistinct, CommandRefused, completionsOf, drawn, drawnSections, execute, finish, given, listed, placeholder, readLine, signature, take,
  type Command, type CommandDoor, type Completion,
} from '../platform/commands/commands';
import { argumentShown, argumentsDrawn, commandDoorOf, lineAsked, previewShown, screenShown, servedCommands, taken } from '../platform/commands/served';
import { recordUse, usageScore } from '../platform/commands/usage';
import { resetSetting, setSetting, useSettings, type SettingChanged, type SettingDescribed } from '../platform/settings/settings';
import { openSettings } from './SettingsDialog';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { Catalog, LineCompletion, LinePreview, LineView, RunRecord } from '../platform/contracts';
import type { Notice, Notify } from '../platform/notices';
import { viewsFor } from '../platform/views/binding';
import { CONTEXT_CONTRACT, useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { keyOf, panelLabel, type Panel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { holdKeyboard, keepKeyboard, keyboardAwaited, letGo, releaseKeyboard, rememberKeyboard, returnKeyboard } from './keyboard';
import { goTo, placesOf } from './places';
import { panelCommands, type PanelState } from './panelVerbs';
import { useStore } from 'zustand';
import { createStore } from 'zustand/vanilla';
import { useOptionalAiStore } from '../ai/AiProvider';
import { aiContextOf } from '../ai/effects';
import { appCommands } from '../apps';

/**
 * The commands of the user's settings (ADR-0017 §4), on every page, with or without a workspace: `settings` shows the
 * dialog, `settings <name> <value>` sets one a line types, completing its choices, and `settings reset <name>` returns
 * one the user set to its registered value. The server reads and checks the value, and what it answered is the notice.
 */

export function settingCommands(settings: readonly SettingDescribed[], notify: Notify): Command[] {
  const said = (act: Promise<SettingChanged>) =>
    act.then((changed) => notify('done', changed.said), (error: unknown) => notify('refused', `settings: ${error instanceof Error ? error.message : String(error)}`));
  return [
    { id: 'settings.open', words: 'settings', title: 'Show your settings', group: 'Settings', run: openSettings },
    ...settings.filter((s) => s.typed).map((setting): Command => ({
      id: `settings.set:${setting.key}`,
      words: `settings ${setting.name}`,
      title: `Set your ${setting.title.toLowerCase()}`,
      group: 'Settings',
      target: setting.value,
      args: [{
        name: 'value', required: true, hint: setting.takes,
        ...(setting.choices === undefined ? {} : { complete: () => (setting.choices ?? []).map((value) => ({ value })) }),
      }],
      run: (a) => { void said(setSetting(setting.key, given(a, 'value'))); },
    })),
    {
      id: 'settings.reset',
      words: 'settings reset',
      title: 'Reset one of your settings to its registered value',
      group: 'Settings',
      args: [{
        name: 'setting', required: true, rest: true, hint: 'a setting you set',
        complete: () => settings.filter((s) => s.set).map((s) => ({ value: s.name, detail: s.title })),
      }],
      run: (a) => { void said(resetSetting(given(a, 'setting'))); },
    },
  ];
}

/**
 * Builds every command the workbench offers right now. Workflows, views and context values come from
 * the catalog, so a module that registers a workflow gets its command without a client change.
 */
export function buildCommands(args: {
  readonly catalog: Catalog;
  readonly state: PanelState & Pick<WorkspaceState, 'context' | 'openRunDialog' | 'setPanelView' | 'requestContext' | 'showApp' | 'openAppDialog'>;
  readonly activeRun: RunRecord | undefined;
  readonly toggleTheme: () => void;
  readonly notify: Notify;
  /** The user's settings as read (ADR-0017 §4): `settings <name> <value>` for each a line types, and `settings reset <name>`. */
  readonly settings?: readonly SettingDescribed[];
  /** The AI pane, where one is mounted: `ai <request>` hands the request to it (ADR-0006 §7, ADR-0007 §7). */
  readonly ai?: { readonly ask: (request: string) => string | undefined; readonly open: boolean; readonly setOpen: (open: boolean) => void };
}): Command[] {
  const { catalog, state, activeRun } = args;
  const active: Panel | undefined = state.panels.find((p) => p.id === state.activePanelId);
  // The commands the catalog says how to type (ADR-0016 §4): the server reads their lines, for the workspace of the
  // page in the active panel.
  const served = servedCommands(catalog, active, commandDoorOf(active?.id), args.notify);
  // The applications' commands first: the empty line lists the groups in the order registered (ADR-0006's note 3 of
  // 2026-11-11), and what an application offers comes before the catalog's workflows and the shell's own. Of those, the
  // commands the page's application serves lead, in the order its module registers them (ADR-0016 note 33), so that the
  // line opens on what the page is for; the commands still written here follow, then the kernel's.
  const commands: Command[] = [
    ...served.filter((c) => c.served?.app !== undefined),
    ...appCommands(state),
    ...served.filter((c) => c.served?.app === undefined),
  ];

  for (const workflow of catalog.workflows)
    commands.push({
      id: `workflow.run:${keyOf(workflow)}`,
      words: `run ${wordsOf(workflow.title)}`,
      title: `Run ${workflow.title}`,
      group: 'Workflows',
      ...(state.context.prepared === undefined ? { unavailable: 'The context is not valid.' } : {}),
      run: () => state.openRunDialog(keyOf(workflow)),
    });

  if (active?.kind === 'run' && activeRun !== undefined && activeRun.status !== 'running')
    for (const view of viewsFor(activeRun.workflow, catalog.views))
      commands.push({
        id: `view.open:${keyOf(view)}`,
        words: `show ${wordsOf(view.title)}`,
        title: `Show as ${view.title}`,
        group: 'Views',
        // A Run's views are its page's commands: the empty line lists them first (ADR-0006's note 3 of 2026-11-11).
        target: panelLabel(active),
        page: true,
        run: () => state.setPanelView(active.id, keyOf(view)),
      });

  const contextContract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT);
  for (const [field, schema] of Object.entries(contextContract?.schema.properties ?? {})) {
    // `set <field> <choice>` is built for a field of choices; a list (ADR-0016 note 37) has none to offer here.
    const options = schema.type === 'array' ? [] : schema.enum ?? [];
    if (options.length === 0) continue;
    const name = schema.title ?? field;
    commands.push({
      id: `context.set:${field}`,
      words: `set ${wordsOf(name)}`,
      title: `Set ${name}`,
      group: 'Context',
      args: [{
        name: wordsOf(name).replace(/ /g, '-'),
        required: true,
        complete: () => options.map((value) => ({ value })),
        check: (value) => (options.some((o) => o.toLowerCase() === value.toLowerCase()) ? undefined : `one of ${options.join(', ')}`),
      }],
      run: (given) => {
        const value = Object.values(given)[0]!;
        state.requestContext(contextContract!, { [field]: options.find((o) => o.toLowerCase() === value.toLowerCase())! });
      },
    });
  }

  /** The places `go` takes: those of the page in the active panel that are no tab (ADR-0016 note 30). */
  const placesToGo = () => placesOf(active?.id).filter((p) => p.tab !== true);

  commands.push(
    {
      id: 'context.focus',
      words: 'edit context',
      title: 'Edit the context',
      group: 'Context',
      shortcut: 'mod+shift+E',
      run: () => document.querySelector<HTMLElement>('#context-bar input')?.focus(),
    },
    // ADR-0016 note 30: a command per tab of the page in the active panel, as the page has them when the line opens.
    // Each is the page's own, so the empty line lists it with the page's commands; a tab that does not show now
    // refuses with the page's reason.
    ...(active === undefined ? [] : placesOf(active.id).filter((p) => p.tab === true).map((tab): Command => ({
      id: `panel.open.${tab.word}`,
      words: `open ${tab.word}`,
      title: `Open ${tab.title}`,
      group: 'Panels',
      target: panelLabel(active),
      page: true,
      run: () => {
        // Read again when it runs: the page may have drawn its tabs anew since the line opened.
        const place = placesOf(active.id).find((p) => p.tab === true && p.word === tab.word);
        if (place === undefined) throw new CommandRefused(`This page has no tab ${tab.word} now.`);
        goTo(place, args.notify);
      },
    }))),
    {
      // ADR-0016 §9, notes 20 and 30: the places are the page's, read when the command runs or completes, and `go` says
      // where the keyboard went. A tab is opened by `open`, so `go` offers the places that are none.
      id: 'panel.go',
      words: 'go',
      title: 'Put the keyboard in a place of the page, such as a grid',
      group: 'Panels',
      args: [{
        name: 'place', required: true, hint: 'a place of this page',
        complete: () => placesToGo().map((p) => ({ value: p.word, detail: p.title })),
        check: (word) => (placesToGo().some((p) => p.word === word.toLowerCase()) ? undefined : `one of ${placesToGo().map((p) => p.word).join(', ')}`),
      }],
      ...(active === undefined ? { unavailable: 'No panel is open.' } : placesToGo().length === 0 ? { unavailable: 'This page has no places to go to.' } : { target: panelLabel(active) }),
      run: (a) => {
        const place = placesToGo().find((p) => p.word === a.place?.toLowerCase());
        if (place === undefined) throw new CommandRefused(`go needs a place of this page: ${placesToGo().map((p) => p.word).join(', ')}.`);
        goTo(place, args.notify);
      },
    },
    {
      // `go` to the page's first grid, with a key (ADR-0016 §9, note 20).
      id: 'panel.focusGrid',
      words: 'focus grid',
      title: 'Give the keyboard to the panel\'s grid',
      group: 'Panels',
      shortcut: 'mod+shift+G',
      ...(active === undefined ? { unavailable: 'No panel is open.' } : { target: panelLabel(active) }),
      run: () => {
        const grid = placesOf(active?.id).find((p) => p.grid === true);
        if (grid === undefined) throw new CommandRefused('This panel has no grid yet.');
        goTo(grid, args.notify);
      },
    },
    // The shell's verbs over its panels (ADR-0006 note 7), declared once with the strip's menus and keys.
    ...panelCommands(state, args.notify),
    {
      id: 'theme.toggle',
      words: 'toggle theme',
      title: 'Switch between light and dark',
      group: 'Appearance',
      run: args.toggleTheme,
    },
    ...settingCommands(args.settings ?? [], args.notify),
    {
      // ADR-0006 §7: the request goes to the AI pane with the current context; the keyboard stays where it was.
      id: 'ai.ask',
      words: 'ai',
      title: 'Ask the AI',
      group: 'AI',
      args: [{ name: 'request', required: true, rest: true, hint: 'what you want done, in your own words' }],
      ...(args.ai === undefined ? { unavailable: 'The AI pane is not shown here; nothing was sent.' } : {}),
      ...(active === undefined ? {} : { target: panelLabel(active) }),
      run: (a) => {
        const refused = args.ai?.ask(a.request ?? '');
        if (refused !== undefined) args.notify('refused', `ai: ${refused}`);
      },
    },
    {
      id: 'ai.pane',
      words: args.ai?.open === true ? 'close ai pane' : 'open ai pane',
      title: args.ai?.open === true ? 'Hide the AI pane' : 'Show the AI pane',
      group: 'AI',
      ...(args.ai === undefined ? { unavailable: 'The AI pane is not shown here.' } : {}),
      run: () => args.ai?.setOpen(!args.ai.open),
    },
  );
  assertDistinct(commands);
  return commands;
}

/** A title as the words a command is typed with: lower case, single spaces, no punctuation. */
export function wordsOf(title: string): string {
  return title.toLowerCase().replace(/[^a-z0-9.\-/ ]+/g, ' ').replace(/\s+/g, ' ').trim();
}

let openLine: (() => void) | undefined;

// How much of a list the line draws before it says how many more there are (ADR-0006's note 3 of 2026-11-11): every
// command an open page and the shell offer today, and a screenful of a slot's values, which may be a workspace's rows.
const COMMANDS_DRAWN = 200;
const VALUES_DRAWN = 50;
// How much of what a chain has said the line draws (ADR-0016 §9, note 24): the last two commands and what each said.
const SAID_DRAWN = 4;
// The line is typed over whatever is open, a row's dialog included (ADR-0016 note 57): above Mantine's modals (200) and
// below its popovers (300), which the line draws none of.
const LINE_Z_INDEX = 210;

/**
 * One thing an open line says of its chain (ADR-0016 §9, note 24 of 2026-11-11): a line the chain key ran, which has
 * not ended while it is `running`, or what the application said since the chain began. They are said in the order
 * they happened, and no notice is said to be any line's.
 */
type Said = Ran | { readonly id: number; readonly notice: Notice };
interface Ran { readonly id: number; readonly ran: string; readonly running?: true }

/**
 * No command is highlighted while the line lists them, empty or at `?` (ADR-0006's note 3 of 2026-11-11): the first one
 * listed is the page's, which on a pricer's page is Price, and Enter alone must run nothing the user did not choose.
 */
const NONE = -1;
const lists = (text: string): boolean => text.trim() === '' || text.trim() === '?';

/** Stands in for the AI store where no pane is mounted, so the hook order never changes. */
const NO_AI = createStore<{ readonly open: boolean }>()(() => ({ open: false }));

/** Opens the command line (Ctrl+K anywhere; `/` where it would not be typed into something). */
export function openPalette(): void {
  if (document.activeElement?.closest('.qube-palette') != null) return;
  rememberKeyboard();
  letGo();
  openLine?.();
}

export function CommandPalette() {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const panels = useWorkspace((s) => s.panels);
  const activePanelId = useWorkspace((s) => s.activePanelId);
  // The closed panels, so `reopen panel` is listed as it stands: one shown again where it was open changes no panel.
  const closed = useWorkspace((s) => s.closed);
  const context = useWorkspace((s) => s.context);
  const { toggleColorScheme } = useMantineColorScheme();
  const { settings } = useSettings();
  const aiStore = useOptionalAiStore();
  const aiOpen = useStore(aiStore ?? NO_AI, (s) => s.open);
  const active = panels.find((p) => p.id === activePanelId);
  const runId = active?.kind === 'run' ? active.runId : undefined;
  // Observes the active Run's cached record; its panel is the one that fetches and polls it.
  const activeRun = useQuery<RunRecord>({
    queryKey: ['run', runId],
    queryFn: () => workspace.api.run(runId!),
    enabled: false,
  }).data;

  const [opened, setOpened] = useState(false);
  // The text is held here and starts empty at every opening.
  const [text, setText] = useState('');
  const [highlight, setHighlight] = useState(NONE);
  const [problems, setProblems] = useState<readonly string[]>([]);
  // While the server reads the line at Enter (ADR-0016 §5) the line is closing on it: it is not typed into, and no
  // second Enter is taken.
  const [closing, setClosing] = useState(false);
  // Counts the line's turns: an answer for a line that was closed or opened again since is not this line's to show.
  const turn = useRef(0);
  // Counts the line's openings: a reading belongs to the opening that asked for it.
  const [opening, setOpening] = useState(0);
  // What the user was looking at when the line opened (ADR-0016 §9, note 16 of 2026-11-11): read once at each opening,
  // from the page in the active panel, and sent with every reading of that opening and with its Enter.
  const [screen, setScreen] = useState<{ readonly door: CommandDoor; readonly view: LineView } | undefined>(undefined);
  const input = useRef<HTMLInputElement>(null);
  // What this opening's chain has said (ADR-0016 §9, note 24 of 2026-11-11). Nothing until its first chained command.
  const [said, setSaid] = useState<readonly Said[] | undefined>(undefined);
  const saidIds = useRef(0);
  // The chained line that has not ended. The line is typed into meanwhile, for after it, and runs nothing until it has
  // ended: this is of the same drawing as the screen a line would be read with, so no line runs with the screen of
  // before a command that is moving it.
  const staying = said?.find((entry): entry is Ran => 'ran' in entry && entry.running === true);
  // The chained line that has not ended: what the line says of it, and the text the line held when it was run, which
  // comes back if it is refused.
  const running = useRef<{ readonly id: number; readonly ran: string; readonly text: string } | undefined>(undefined);
  // What ends the chained line that put the keyboard in a place the page has yet to draw, once the place takes it.
  const arriving = useRef<(() => void) | undefined>(undefined);
  // The part of a refused line to select once the line is drawn with that text.
  const select = useRef<{ readonly start: number; readonly end: number } | undefined>(undefined);

  // Built when the line opens, at each chained command and as the panels change, so a page that registers its commands
  // after mounting is seen.
  const commands = useMemo(() => {
    const state = workspace.store.getState();
    const ai = aiStore === undefined ? undefined : {
      open: aiOpen,
      setOpen: (open: boolean) => aiStore.getState().setOpen(open),
      ask: (request: string) => aiStore.getState().ask(request, aiContextOf(active)),
    };
    return buildCommands({ catalog: workspace.catalog, state: { ...state, panels, activePanelId, closed, context }, activeRun, toggleTheme: toggleColorScheme, notify: workspace.notify,
      settings: Object.values(settings), ...(ai === undefined ? {} : { ai }) });
  }, [workspace, panels, activePanelId, closed, context, activeRun, toggleColorScheme, opened, opening, aiStore, aiOpen, active, settings]);

  /** Reads what the page in the active panel shows now, as an opening: at each opening, and at each chained command. */
  const readScreen = useCallback(() => {
    const door = commandDoorOf(workspace.store.getState().activePanelId);
    const view = door?.view();
    setScreen(door === undefined || view === undefined ? undefined : { door, view });
    setOpening((n) => n + 1);
  }, [workspace]);
  useEffect(() => {
    openLine = () => {
      readScreen();
      running.current = undefined; arriving.current = undefined; select.current = undefined;
      turn.current += 1; setClosing(false); setText(''); setProblems([]); setHighlight(NONE); setSaid(undefined); setOpened(true);
    };
    return () => { openLine = undefined; };
  }, [readScreen]);
  // The line hears what the application says (ADR-0016 §9, note 24): once a chain has begun, each notice is said under
  // the line as it arrives, a Price's result long after its command as much as an edit's sentence.
  useEffect(() => workspace.hear((notice) => {
    const id = saidIds.current += 1;
    setSaid((before) => (before === undefined ? before : [...before, { id, notice }]));
  }), [workspace]);
  // A refused line's word at fault is selected once the line is drawn with its text: a chained line's was cleared
  // while it ran.
  useLayoutEffect(() => {
    const at = select.current;
    if (at === undefined) return;
    select.current = undefined;
    input.current?.setSelectionRange(at.start, at.end);
  });
  /** The view this opening was read with, for a command of the workspace it is of. */
  const viewOn = (door: CommandDoor): LineView | undefined => (screen !== undefined && screen.door.workspaceId === door.workspaceId ? screen.view : undefined);

  /** Runs a command, and answers why when nothing ran: it cannot run now, or it could not read its arguments. */
  const run = (command: Command, args: Readonly<Record<string, string>> = {}): string | undefined => {
    const reason = execute(command, args);
    if (reason === undefined) recordUse(command.id);
    return reason;
  };

  // A chord has no line to say why in: its reason is a notice.
  const chord = (command: Command) => {
    const reason = run(command);
    if (reason !== undefined) workspace.notify('refused', `${command.words}: ${reason}`);
  };

  useHotkeys([
    ['mod+K', openPalette],
    ...commands.filter((c) => c.shortcut !== undefined).map((c): [string, () => void] => [c.shortcut!, () => chord(c)]),
  ], []);

  // `/` opens the line wherever it would not be typed into something: not in a text field, not in a grid, where it
  // starts editing a cell, and not in the AI chat, whose slash commands are its own (ADR-0006 §2).
  useEffect(() => {
    const onKey = (event: globalThis.KeyboardEvent) => {
      if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || event.defaultPrevented) return;
      const at = event.target instanceof Element ? event.target : null;
      if (at?.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"], .ex-grid, [data-ai-chat]') != null) return;
      event.preventDefault();
      openPalette();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  const reading = readLine(text, commands);
  // While a command is chosen the list has sections (ADR-0006's note 3 of 2026-11-11); a slot's values have none.
  // What is drawn is what the arrows reach; what is left out is counted and said, never cut in silence.
  const sections = drawnSections(reading.kind === 'choosing' ? listed(reading.query, commands, usageScore) : [], COMMANDS_DRAWN);
  const listedCommands = sections.sections.flatMap((s) => s.commands);
  const chosenCommand = reading.kind !== 'choosing' || highlight === NONE || listedCommands.length === 0 ? undefined : listedCommands[Math.min(highlight, listedCommands.length - 1)];
  // A command the catalog says how to type is read by the server (ADR-0016 §5): its line while it is typed, the latest
  // text winning, and the words of one that takes no argument while it is the one chosen.
  const served = reading.kind === 'arguments' ? reading.command.served : chosenCommand?.served;
  // Nothing is asked while a chained line runs: a reading then would be of the workspace that line is about to leave.
  const asked = opened && served !== undefined && staying === undefined ? lineAsked(text, reading, chosenCommand) : undefined;
  const answer = useQuery<LinePreview>({
    queryKey: ['command-line', opening, served?.door.workspaceId, asked],
    // After the edits the page has already sent, so that the reading is of the workspace they leave.
    queryFn: async ({ signal }) => {
      await served!.door.settled();
      const view = viewOn(served!.door);
      return workspace.api.preview(asked!, served!.door.workspaceId, view === undefined ? undefined : { view }, signal);
    },
    enabled: asked !== undefined,
    // A reading is of the workspace as it stood when it was asked for, so none is kept once the line has moved on. The
    // one before is drawn until this text's own arrives, said to be so, and only within one opening on one workspace:
    // a reading of an earlier opening, or of another workspace, is of a line the user is no longer looking at.
    gcTime: 0,
    retry: false,
    placeholderData: (previous, before) => (before?.queryKey[1] === opening && before.queryKey[2] === served?.door.workspaceId ? previous : undefined),
  });
  const latest = asked === undefined ? undefined : answer.data;
  // The view in the server's words (ADR-0016 note 19): asked once as the line opens, with no text, so that it is said
  // before anything is typed; every reading asked with the view says it again, as the workspace then stood.
  const seen = useQuery<LinePreview>({
    queryKey: ['command-line-view', opening, screen?.door.workspaceId],
    queryFn: async ({ signal }) => { await screen!.door.settled(); return workspace.api.preview('', screen!.door.workspaceId, { view: screen!.view }, signal); },
    enabled: opened && screen !== undefined,
    gcTime: 0,
    retry: false,
  });
  const screenSaid = screenShown(latest, opened && screen !== undefined ? seen.data : undefined);
  // What is drawn is of an earlier text, or of the workspace before it was asked about again, until this text's own
  // reading arrives: nothing is taken from it, and Enter does not run it.
  const waiting = asked !== undefined && (answer.isFetching || answer.isPending || answer.isPlaceholderData);
  const fresh = waiting || !answer.isSuccess ? undefined : answer.data;
  const values = reading.kind === 'arguments' && reading.command.served !== undefined
    ? { shown: latest?.complete ?? [], more: latest?.more ?? 0 }
    : drawn(completionsOf(reading), VALUES_DRAWN);
  const items: readonly (Command | Completion | LineCompletion)[] = reading.kind === 'choosing' ? listedCommands : values.shown;
  const more = reading.kind === 'choosing' ? sections.more : values.more;
  const at = highlight === NONE || items.length === 0 ? NONE : Math.min(highlight, items.length - 1);
  const chosen = at === NONE ? undefined : items[at];

  const change = (next: string) => { setText(next); setHighlight(lists(next) ? NONE : 0); setProblems([]); };
  /** The line closes, and holds nothing: no turn of it is left to answer, and no chain to say anything of. */
  const leave = () => {
    turn.current += 1; running.current = undefined; arriving.current = undefined; select.current = undefined;
    setClosing(false); setSaid(undefined); setOpened(false);
  };
  const close = () => { leave(); returnKeyboard(); };
  /**
   * The keyboard stays the line's from here (ADR-0016 §9, note 24). A place that takes it gives it back at once: that
   * ends the chained line that put it there, or, with none waiting on it, has the line read the screen again. A dialog
   * that takes it closes the line.
   */
  const hold = () => { if (input.current !== null) holdKeyboard(input.current, { taken: () => (arriving.current ?? readScreen)(), lost: leave }); };
  /**
   * A chained line begins (ADR-0016 §9, note 24): the line says it is running, and is empty for the next line, which
   * is typed meanwhile and run once this one has ended.
   */
  const chainBegins = (ran: string) => {
    hold();
    const id = saidIds.current += 1;
    running.current = { id, ran, text };
    setSaid((before) => [...(before ?? []), { id, ran, running: true }]);
    setText(''); setProblems([]); setHighlight(NONE);
  };
  /**
   * The chained line was refused, and nothing ran (ADR-0006's note 1 of 2026-11-11): the line holds it again as it
   * was typed, with why, and `at` selected. What was typed while it ran was typed for after it, and goes with it.
   */
  const chainRefused = (reason: string, at?: { readonly start: number; readonly end: number }) => {
    const refused = running.current;
    if (refused === undefined) throw new Error('A chained line was refused, and the line holds none that is running.');
    running.current = undefined; arriving.current = undefined;
    setSaid((before) => before?.filter((entry) => entry.id !== refused.id));
    setText(refused.text); setHighlight(lists(refused.text) ? NONE : 0); setProblems([reason]);
    select.current = at;
    input.current?.focus();
  };
  /** The chained line has ended: the line says it ran, and reads the screen and the commands as at an opening. */
  const chainEnded = () => {
    const ended = running.current;
    running.current = undefined; arriving.current = undefined;
    setSaid((before) => before?.map((entry) => (entry.id === ended?.id && 'ran' in entry ? { id: entry.id, ran: entry.ran } : entry)));
    setProblems([]);
    readScreen();
    input.current?.focus();
  };
  /**
   * The chained line's command ran. Where it put the keyboard in a place the page has yet to draw, it has not ended
   * until the place takes the keyboard: the screen the next line is read with is that place's, never the one before.
   */
  const chainRan = () => {
    if (keyboardAwaited()) arriving.current = chainEnded;
    else chainEnded();
  };
  /** The line closed on a command that ran: one that opened a surface leaves the keyboard to it; any other gives it back. */
  const keyboardToWhatRan = () => {
    if (workspace.store.getState().runDialog === undefined && document.activeElement === document.body) returnKeyboard();
    else keepKeyboard();
  };
  /** Whether a line may run now. While a chained line has yet to end, none may, and the line says so. */
  const mayRun = (): boolean => {
    if (staying !== undefined) setProblems([`${staying.ran} has not ended yet. Press Enter again once it has.`]);
    return staying === undefined && !closing;
  };
  /** Runs a command of the client's with the chain key (ADR-0016 §9, note 24): `line` is what the line says ran. */
  const runChained = (command: Command, args: Readonly<Record<string, string>>, line: string) => {
    chainBegins(line);
    const reason = run(command, args);
    if (reason !== undefined) { chainRefused(reason); return; }
    const { runDialog, appDialog } = workspace.store.getState();
    if (runDialog === undefined && appDialog === undefined) { chainRan(); return; }
    // A command that opened a dialog ends the chain: the line closes, as with Enter, and the dialog has the keyboard.
    releaseKeyboard();
    letGo();
    leave();
    keyboardToWhatRan();
  };
  /** Runs a command of the client's with Enter: the line closes on it. */
  const runClosing = (command: Command, args: Readonly<Record<string, string>>) => {
    // The line is closing, so it holds the keyboard no longer: what the command focuses keeps it.
    const wasHolding = releaseKeyboard();
    letGo();
    setOpened(false);
    const reason = run(command, args);
    if (reason !== undefined) {
      // Nothing ran (ADR-0006's note 1 of 2026-11-11): the line stays, with what was typed and why. It was closed and
      // is opened again within this one event, so it is never drawn closed.
      setOpened(true);
      setProblems([reason]);
      input.current?.focus();
      if (wasHolding) hold();
      return;
    }
    leave();
    keyboardToWhatRan();
  };
  const runNow = (command: Command, args: Readonly<Record<string, string>>, chaining: boolean, line: string) => {
    if (chaining) runChained(command, args, line);
    else runClosing(command, args);
  };

  /**
   * Enter on a command the server reads (ADR-0016 §2, §5): the page runs the reading this line shows for exactly this
   * text, which a workspace that has moved since refuses; with none shown yet, the server reads `line` as the page's
   * workspace then stands. Taken, the line closes, and what was done is the page's to say. Refused, nothing ran: the
   * line stays, with what was typed and why (ADR-0006's note 1 of 2026-11-11), its word at fault marked, and the
   * server is asked to read it again, since the workspace may not be as the reading on screen had it. With the chain
   * key (`chaining`, ADR-0016 §9, note 24) the line stays open and says the line is running, then that it ran.
   */
  const enterServed = (command: Command, line: string, chaining: boolean) => {
    if (closing || staying !== undefined || command.served === undefined) return;
    const mine = turn.current += 1;
    const typed = line === text;
    // With Enter the line is closing, so it holds the keyboard no longer: a place the page gives it to keeps it.
    const wasHolding = chaining ? false : releaseKeyboard();
    if (chaining) chainBegins(line.trim());
    else setClosing(true);
    void command.served.door.enter(line, line === asked ? fresh : undefined, viewOn(command.served.door)).then((ended) => {
      const reason = ended !== undefined && 'sentence' in ended ? ended : undefined;
      // The line was closed, or opened again, meanwhile: its reason has no line left, and is a notice.
      if (turn.current !== mine) {
        if (reason === undefined) recordUse(command.id);
        else workspace.notify('refused', `${command.words}: ${reason.sentence}`);
        return;
      }
      if (reason === undefined) {
        recordUse(command.id);
        // A command that put the keyboard somewhere itself keeps it there (ADR-0016 §9, note 22); any other gives it back.
        if (chaining) {
          if (ended !== undefined) keepKeyboard();
          chainRan();
          return;
        }
        leave();
        if (ended === undefined) returnKeyboard();
        else keepKeyboard();
        return;
      }
      void queryClient.invalidateQueries({ queryKey: ['command-line'] });
      // The word at fault is selected, so that typing replaces it. One that ends the line may only lack what follows
      // it (`set R1 checked`): there the caret stands after the line, and typing adds to it.
      const at = !typed || reason.start === undefined || reason.end === undefined ? undefined
        : line.slice(reason.end).trim() === '' ? { start: line.length, end: line.length } : { start: reason.start, end: reason.end };
      if (chaining) { chainRefused(reason.sentence, at); return; }
      setClosing(false);
      if (wasHolding) hold();
      setProblems([reason.sentence]);
      select.current = at;
      input.current?.focus();
    });
  };

  const takeItem = (item: Command | Completion | LineCompletion) => {
    if ('start' in item) {
      // A completion of an earlier text has its place in that text, not in this one.
      if (waiting) return;
      change(taken(text, item));
    } else change(take(text, reading, item));
    input.current?.focus();
  };

  /**
   * A command chosen in the list: one that needs words after its own is written on the line, and any other runs, with
   * the chain key (`chaining`) leaving the line open. A served command needs words where an argument of its form is
   * required, as a client's does: one whose arguments may all be left out (`pv01 retry`, `reset` on the focused cell)
   * was read by its words alone while it was chosen (`lineAsked`), and Enter runs what that reading showed.
   */
  const choose = (command: Command, chaining = false) => {
    if (command.unavailable !== undefined) { setProblems([command.unavailable]); return; }
    if (command.served !== undefined) {
      if (command.served.arguments.some((a) => a.required)) takeItem(command);
      else if (mayRun()) enterServed(command, command.words, chaining);
      return;
    }
    if ((command.args ?? []).some((a) => a.required)) { takeItem(command); return; }
    if (mayRun()) runNow(command, {}, chaining, command.words);
  };

  /** Enter, and with `chaining` the chain key (ADR-0016 §9, note 24): the same line run, and the line kept open. */
  const enter = (chaining: boolean) => {
    if (closing) return;
    // A command chosen in the list may only be written on the line, which a chained line that is running allows.
    if (reading.kind === 'choosing' && chosen !== undefined && 'words' in chosen) { choose(chosen, chaining); return; }
    if (!mayRun()) return;
    if (reading.kind === 'choosing') {
      // A list with nothing chosen in it: Enter runs nothing, and says how to choose.
      if (lists(text)) { setProblems(['Choose a command with the arrows, or type what you want to do.']); return; }
      // ADR-0006 §7: what matches no command is offered to the AI last.
      const ask = commands.find((c) => c.id === 'ai.ask');
      if (ask !== undefined && ask.unavailable === undefined) { runNow(ask, { request: text.trim() }, chaining, `${ask.words} ${text.trim()}`); return; }
      setProblems(['No command matches.']);
      return;
    }
    if (reading.command.unavailable !== undefined) { setProblems([reading.command.unavailable]); return; }
    if (reading.command.served !== undefined) { enterServed(reading.command, text, chaining); return; }
    const done = finish(reading);
    if ('problems' in done) { setProblems(done.problems); return; }
    runNow(reading.command, done.args, chaining, text.trim());
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    // While an IME composes, every key of the line is the IME's (ADR-0016 §9, note 24): the Enter that commits a
    // composition runs nothing, and its arrows and Tab choose among the IME's candidates. 229 is what a browser that
    // has already ended the composition still says of the key that ended it.
    if (event.nativeEvent.isComposing || event.keyCode === 229) return;
    // Ctrl+N and Ctrl+P move as the arrows do (owner, 2026-10-28): literal Ctrl, so not Cmd on a Mac.
    const emacs = event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey;
    const key = event.key.toLowerCase();
    const step = event.key === 'ArrowDown' || (emacs && key === 'n') ? 1 : event.key === 'ArrowUp' || (emacs && key === 'p') ? -1 : 0;
    if (step !== 0) {
      event.preventDefault();
      // From nothing chosen, down is the first and up the last.
      if (items.length > 0) setHighlight(at === NONE ? (step > 0 ? 0 : items.length - 1) : (at + step + items.length) % items.length);
    } else if (event.key === 'Tab') {
      event.preventDefault();
      if (chosen !== undefined && !closing) takeItem(chosen);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      // Ctrl+Enter, and ⌘+Enter on a Mac, is the chain key: the line runs and stays open for the next.
      enter(event.ctrlKey || event.metaKey);
    }
  };

  const optionId = (i: number) => `qube-line-option-${i}`;
  const head = reading.kind === 'arguments' ? reading.command : chosen !== undefined && 'words' in chosen ? chosen : undefined;
  // The server did not answer with a reading: that is said as a failure, never drawn as what the line means.
  const unread = asked !== undefined && answer.isError && !answer.isFetching ? `The server did not read the line: ${answer.error.message}`
    : opened && screen !== undefined && seen.isError && !seen.isFetching ? `The server did not read what is on screen: ${seen.error.message}` : undefined;
  // What the server read of the line, under it. A reason the line already says in red, at Enter, is not said twice.
  const shown = asked === undefined || unread !== undefined ? undefined : latest === undefined ? {} : previewShown(latest);
  const preview = shown === undefined || (problems.length > 0 && shown.reason !== undefined) ? undefined : shown;
  // `under` is the heading an option is drawn under: a command does not repeat the panel its heading names.
  const option = (item: Command | Completion | LineCompletion, i: number, under?: string) => (
    <UnstyledButton key={'words' in item ? item.id : 'start' in item ? `${item.start}:${item.value}:${item.detail}` : item.value} id={optionId(i)} role="option" aria-selected={i === at} tabIndex={-1}
      className="qube-line-option" data-selected={i === at || undefined} onMouseDown={(e) => e.preventDefault()}
      // A command that cannot run now is still written on the line, where Enter says why.
      onClick={() => { if (closing) return; if ('words' in item && item.unavailable === undefined) choose(item); else takeItem(item); }}
      px="xs" py={4} style={{ borderRadius: 4, background: i === at ? 'var(--mantine-color-default-hover)' : undefined }}>
      {'words' in item ? <CommandOption command={item} under={under} /> : <ValueOption completion={item} />}
    </UnstyledButton>
  );

  return (
    <Modal opened={opened} onClose={close} withCloseButton={false} trapFocus returnFocus={false} size="lg" yOffset="10vh" zIndex={LINE_Z_INDEX}
      transitionProps={{ duration: 0 }} className="qube-palette" aria-label="Command line" padding="sm">
      <Stack gap={6}>
        <TextInput ref={input} data-autofocus value={text} onChange={(e) => change(e.currentTarget.value)} onKeyDown={onKeyDown}
          placeholder={placeholder(listed('', commands, usageScore))} aria-label="Command"
          role="combobox" aria-expanded={items.length > 0} aria-controls="qube-line-options" aria-autocomplete="list"
          readOnly={closing} aria-busy={closing || staying !== undefined || undefined}
          {...(chosen === undefined ? {} : { 'aria-activedescendant': optionId(at) })}
          {...(preview === undefined && screenSaid === undefined ? {} : { 'aria-describedby': [screenSaid === undefined ? [] : ['qube-line-context'], preview === undefined ? [] : ['qube-line-preview']].flat().join(' ') })}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} />
        {/* What this opening's chain has said, in the order it happened (ADR-0016 §9, note 24): each line the chain key
            ran, and each notice since. A notice's kind is said by its icon and its colour (design guidelines §6). */}
        {said !== undefined && said.length > 0 && (
          <Stack gap={0} role="log" aria-label="What this line ran" data-testid="command-said">
            {said.length > SAID_DRAWN && <Text size="xs" c="dimmed" data-testid="command-said-earlier">{said.length - SAID_DRAWN} earlier not shown.</Text>}
            {said.slice(-SAID_DRAWN).map((entry) => <SaidRow key={entry.id} entry={entry} />)}
          </Stack>
        )}
        {/* What the line was opened on (ADR-0016 note 19): what `selected`, `shown` and rows left out mean here. An order
            that has moved since is stale, said in the sentence and drawn as a warning (design guidelines §7). */}
        {screenSaid !== undefined && (
          <Text size="xs" c={screenSaid.moved ? 'yellow' : 'dimmed'} id="qube-line-context" data-testid="command-context" {...(screenSaid.moved ? { 'data-moved': true } : {})}>
            On screen: {screenSaid.sentence}
          </Text>
        )}
        {head !== undefined && <Head command={head} reading={reading} latest={latest} waiting={waiting} />}
        {preview !== undefined && (
          <Stack gap={0} id="qube-line-preview" className="qube-line-preview" data-testid="command-preview" aria-busy={waiting || undefined}>
            <Stack gap={0} className="qube-line-read">
              {/* A short form (ADR-0016 note 29): the line it stands for, which is what Enter runs and `undo` will name. */}
              {preview.expands !== undefined && <Text size="xs" data-testid="command-expands"><Text span c="dimmed">Runs as </Text><Text span ff="monospace">{preview.expands}</Text></Text>}
              {preview.rows !== undefined && <Text size="xs" ff="monospace">{preview.rows}</Text>}
              {preview.would !== undefined && <Text size="xs">{preview.would}{preview.at === undefined ? '' : ` · ${new Date(preview.at).toLocaleTimeString()}`}</Text>}
              {preview.reason !== undefined && <Text size="xs" c="dimmed">{preview.reason}</Text>}
            </Stack>
            {/* Said in words once the server has taken long enough for it to be seen (shell.css). */}
            {waiting && <Text size="xs" c="dimmed" className="qube-line-reading">Reading the line…</Text>}
          </Stack>
        )}
        {unread !== undefined && <Text size="xs" c="red" role="alert" data-testid="command-unread">{unread}</Text>}
        {problems.length > 0 && (
          <Stack gap={0} role="alert" data-testid="command-problems">
            {problems.map((p) => <Text key={p} size="xs" c="red">{p}</Text>)}
          </Stack>
        )}
        <Stack gap={0} id="qube-line-options" role="listbox" className="qube-line-options" aria-busy={(waiting && reading.kind === 'arguments') || undefined}
          aria-label={reading.kind === 'choosing' ? 'Commands' : reading.command.served !== undefined ? 'What could follow' : `Values for ${reading.slot?.name ?? 'options'}`}
          style={{ maxHeight: '50vh', overflowY: 'auto' }}>
          {reading.kind === 'arguments' ? values.shown.map((item, i) => option(item, i)) : sections.sections.map((section) => (section.heading === undefined
            ? section.commands.map((c, i) => option(c, section.start + i))
            : (
              <Stack key={section.heading} gap={0} role="group" aria-labelledby={`qube-line-heading-${section.start}`}>
                {/* The heading names its group and is not one of the list's own children: a listbox holds options and groups of them. */}
                <Text id={`qube-line-heading-${section.start}`} role="presentation" size="xs" fw={600} c="dimmed" px="xs" pt="xs" data-testid="command-heading">{section.heading}</Text>
                {section.commands.map((c, i) => option(c, section.start + i, section.heading))}
              </Stack>
            )))}
          {items.length === 0 && reading.kind === 'choosing' && text.trim() !== '' && (
            <Text size="xs" c="dimmed" px="xs">No command matches. <Kbd size="xs">Enter</Kbd> asks the AI: <Kbd size="xs">ai {text.trim()}</Kbd></Text>
          )}
        </Stack>
        {/* Said under the list, not in it: it is no option, and it is read out when the list is cut. */}
        {more > 0 && <Text size="xs" c="dimmed" px="xs" role="status" data-testid="command-more">{more} more: type to narrow.</Text>}
        <Group gap="md">
          <Text size="xs" c="dimmed"><Kbd size="xs">Tab</Kbd> complete</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">Enter</Kbd> run</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">Ctrl</Kbd>+<Kbd size="xs">Enter</Kbd> run and stay</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">↑</Kbd><Kbd size="xs">↓</Kbd> choose</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">Esc</Kbd> close</Text>
        </Group>
      </Stack>
    </Modal>
  );
}

/** A row of what the line says of its chain: a line the chain key ran, or a notice, its kind said by its icon and its colour. */
function SaidRow({ entry }: { readonly entry: Said }): ReactNode {
  if ('ran' in entry) return (
    <Text size="xs" c="dimmed" data-kind={entry.running === true ? 'running' : 'ran'}>
      <Text span ff="monospace" fw={600}>› {entry.ran}</Text>{entry.running === true ? ' is running…' : ''}
    </Text>
  );
  const refused = entry.notice.kind === 'refused';
  return (
    <Group gap="xs" wrap="nowrap" align="flex-start" data-kind={entry.notice.kind} c={refused ? 'red' : 'teal'}>
      <Box className="qube-line-said-icon">{refused ? <IconX size={14} stroke={1.75} aria-hidden /> : <IconCheck size={14} stroke={1.75} aria-hidden />}</Box>
      <Text size="xs">{entry.notice.message}</Text>
    </Group>
  );
}

/** The chosen command with its slots: given values, the slot being typed, and those still to come. */
function Head({ command, reading, latest, waiting }: { readonly command: Command; readonly reading: ReturnType<typeof readLine>; readonly latest: LinePreview | undefined; readonly waiting: boolean }): ReactNode {
  const args = command.args ?? [];
  // A served command's arguments are the catalog's, each with the words the server read for it, once it has read the
  // line, and what it read under other names after them (`argumentsDrawn`); while the reading is of an earlier text
  // they are dimmed with it (shell.css).
  return (
    <Group gap={6} wrap="wrap" data-testid="command-pending" className="qube-line-head" aria-busy={waiting || undefined}>
      <Text size="sm" fw={600} ff="monospace">{command.words}</Text>
      {/* A short form's own arguments are how it is typed; once it is read, the line it stands for says what was read. */}
      {(latest?.expands !== undefined || command.served === undefined ? [] : argumentsDrawn(command.served.arguments, latest?.arguments ?? [])).map(({ read, ...a }) => {
        // One the user did not type (ADR-0016 note 18) is drawn as an outline, and says in words where it came from.
        return (
          <Badge key={a.name} size="sm" radius="sm" className="qube-line-read" variant={read === undefined || read.from === 'context' ? 'outline' : 'light'} color={read === undefined ? 'gray' : 'blue'}
            style={{ textTransform: 'none' }} {...(read === undefined ? {} : { 'data-from': read.from })}>
            {read === undefined ? (a.required ? a.name : `[${a.name}]`) : argumentShown(read)}
          </Badge>
        );
      })}
      {args.flatMap((a) => {
        if (a.parts !== undefined) {
          const typed = reading.kind === 'arguments' ? reading.given[a.name] ?? '' : '';
          return a.parts(typed).map((p, i) => (
            <Badge key={`${a.name}/${i}`} size="sm" radius="sm" variant={p.value === undefined ? 'outline' : 'light'}
              color={p.name === '?' ? 'red' : p.value === undefined ? 'gray' : 'blue'} style={{ textTransform: 'none' }}>
              {p.value === undefined ? p.name : p.name === '?' ? `? ${p.value}` : `${p.name} ${p.value}`}
            </Badge>
          ));
        }
        const value = reading.kind === 'arguments' ? reading.given[a.name] ?? (reading.slot === a && reading.typed !== '' ? reading.typed : undefined) : undefined;
        const current = reading.kind === 'arguments' && reading.slot === a;
        return (
          <Badge key={a.name} size="sm" radius="sm" variant={current ? 'filled' : value === undefined ? 'outline' : 'light'}
            color={value === undefined && a.required && !current ? 'gray' : 'blue'} style={{ textTransform: 'none' }}>
            {a.required ? '' : `${a.name}=`}{value ?? (a.required ? a.name : '…')}
          </Badge>
        );
      })}
      {command.target !== undefined && <Text size="xs" c="dimmed">on {command.target}</Text>}
      {reading.kind === 'arguments' && reading.slot?.hint !== undefined && <Text size="xs" c="dimmed">{reading.slot.name}: {reading.slot.hint}</Text>}
    </Group>
  );
}

function CommandOption({ command, under }: { readonly command: Command; readonly under: string | undefined }): ReactNode {
  const target = command.target === undefined || command.target === under ? '' : ` · ${command.target}`;
  return (
    <Group gap="xs" wrap="nowrap" justify="space-between">
      <Group gap="xs" wrap="wrap">
        <Text size="sm" ff="monospace" fw={600} {...(command.unavailable === undefined ? {} : { c: 'dimmed' })}>{command.words}</Text>
        {signature(command) !== '' && <Text size="xs" ff="monospace" c="dimmed">{signature(command)}</Text>}
        <Text size="xs" c="dimmed">{command.title}{target}</Text>
        {command.unavailable !== undefined && <Text size="xs" c="orange">{command.unavailable}</Text>}
      </Group>
      {command.shortcut !== undefined && <Kbd size="xs">{command.shortcut.replace('mod', 'Ctrl')}</Kbd>}
    </Group>
  );
}

function ValueOption({ completion }: { readonly completion: Completion }): ReactNode {
  return (
    <Group gap="xs" wrap="nowrap">
      <Text size="sm" ff="monospace">{completion.value}</Text>
      {completion.detail !== undefined && <Text size="xs" c="dimmed">{completion.detail}</Text>}
    </Group>
  );
}

