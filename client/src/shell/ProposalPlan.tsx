import { Button, Code, Group, Stack, Text } from '@mantine/core';
import { CommandRefused } from '../platform/commands/commands';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { currentArrangement, useArrangementVersion } from './arrangement';
import { AREAS_FROM } from './layout';
import { arrange, arrangeUnavailable, arrangingOf, placeLabel, planOf, useProposal } from './proposals';

// A proposal of the AI on its card (ADR-0018 §8): a small plan of its areas with the panels' titles, the button that
// applies it, and the line's words for the same, `arrange <n>`. Nothing moves until the user applies it.

const percent = (fraction: number) => `${(fraction * 100).toFixed(2)}%`;

export function ProposalPlan({ call }: { readonly outputs: Readonly<Record<string, unknown>>; readonly call: string }) {
  const proposal = useProposal(call);
  const workspace = useWorkspaceContext();
  // Read again as the panels and the arrangement change: what can be applied now can be.
  useWorkspace((s) => s.panels);
  useArrangementVersion();
  if (proposal === undefined) throw new Error(`The proposal of tool call ${call} was not numbered as it arrived.`);
  const { n } = proposal;
  const arranging = arrangingOf(workspace);
  const why = arrangeUnavailable(arranging, n);
  const room = currentArrangement().room();
  const short = room.width < Math.max(AREAS_FROM, proposal.needs.width) || room.height < proposal.needs.height;
  const held = (area: string) => proposal.places.filter((p) => p.area === area);
  const described = planOf(proposal).map((a) => `${a.floating ? 'floating: ' : ''}${held(a.area).map(placeLabel).join(', ')}`).join('; ');
  const apply = () => {
    try {
      arrange(arranging, n, workspace.notify);
    } catch (error) {
      if (!(error instanceof CommandRefused)) throw error;
      workspace.notify('refused', `arrange ${n}: ${error.message}`);
    }
  };
  return (
    <Stack gap={6} data-testid="proposal" data-proposal={n}>
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Text size="xs" fw={600}>Proposal {n}</Text>
        <Text size="xs" c="dimmed">{proposal.areas.length === 1 ? '1 area' : `${proposal.areas.length} areas`} · from {Math.max(AREAS_FROM, proposal.needs.width)} × {proposal.needs.height}px</Text>
      </Group>
      <div className="qube-plan" role="img" aria-label={`Plan of proposal ${n}: ${described}`}>
        {planOf(proposal).map((a) => {
          const places = held(a.area);
          const shown = places.find((p) => p.shown === true);
          if (shown === undefined) throw new Error(`Area ${a.area} of proposal ${n} shows none of its places; the server refuses that.`);
          return (
            <div key={a.area} className="qube-plan-area" {...(a.floating ? { 'data-floating': true } : {})}
              style={{ left: percent(a.left), top: percent(a.top), width: percent(a.width), height: percent(a.height) }}>
              <span className="qube-plan-title">{placeLabel(shown)}</span>
              {places.length > 1 && <span className="qube-plan-more">+{places.length - 1}</span>}
            </div>
          );
        })}
      </div>
      <Group gap="xs" wrap="nowrap">
        <Button size="compact-xs" onClick={apply} disabled={why !== undefined}>Apply</Button>
        <Code>arrange {n}</Code>
      </Group>
      {why !== undefined && <Text size="xs" c="dimmed">{why}{short ? ' Closing this pane may give the panels the room.' : ''}</Text>}
    </Stack>
  );
}
