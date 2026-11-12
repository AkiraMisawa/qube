import { useEffect, useRef, useState, type RefObject } from 'react';
import { Kbd, Stack, Text } from '@mantine/core';
import { useElementSize } from '@mantine/hooks';
import { registerAiAnswer, registerAiEffect, registerAiSharedContext } from '../ai/effects';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { stripReason } from './arrangement';
import { useKeptLayout } from './keptPanels';
import { fits, needs } from './layout';
import { PanelAreas } from './PanelAreas';
import { PanelStrip } from './PanelStrip';
import { ProposalPlan } from './ProposalPlan';
import { panelsContext, takeProposal } from './proposals';

// The panels' space (ADR-0018 §5): the window less the header and the AI pane. From 768px wide, where the kept
// arrangement fits, the panels stand in areas (`PanelAreas`); else they are note 7's one strip (`PanelStrip`), and the
// arrangement waits for the room. The space is read as the browser reports it when it changes, never measured on the
// way to a paint.

// The AI's proposal of an arrangement (ADR-0018 §8): numbered as it arrives, and drawn on its card as a plan with Apply.
// It opens no panel and moves none.
registerAiEffect('layout.propose', (outputs, { call }) => {
  takeProposal(call, outputs);
  return undefined;
});
registerAiAnswer('layout.propose', ProposalPlan);

export function Panels() {
  const { store } = useWorkspaceContext();
  // Every request to the AI carries the open panels and their arrangement, so that it can name them in a proposal.
  useEffect(() => registerAiSharedContext(() => panelsContext(store.getState())), [store]);
  const count = useWorkspace((s) => s.panels.length);
  const kept = useKeptLayout();
  // The width is the panels' own, edge to edge, with the document's scrollbar where it takes room: the strip's page
  // scrolls the document, the areas do not, so without it a window a scrollbar's width past 768px would stay in
  // whichever it was drawn in first. The height is the window's below the header, which the strip does not fill but
  // would have.
  const { ref, width } = usePanelsWidth();
  const { ref: below, height } = useElementSize<HTMLDivElement>();
  const room = { width, height };
  const measured = width > 0 && height > 0;
  const areas = measured && count > 0 && fits(kept, room);
  return (
    <div ref={ref} className="qube-panels" data-mode={areas ? 'areas' : 'strip'}>
      <div ref={below} className="qube-panels-room" aria-hidden />
      {count === 0 ? <NothingOpen /> : !measured ? null : areas ? <PanelAreas room={room} /> : (
        <PanelStrip room={room} why={stripReason(room, kept === undefined ? undefined : needs(kept))} />
      )}
    </div>
  );
}

/**
 * The panels' width edge to edge, with the width the document's own scrollbar takes from the window (none where the
 * document does not scroll, or where the scrollbar is drawn over the page: macOS, a phone). Both are read in one
 * callback, as the browser reports a change of the panels' box, the document's or the window's: a scrollbar coming or
 * going changes the two in one layout, and read apart they would add up, for a render, to a width neither layout had,
 * which drew the areas and the strip by turns.
 */
function usePanelsWidth(): { readonly ref: RefObject<HTMLDivElement | null>; readonly width: number } {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  useEffect(() => {
    const panels = ref.current;
    if (panels === null) throw new Error('The panels\' space is not drawn.');
    const html = document.documentElement;
    const read = () => setWidth(panels.getBoundingClientRect().width + window.innerWidth - html.clientWidth);
    const observer = new ResizeObserver(read);
    observer.observe(panels);
    observer.observe(html);
    window.addEventListener('resize', read);
    return () => { observer.disconnect(); window.removeEventListener('resize', read); };
  }, []);
  return { ref, width };
}

function NothingOpen() {
  return (
    <Stack align="center" mt="xl" gap="xs">
      <Text c="dimmed">Nothing open yet.</Text>
      {/* The key is said where a keyboard is likely (docs/design-guidelines.md §8); a phone opens the line from the header. */}
      <Text c="dimmed" size="sm"><Text span inherit visibleFrom="sm">Press <Kbd>Ctrl</Kbd> + <Kbd>K</Kbd></Text><Text span inherit hiddenFrom="sm">Open Commands</Text> and type what you want to do: <Kbd>new rates</Kbd>, <Kbd>new xva</Kbd>, <Kbd>open pricing db</Kbd>, or <Kbd>?</Kbd> for every command.</Text>
    </Stack>
  );
}
