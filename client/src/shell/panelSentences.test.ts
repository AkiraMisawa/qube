import { Orientation, type SerializedDockview } from 'dockview-react';
import type { Panel } from '../platform/workspace/workspaceStore';
import { backInThisWindow } from './panelSentences';

// ADR-0018 §9: where the panels of other windows went as an arrangement was drawn with them in the main window, said
// alike after a reload and by `arrange back`.

const panel = (id: string, title: string): Panel => ({ id, kind: 'run', title, runId: `run-${id}`, viewKey: undefined });
const state = { panels: [panel('a', 'A'), panel('b', 'B'), panel('c', 'C'), panel('d', 'D')] };

/** A and C in one area, B in another; drawn after the windows' panels were docked. */
const drawn: SerializedDockview = {
  grid: {
    root: {
      type: 'branch',
      data: [
        { type: 'leaf', size: 600, data: { id: 'left', views: ['a', 'c'], activeView: 'a' } },
        { type: 'leaf', size: 600, data: { id: 'right', views: ['b', 'd'], activeView: 'b' } },
      ],
    },
    width: 1200, height: 800, orientation: Orientation.HORIZONTAL,
  },
  panels: {},
};

describe('what is said of the panels back from other windows (ADR-0018 §9)', () => {
  it('says each where it was, in the area of a panel it joined, and why where there is a reason', () => {
    expect(backInThisWindow([{ panels: ['c'], into: 'left', cameFrom: true }, { panels: ['d'], into: 'right', cameFrom: false }], state, drawn, 'as it may'))
      .toBe('Back in this window, as it may: C where it was; D in the area of B. pop out panel sends a panel out again.');
  });

  it('says panels that came back alone into an area are in an area of their own, and nothing of panels closed since', () => {
    const own: SerializedDockview = { ...drawn, grid: { ...drawn.grid, root: { type: 'branch', data: [{ type: 'leaf', size: 1200, data: { id: 'new', views: ['b', 'd'], activeView: 'b' } }] } } };
    expect(backInThisWindow([{ panels: ['b', 'd'], into: 'new', cameFrom: false }], state, own))
      .toBe('Back in this window: B and D in an area of their own. pop out panel sends a panel out again.');
    expect(backInThisWindow([{ panels: ['gone'], into: 'left', cameFrom: true }], state, drawn)).toBeUndefined();
  });
});
