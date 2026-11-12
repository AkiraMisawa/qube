import { Button, Divider, Kbd, Title } from '@mantine/core';
import { IconSparkles, IconTerminal2 } from '@tabler/icons-react';
import { useAi, useAiContext } from '../ai/AiProvider';
import { AccountMenu } from './AccountMenu';
import { ICON, STROKE } from './icons';
import { openPalette } from './CommandPalette';
import { ContextBar } from './ContextBar';

/**
 * The header's height on each breakpoint, which AppShell reserves above the main area. It is fixed, never `auto`:
 * AppShell offsets the main area by this number, so a header that grows past it covers the panel below
 * (docs/design-guidelines.md, "The shell"). One row of Control Height (30px), centred, from `md` up; below `md`,
 * two such rows 8px apart. Each number includes the header's 1px bottom border.
 */
export const HEADER_HEIGHT = { base: 82, md: 44 } as const;

/**
 * Three zones on one row: what this is and how to command it, the context every run is bound to, and what is the
 * user's own: the AI pane and their account. Every control is one Control Height tall, so their edges line up with
 * each other and with the inputs' (docs/design-guidelines.md, "Alignment"). Below `md` the context moves to a second
 * row of its own.
 */
export function AppHeader() {
  const aiOpen = useAi((s) => s.open);
  const ai = useAiContext().store;
  return (
    <div className="qube-header">
      <div className="qube-header-brand">
        <Title order={1} className="qube-header-title">QUBE</Title>
        <Button size="xs" variant="default" onClick={openPalette} aria-label="Open command line"
          leftSection={<IconTerminal2 size={ICON} stroke={STROKE} aria-hidden />}
          rightSection={<Kbd size="xs" visibleFrom="sm">Ctrl K</Kbd>}>Commands</Button>
      </div>
      <ContextBar className="qube-header-context" />
      <div className="qube-header-actions">
        <Divider orientation="vertical" visibleFrom="md" className="qube-header-divider" />
        <Button size="xs" variant={aiOpen ? 'light' : 'default'} onClick={() => ai.getState().setOpen(!aiOpen)}
          aria-pressed={aiOpen} aria-label={aiOpen ? 'Hide the AI pane' : 'Show the AI pane'}
          leftSection={<IconSparkles size={ICON} stroke={STROKE} aria-hidden />}>AI</Button>
        <AccountMenu />
      </div>
    </div>
  );
}
