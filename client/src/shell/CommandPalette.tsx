import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Badge, Group, Kbd, Modal, Stack, Text, TextInput, UnstyledButton, useMantineColorScheme } from '@mantine/core';
import { useHotkeys } from '@mantine/hooks';
import {
  assertDistinct, CommandRefused, completionsOf, drawn, drawnSections, execute, finish, listed, placeholder, readLine, signature, take,
  type Command, type CommandDoor, type Completion,
} from '../platform/commands/commands';
import { argumentShown, commandDoorOf, lineAsked, previewShown, screenShown, servedCommands, taken } from '../platform/commands/served';
import { recordUse, usageScore } from '../platform/commands/usage';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { Catalog, LineCompletion, LinePreview, LineView, RunRecord } from '../platform/contracts';
import type { Notify } from '../platform/notices';
import { viewsFor } from '../platform/views/binding';
import { CONTEXT_CONTRACT, useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { keyOf, type Panel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { keepKeyboard, letGo, rememberKeyboard, returnKeyboard } from './keyboard';
import { goTo, placesOf } from './places';
import { useStore } from 'zustand';
import { createStore } from 'zustand/vanilla';
import { useOptionalAiStore } from '../ai/AiProvider';
import { aiContextOf } from '../ai/effects';
import { appCommands } from '../apps';

/**
 * The open panel a typed title names: the one titled so, else the only one whose title contains the text. Two of one
 * title, or a part that several contain, are named back rather than guessed between.
 */
export function panelTitled(panels: readonly Panel[], text: string): { readonly panel: Panel } | { readonly reason: string } {
  const t = text.trim().toLowerCase();
  if (t === '') return { reason: 'Type the title of an open panel.' };
  const exact = panels.filter((p) => p.title.toLowerCase() === t);
  const matching = exact.length > 0 ? exact : panels.filter((p) => p.title.toLowerCase().includes(t));
  if (matching.length === 1) return { panel: matching[0]! };
  return matching.length === 0
    ? { reason: `No open panel is titled like "${text.trim()}".` }
    : { reason: `${matching.length} panels match "${text.trim()}": ${matching.map((p) => p.title).join(', ')}.` };
}

/**
 * Builds every command the workbench offers right now. Workflows, views and context values come from
 * the catalog, so a module that registers a workflow gets its command without a client change.
 */
export function buildCommands(args: {
  readonly catalog: Catalog;
  readonly state: Pick<WorkspaceState, 'panels' | 'activePanelId' | 'context' | 'openRunDialog' | 'setPanelView' | 'closePanel' | 'cycle' | 'activate' | 'requestContext' | 'showApp' | 'openAppDialog'>;
  readonly activeRun: RunRecord | undefined;
  readonly toggleTheme: () => void;
  readonly notify: Notify;
  /** The AI pane, where one is mounted: `ai <request>` hands the request to it (ADR-0006 §7, ADR-0007 §7). */
  readonly ai?: { readonly ask: (request: string) => string | undefined; readonly open: boolean; readonly setOpen: (open: boolean) => void };
}): Command[] {
  const { catalog, state, activeRun } = args;
  const active: Panel | undefined = state.panels.find((p) => p.id === state.activePanelId);
  // The applications' commands first: the empty line lists the groups in the order registered (ADR-0006's note 3 of
  // 2026-11-11), and what an application offers comes before the catalog's workflows and the shell's own.
  const commands: Command[] = [
    ...appCommands(state, args.notify),
    // The commands the catalog says how to type (ADR-0016 §4): the server reads their lines, for the workspace of the
    // page in the active panel.
    ...servedCommands(catalog, active, commandDoorOf(active?.id), args.notify),
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
        target: active.title,
        page: true,
        run: () => state.setPanelView(active.id, keyOf(view)),
      });

  const contextContract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT);
  for (const [field, schema] of Object.entries(contextContract?.schema.properties ?? {})) {
    const options = schema.enum ?? [];
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

  commands.push(
    {
      id: 'context.focus',
      words: 'edit context',
      title: 'Edit the context',
      group: 'Context',
      shortcut: 'mod+shift+E',
      run: () => document.querySelector<HTMLElement>('#context-bar input')?.focus(),
    },
    {
      // ADR-0016 §9, note 20: the places are the page's, read when the command runs or completes, and `go` says where
      // the keyboard went.
      id: 'panel.go',
      words: 'go',
      title: 'Put the keyboard in a place of the page, such as a grid or a tab',
      group: 'Panels',
      args: [{
        name: 'place', required: true, hint: 'a place of this page',
        complete: () => placesOf(active?.id).map((p) => ({ value: p.word, detail: p.title })),
        check: (word) => (placesOf(active?.id).some((p) => p.word === word.toLowerCase()) ? undefined : `one of ${placesOf(active?.id).map((p) => p.word).join(', ')}`),
      }],
      ...(active === undefined ? { unavailable: 'No panel is open.' } : placesOf(active.id).length === 0 ? { unavailable: 'This page has no places to go to.' } : { target: active.title }),
      run: (a) => {
        const place = placesOf(active?.id).find((p) => p.word === a.place?.toLowerCase());
        if (place === undefined) throw new CommandRefused(`go needs a place of this page: ${placesOf(active?.id).map((p) => p.word).join(', ')}.`);
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
      ...(active === undefined ? { unavailable: 'No panel is open.' } : { target: active.title }),
      run: () => {
        const grid = placesOf(active?.id).find((p) => p.grid === true);
        if (grid === undefined) throw new CommandRefused('This panel has no grid yet.');
        goTo(grid, args.notify);
      },
    },
    {
      id: 'panel.next',
      words: 'next panel',
      title: 'Show the next panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowRight',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(1),
    },
    {
      id: 'panel.previous',
      words: 'previous panel',
      title: 'Show the previous panel',
      group: 'Panels',
      shortcut: 'mod+alt+ArrowLeft',
      ...(state.panels.length < 2 ? { unavailable: 'There is no other panel.' } : {}),
      run: () => state.cycle(-1),
    },
    {
      id: 'panel.show',
      words: 'show panel',
      title: 'Show an open panel by its title',
      group: 'Panels',
      args: [{
        name: 'panel', required: true, rest: true, hint: 'its title, or a part of it',
        complete: () => state.panels.map((p) => ({ value: p.title })),
        check: (text) => { const named = panelTitled(state.panels, text); return 'reason' in named ? named.reason : undefined; },
      }],
      ...(state.panels.length === 0 ? { unavailable: 'No panel is open.' } : {}),
      run: (a) => { const named = panelTitled(state.panels, a.panel ?? ''); if ('panel' in named) state.activate(named.panel.id); },
    },
    {
      id: 'panel.close',
      words: 'close panel',
      title: 'Close the panel',
      group: 'Panels',
      shortcut: 'mod+shift+X',
      ...(active === undefined ? { unavailable: 'No panel is open.' } : { target: active.title }),
      run: () => { if (active !== undefined) state.closePanel(active.id); },
    },
    {
      id: 'theme.toggle',
      words: 'toggle theme',
      title: 'Switch between light and dark',
      group: 'Appearance',
      run: args.toggleTheme,
    },
    {
      // ADR-0006 §7: the request goes to the AI pane with the current context; the keyboard stays where it was.
      id: 'ai.ask',
      words: 'ai',
      title: 'Ask the AI',
      group: 'AI',
      args: [{ name: 'request', required: true, rest: true, hint: 'what you want done, in your own words' }],
      ...(args.ai === undefined ? { unavailable: 'The AI pane is not shown here; nothing was sent.' } : {}),
      ...(active === undefined ? {} : { target: active.title }),
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
  const context = useWorkspace((s) => s.context);
  const { toggleColorScheme } = useMantineColorScheme();
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
  // While the server reads the line at Enter (ADR-0016 §5) the line is not typed into, and no second Enter is taken.
  const [entering, setEntering] = useState(false);
  // Counts the line's turns: an answer for a line that was closed or opened again since is not this line's to show.
  const turn = useRef(0);
  // Counts the line's openings: a reading belongs to the opening that asked for it.
  const [opening, setOpening] = useState(0);
  // What the user was looking at when the line opened (ADR-0016 §9, note 16 of 2026-11-11): read once at each opening,
  // from the page in the active panel, and sent with every reading of that opening and with its Enter.
  const [screen, setScreen] = useState<{ readonly door: CommandDoor; readonly view: LineView } | undefined>(undefined);
  const input = useRef<HTMLInputElement>(null);

  // Built when the line opens and as the panels change, so a page that registers its commands after mounting is seen.
  const commands = useMemo(() => {
    const state = workspace.store.getState();
    const ai = aiStore === undefined ? undefined : {
      open: aiOpen,
      setOpen: (open: boolean) => aiStore.getState().setOpen(open),
      ask: (request: string) => aiStore.getState().ask(request, aiContextOf(active)),
    };
    return buildCommands({ catalog: workspace.catalog, state: { ...state, panels, activePanelId, context }, activeRun, toggleTheme: toggleColorScheme, notify: workspace.notify, ...(ai === undefined ? {} : { ai }) });
  }, [workspace, panels, activePanelId, context, activeRun, toggleColorScheme, opened, aiStore, aiOpen, active]);

  useEffect(() => {
    openLine = () => {
      const door = commandDoorOf(workspace.store.getState().activePanelId);
      const view = door?.view();
      setScreen(door === undefined || view === undefined ? undefined : { door, view });
      turn.current += 1; setOpening((n) => n + 1); setEntering(false); setText(''); setProblems([]); setHighlight(NONE); setOpened(true);
    };
    return () => { openLine = undefined; };
  }, [workspace]);
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
  const asked = opened && served !== undefined ? lineAsked(text, reading, chosenCommand) : undefined;
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
  const close = () => { turn.current += 1; setEntering(false); setOpened(false); returnKeyboard(); };
  const runNow = (command: Command, args: Readonly<Record<string, string>>) => {
    letGo();
    setOpened(false);
    const reason = run(command, args);
    if (reason !== undefined) {
      // Nothing ran (ADR-0006's note 1 of 2026-11-11): the line stays, with what was typed and why. It was closed and
      // is opened again within this one event, so it is never drawn closed.
      setOpened(true);
      setProblems([reason]);
      input.current?.focus();
      return;
    }
    // A command that opened a surface leaves the keyboard to it; any other gives it back.
    if (workspace.store.getState().runDialog === undefined && document.activeElement === document.body) returnKeyboard();
    else keepKeyboard();
  };

  /**
   * Enter on a command the server reads (ADR-0016 §2, §5): the page runs the reading this line shows for exactly this
   * text, which a workspace that has moved since refuses; with none shown yet, the server reads `line` as the page's
   * workspace then stands. Taken, the line closes, and what was done is the page's to say. Refused, nothing ran: the
   * line stays, with what was typed and why (ADR-0006's note 1 of 2026-11-11), its word at fault marked, and the
   * server is asked to read it again, since the workspace may not be as the reading on screen had it.
   */
  const enterServed = (command: Command, line: string) => {
    if (entering || command.served === undefined) return;
    const mine = turn.current += 1;
    const typed = line === text;
    setEntering(true);
    void command.served.door.enter(line, line === asked ? fresh : undefined, viewOn(command.served.door)).then((ended) => {
      const reason = ended !== undefined && 'sentence' in ended ? ended : undefined;
      // The line was closed, or opened again, meanwhile: its reason has no line left, and is a notice.
      if (turn.current !== mine) {
        if (reason === undefined) recordUse(command.id);
        else workspace.notify('refused', `${command.words}: ${reason.sentence}`);
        return;
      }
      setEntering(false);
      if (reason === undefined) {
        recordUse(command.id);
        setOpened(false);
        // A command that put the keyboard somewhere itself keeps it there (ADR-0016 §9, note 22); any other gives it back.
        if (ended === undefined) returnKeyboard();
        else keepKeyboard();
        return;
      }
      void queryClient.invalidateQueries({ queryKey: ['command-line'] });
      setProblems([reason.sentence]);
      input.current?.focus();
      if (!typed || reason.start === undefined || reason.end === undefined) return;
      // The word at fault is selected, so that typing replaces it. One that ends the line may only lack what follows
      // it (`set R1 checked`): there the caret stands after the line, and typing adds to it.
      if (line.slice(reason.end).trim() === '') input.current?.setSelectionRange(line.length, line.length);
      else input.current?.setSelectionRange(reason.start, reason.end);
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

  /** A command chosen in the list: one that needs words after its own is written on the line, and any other runs. */
  const choose = (command: Command) => {
    if (command.unavailable !== undefined) { setProblems([command.unavailable]); return; }
    if (command.served !== undefined) {
      if (command.served.arguments.length > 0) takeItem(command);
      else enterServed(command, command.words);
      return;
    }
    if ((command.args ?? []).some((a) => a.required)) { takeItem(command); return; }
    runNow(command, {});
  };

  const enter = () => {
    if (entering) return;
    if (reading.kind === 'choosing') {
      if (!(chosen !== undefined && 'words' in chosen)) {
        // A list with nothing chosen in it: Enter runs nothing, and says how to choose.
        if (lists(text)) { setProblems(['Choose a command with the arrows, or type what you want to do.']); return; }
        // ADR-0006 §7: what matches no command is offered to the AI last.
        const ask = commands.find((c) => c.id === 'ai.ask');
        if (ask !== undefined && ask.unavailable === undefined) { runNow(ask, { request: text.trim() }); return; }
        setProblems(['No command matches.']);
        return;
      }
      choose(chosen);
      return;
    }
    if (reading.command.unavailable !== undefined) { setProblems([reading.command.unavailable]); return; }
    if (reading.command.served !== undefined) { enterServed(reading.command, text); return; }
    const done = finish(reading);
    if ('problems' in done) { setProblems(done.problems); return; }
    runNow(reading.command, done.args);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    // Ctrl+N and Ctrl+P move as the arrows do (owner, 2026-10-28): literal Ctrl, so not Cmd on a Mac, and never while an
    // IME is composing, whose keys are its own.
    const emacs = event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey && !event.nativeEvent.isComposing;
    const key = event.key.toLowerCase();
    const step = event.key === 'ArrowDown' || (emacs && key === 'n') ? 1 : event.key === 'ArrowUp' || (emacs && key === 'p') ? -1 : 0;
    if (step !== 0) {
      event.preventDefault();
      // From nothing chosen, down is the first and up the last.
      if (items.length > 0) setHighlight(at === NONE ? (step > 0 ? 0 : items.length - 1) : (at + step + items.length) % items.length);
    } else if (event.key === 'Tab') {
      event.preventDefault();
      if (chosen !== undefined && !entering) takeItem(chosen);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      enter();
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
      onClick={() => { if (entering) return; if ('words' in item && item.unavailable === undefined) choose(item); else takeItem(item); }}
      px="xs" py={4} style={{ borderRadius: 4, background: i === at ? 'var(--mantine-color-default-hover)' : undefined }}>
      {'words' in item ? <CommandOption command={item} under={under} /> : <ValueOption completion={item} />}
    </UnstyledButton>
  );

  return (
    <Modal opened={opened} onClose={close} withCloseButton={false} trapFocus returnFocus={false} size="lg" yOffset="10vh"
      transitionProps={{ duration: 0 }} className="qube-palette" aria-label="Command line" padding="sm">
      <Stack gap={6}>
        <TextInput ref={input} data-autofocus value={text} onChange={(e) => change(e.currentTarget.value)} onKeyDown={onKeyDown}
          placeholder={placeholder(listed('', commands, usageScore))} aria-label="Command"
          role="combobox" aria-expanded={items.length > 0} aria-controls="qube-line-options" aria-autocomplete="list"
          readOnly={entering} aria-busy={entering || undefined}
          {...(chosen === undefined ? {} : { 'aria-activedescendant': optionId(at) })}
          {...(preview === undefined && screenSaid === undefined ? {} : { 'aria-describedby': [screenSaid === undefined ? [] : ['qube-line-context'], preview === undefined ? [] : ['qube-line-preview']].flat().join(' ') })}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} />
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
          <Text size="xs" c="dimmed"><Kbd size="xs">↑</Kbd><Kbd size="xs">↓</Kbd> choose</Text>
          <Text size="xs" c="dimmed"><Kbd size="xs">Esc</Kbd> close</Text>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The chosen command with its slots: given values, the slot being typed, and those still to come. */
function Head({ command, reading, latest, waiting }: { readonly command: Command; readonly reading: ReturnType<typeof readLine>; readonly latest: LinePreview | undefined; readonly waiting: boolean }): ReactNode {
  const args = command.args ?? [];
  // A served command's arguments are the catalog's, each with the words the server read for it, once it has read the
  // line; while the reading is of an earlier text they are dimmed with it (shell.css).
  const given = new Map((latest?.arguments ?? []).map((a) => [a.name, a]));
  return (
    <Group gap={6} wrap="wrap" data-testid="command-pending" className="qube-line-head" aria-busy={waiting || undefined}>
      <Text size="sm" fw={600} ff="monospace">{command.words}</Text>
      {(command.served?.arguments ?? []).map((a) => {
        const read = given.get(a.name);
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

