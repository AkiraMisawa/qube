import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { Alert, Badge, Button, CloseButton, Code, Group, Kbd, Loader, Paper, ScrollArea, Select, Stack, Text, Textarea, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useValue } from '../platform/settings/settings';
import { useWorkspace } from '../platform/workspace/WorkspaceProvider';
import { useAi, useAiContext } from './AiProvider';
import { answering, conversationModel } from './aiStore';
import type { AiCard, AiStep } from './cards';
import { aiContextOf } from './effects';

// The AI pane (ADR-0007 §7): one card per request, newest last, and the chat input. `/` at the start of the input
// opens the chat's own commands, which are about the conversation, never the command line's (ADR-0006 §2).

interface SlashCommand {
  readonly words: string;
  readonly title: string;
}

export const SLASH_COMMANDS: readonly SlashCommand[] = [
  { words: 'new', title: 'Start a new conversation' },
  { words: 'stop', title: 'Stop the answer in progress' },
  { words: 'help', title: 'What the AI can be asked' },
  { words: 'close', title: 'Close the AI pane' },
];

export function slashMatches(text: string): readonly SlashCommand[] {
  if (!text.startsWith('/')) return [];
  const typed = text.slice(1).trim().toLowerCase();
  return SLASH_COMMANDS.filter((c) => c.words.startsWith(typed));
}

let chatInput: HTMLTextAreaElement | null = null;

/** Puts the keyboard in the chat input, if the pane is shown. */
export function focusAiChat(): boolean {
  if (chatInput === null) return false;
  chatInput.focus();
  return true;
}

export function AiPane() {
  const { store, transport } = useAiContext();
  const cards = useAi((s) => s.cards);
  const busy = useAi(answering);
  const panels = useWorkspace((s) => s.panels);
  const activePanelId = useWorkspace((s) => s.activePanelId);
  const activate = useWorkspace((s) => s.activate);
  const runtime = useQuery({ queryKey: ['agent', 'runtime'], queryFn: () => transport.runtime(), staleTime: Infinity });
  const [text, setText] = useState('');
  const [highlight, setHighlight] = useState(0);
  const [notice, setNotice] = useState<string | undefined>(undefined);
  const [help, setHelp] = useState(false);
  const input = useRef<HTMLTextAreaElement>(null);
  const end = useRef<HTMLDivElement>(null);

  useEffect(() => {
    chatInput = input.current;
    return () => { chatInput = null; };
  }, []);
  useEffect(() => { end.current?.scrollIntoView?.({ block: 'end' }); }, [cards]);

  const slash = slashMatches(text);
  const at = Math.min(highlight, Math.max(slash.length - 1, 0));

  const runSlash = (command: SlashCommand) => {
    setText('');
    setHighlight(0);
    const state = store.getState();
    const refused = command.words === 'new' ? state.restart() : command.words === 'stop' ? state.stop() : undefined;
    if (command.words === 'help') setHelp(true);
    if (command.words === 'close') state.setOpen(false);
    setNotice(refused);
  };

  const send = () => {
    if (text.startsWith('/')) {
      const command = slash[at];
      if (command === undefined) setNotice(`No chat command ${text.trim()}. Type / to list them.`);
      else runSlash(command);
      return;
    }
    const active = panels.find((p) => p.id === activePanelId);
    const refused = store.getState().ask(text, aiContextOf(active));
    setNotice(refused);
    if (refused === undefined) setText('');
  };

  const onKey = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (slash.length > 0 && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      event.preventDefault();
      setHighlight((h) => (h + (event.key === 'ArrowDown' ? 1 : slash.length - 1)) % slash.length);
    } else if (slash.length > 0 && event.key === 'Tab') {
      event.preventDefault();
      setText(`/${slash[at]!.words}`);
    } else if (event.key === 'Escape' && text.startsWith('/')) {
      event.preventDefault();
      setText('');
    } else if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      send();
    }
  };

  return (
    <Stack h="100%" gap="xs" p="xs" aria-label="AI" role="complementary" data-testid="ai-pane">
      <Group justify="space-between" wrap="nowrap">
        <Group gap="xs" wrap="nowrap">
          <Title order={2} size="sm">AI</Title>
          {runtime.data !== undefined && (
            <Badge size="sm" variant="light" color={runtime.data.simulated ? 'yellow' : 'blue'} data-testid="ai-runtime">{runtime.data.label}</Badge>
          )}
          {runtime.isError && <Badge size="sm" color="red">no agent: {runtime.error.message}</Badge>}
        </Group>
        <CloseButton size="sm" aria-label="Close the AI pane" onClick={() => store.getState().setOpen(false)} />
      </Group>
      <ScrollArea style={{ flex: 1 }} type="auto" offsetScrollbars>
        <Stack gap="xs">
          {cards.length === 0 && !help && (
            <Text size="sm" c="dimmed">Ask in your own words. The AI acts through Qube's commands, and what it opens appears behind your panel. Type <Kbd size="xs">/</Kbd> for chat commands.</Text>
          )}
          {help && (
            <Alert variant="light" withCloseButton onClose={() => setHelp(false)} title="What the AI can be asked">
              <Text size="sm">It calls the commands the server registers, for you, and asks for anything a command needs that you did not say. It never fills in a term you did not choose.</Text>
              {runtime.data?.simulated === true && <Text size="sm" mt={4}>{runtime.data.label}: requests such as "make a 10y JPY TONA swap, pay fixed, 3bn, fixed 12m float 12m, spread 0", "start ticking" or "stop ticking".</Text>}
            </Alert>
          )}
          {cards.map((card) => <TaskCard key={card.runId} card={card} onOpen={activate} />)}
          <div ref={end} />
        </Stack>
      </ScrollArea>
      {notice !== undefined && <Text size="xs" c="yellow" role="status" data-testid="ai-notice">{notice}</Text>}
      <div style={{ position: 'relative' }} data-ai-chat="">
        {slash.length > 0 && (
          <Paper withBorder shadow="sm" p={4} role="listbox" aria-label="Chat commands" style={{ position: 'absolute', bottom: '100%', left: 0, right: 0, marginBottom: 4, zIndex: 1 }}>
            {slash.map((c, i) => (
              <Group key={c.words} gap="xs" px={6} py={2} role="option" aria-selected={i === at} wrap="nowrap"
                style={{ cursor: 'pointer', borderRadius: 4, background: i === at ? 'var(--mantine-color-default-hover)' : undefined }}
                onMouseDown={(e) => { e.preventDefault(); runSlash(c); }}>
                <Code>/{c.words}</Code><Text size="xs" c="dimmed">{c.title}</Text>
              </Group>
            ))}
          </Paper>
        )}
        <Textarea ref={input} value={text} onChange={(e) => { setText(e.currentTarget.value); setHighlight(0); }} onKeyDown={onKey}
          placeholder={busy ? 'Answering… /stop to stop' : 'Ask the AI, or / for chat commands'} autosize minRows={1} maxRows={6}
          aria-label="Ask the AI" data-ai-chat="" />
      </div>
      {runtime.data !== undefined && runtime.data.models.length > 0 && <ModelChoice models={runtime.data.models} />}
    </Stack>
  );
}

/** The key of the user's default model (ADR-0017 §6.3), registered where models are configured. */
const DEFAULT_MODEL = 'agent.defaultModel';

/**
 * The model this conversation runs on, chosen among those configured (ADR-0017 §6.3). A choice here is the
 * conversation's alone: the user's default model, which a new conversation starts on, is in their settings.
 */
function ModelChoice({ models }: { readonly models: readonly string[] }) {
  const { store } = useAiContext();
  const id = useId();
  const userDefault = useValue(DEFAULT_MODEL);
  const shown = useAi((s) => conversationModel(s, userDefault));
  return (
    <Group gap="xs" wrap="nowrap" data-testid="ai-model-choice">
      <Text component="label" htmlFor={id} size="xs" c="dimmed">Model</Text>
      <Select id={id} size="xs" data={[...models]} value={shown ?? null} allowDeselect={false} placeholder="Choose a model"
        title="The model this conversation runs on. A new conversation starts on your default model, in your settings."
        onChange={(model) => { if (model !== null) store.getState().chooseModel(model); }} comboboxProps={{ withinPortal: true }} style={{ flex: 1 }} />
    </Group>
  );
}

function TaskCard({ card, onOpen }: { readonly card: AiCard; readonly onOpen: (panelId: string) => void }) {
  return (
    <Paper withBorder p="xs" data-testid="ai-card" data-status={card.status}>
      <Stack gap={6}>
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <Text size="sm" fw={600} style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{card.request}</Text>
          <StatusBadge status={card.status} />
        </Group>
        {card.runtime?.simulated === true && <Text size="xs" c="yellow">{card.runtime.label}</Text>}
        {card.runtime?.model !== undefined && <Text size="xs" c="dimmed" data-testid="ai-run-model">{card.runtime.label} on {card.runtime.model}</Text>}
        {card.steps.map((s) => <Step key={s.toolCallId} step={s} />)}
        {card.reply !== '' && <Text size="sm" style={{ whiteSpace: 'pre-wrap' }} data-testid="ai-reply">{card.reply}</Text>}
        {card.error !== undefined && <Text size="sm" c="red" role="alert">{card.error}</Text>}
        {card.opened.length > 0 && (
          <Group gap={4}>
            {card.opened.map((p) => <Button key={p.panelId} size="compact-xs" variant="default" onClick={() => onOpen(p.panelId)}>Show {p.title}</Button>)}
          </Group>
        )}
      </Stack>
    </Paper>
  );
}

function StatusBadge({ status }: { readonly status: AiCard['status'] }) {
  if (status === 'sending' || status === 'running') return <Loader size="xs" aria-label="answering" />;
  const color = { done: 'green', cancelled: 'gray', failed: 'red' }[status];
  return <Badge size="xs" variant="light" color={color} style={{ flexShrink: 0 }}>{status}</Badge>;
}

function Step({ step }: { readonly step: AiStep }) {
  const outcome = step.outcome;
  const name = outcome?.capability ?? step.tool;
  return (
    <Stack gap={2} data-testid="ai-step" data-tool={step.tool} data-outcome={outcome?.status ?? 'calling'}>
      <Group gap={6} wrap="nowrap">
        {outcome === undefined ? <Loader size={10} /> : <Text size="xs" c={outcome.status === 'done' ? 'green' : 'red'}>{outcome.status === 'done' ? '✓' : '✗'}</Text>}
        <Code>{name}</Code>
        {outcome?.status === 'refused' && <Text size="xs" c="red">refused: {outcome.code}</Text>}
      </Group>
      {outcome?.status === 'refused' && (
        <Stack gap={0} pl="md">
          {outcome.issues.map((i, n) => <Text key={n} size="xs" c="dimmed">{i.path === '' ? i.message : `${i.path}: ${i.message}`}</Text>)}
        </Stack>
      )}
      {outcome?.status === 'done' && (
        // An answer may be a page of rows (ADR-0016 note 38): it scrolls in its own block, and the card stays a card.
        // 30vh is geometry the theme has no name for: the most of the window one answer takes, so the conversation
        // under it stays in reach at every height.
        <Code block fz="xs" tabIndex={0} aria-label={`Answer of ${name}`} style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all', maxHeight: '30vh', overflowY: 'auto' }}>
          {JSON.stringify(outcome.outputs)}
        </Code>
      )}
    </Stack>
  );
}
