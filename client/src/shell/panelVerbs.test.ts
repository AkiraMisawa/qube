import { execute } from '../platform/commands/commands';
import { createWorkspaceStore, panelLabel } from '../platform/workspace/workspaceStore';
import { contextContract } from '../test/catalog';
import { stripArrangement } from './arrangement';
import { CLOSE_RIGHT, MOVE, PANEL_VERBS, REOPEN, onNow, panelCommands, runStripVerb, runVerb } from './panelVerbs';

// ADR-0006 note 7 of 2026-11-12: the shell's verbs over its panels, one declaration for the line, the menus and the keys.

const valid = { region: 'North', asOf: '2026-10-26' };
/** The strip's port, as on a phone: no areas. */
const STRIP = stripArrangement({ width: 390, height: 700 }, 'The panels are one strip below 768px; the arrangement comes back with more room.');

function strip(titles: readonly string[]) {
  const store = createWorkspaceStore(valid, contextContract);
  const ids = titles.map((title, i) => store.getState().openPanel({ kind: 'app', title, app: 'rates', params: { workspaceId: `w${i}` } }));
  const notify = vi.fn();
  const commands = () => panelCommands(store.getState(), notify, STRIP);
  const command = (id: string) => {
    const found = commands().find((c) => c.id === id);
    if (found === undefined) throw new Error(`No command ${id}`);
    return found;
  };
  const titlesNow = () => store.getState().panels.map(panelLabel);
  return { store, ids, notify, command, commands, titlesNow };
}

describe('panel verbs', () => {
  it('the line has a command for each verb a tab\'s menu runs, and `reopen panel`', () => {
    const ids = strip(['A']).commands().map((c) => c.id);
    for (const verb of PANEL_VERBS) expect(ids).toContain(verb.id);
    expect(ids).toContain(REOPEN.id);
  });

  it('`show panel` tells two panels of one title apart by their number, and completes each once', () => {
    const { store, ids, command } = strip(['Rates 2026-10-27', 'Rates 2026-10-27', 'Pricing DB']);
    const show = command('panel.show');
    expect(show.args?.[0]?.complete?.({})).toEqual([{ value: 'Rates 2026-10-27' }, { value: 'Rates 2026-10-27 · 2' }, { value: 'Pricing DB' }]);
    expect(execute(show, { panel: 'rates 2026-10-27 · 2' })).toBeUndefined();
    expect(store.getState().activePanelId).toBe(ids[1]);
    expect(execute(show, { panel: 'Rates 2026-10-27' })).toBeUndefined();
    expect(store.getState().activePanelId).toBe(ids[0]);
    expect(show.args?.[0]?.check?.('rates', {})).toBe('2 panels match "rates": Rates 2026-10-27, Rates 2026-10-27 · 2.');
  });

  it('`close other panels` says what it closed and kept, and is unavailable when every other panel is pinned', () => {
    const { store, ids, notify, command, titlesNow } = strip(['A', 'B', 'C', 'D']);
    store.getState().pinPanel(ids[0]!, true);
    store.getState().activate(ids[2]!);
    expect(execute(command('panel.closeOthers'))).toBeUndefined();
    expect(titlesNow()).toEqual(['A', 'C']);
    expect(notify).toHaveBeenCalledWith('done', 'Closed 2 panels. 1 pinned panel was kept. Bring one back with reopen panel.');
    expect(command('panel.closeOthers').unavailable).toBe('The other panels are pinned.');
    expect(command('panel.closeRight').unavailable).toBe('No panel is to the right of this one.');
  });

  it('`move panel` moves along the strip and refuses past its end with the reason', () => {
    const { store, ids, command, titlesNow } = strip(['A', 'B', 'C']);
    expect(execute(command('panel.move'), { where: 'first' })).toBeUndefined();
    expect(titlesNow()).toEqual(['C', 'A', 'B']);
    expect(execute(command('panel.move'), { where: 'left' })).toBe('This is the first panel already.');
    expect(execute(command('panel.move'), { where: 'right' })).toBeUndefined();
    expect(titlesNow()).toEqual(['A', 'C', 'B']);
    // C, the active panel, pinned: the only pinned panel has nowhere to move among the pinned.
    store.getState().pinPanel(ids[2]!, true);
    expect(command('panel.move').unavailable).toBe('There is no other pinned panel to move it past.');
    store.getState().pinPanel(ids[2]!, false);
    expect(execute(command('panel.move'), { where: 'up' })).toBe('move panel takes where to: left, right, first, last.');
    // The line runs none without its where: one missing is a defect thrown on, never shown as a reason.
    expect(() => execute(command('panel.move'))).toThrow('The line ran a command without its where.');
  });

  it('`move panel` with a where given says whether the panel can go there, as a menu\'s Move left asks', () => {
    const { store } = strip(['A', 'B']);
    const [a, b] = store.getState().panels;
    expect(MOVE.unavailable(onNow(store.getState(), STRIP), a!, { where: 'left' })).toBe('This is the first panel already.');
    expect(MOVE.unavailable(onNow(store.getState(), STRIP), a!, { where: 'right' })).toBeUndefined();
    expect(MOVE.unavailable(onNow(store.getState(), STRIP), b!, { where: 'last' })).toBe('This is the last panel already.');
    expect(MOVE.unavailable(onNow(store.getState(), STRIP), b!)).toBeUndefined();
  });

  it('`pin panel` and `unpin panel` are each unavailable where the other applies', () => {
    const { command } = strip(['A']);
    expect(command('panel.unpin').unavailable).toBe('This panel is not pinned.');
    expect(execute(command('panel.pin'))).toBeUndefined();
    expect(command('panel.pin').unavailable).toBe('This panel is pinned already.');
  });

  it('`reopen panel` brings the panel closed last back, and is unavailable before one is closed', () => {
    const { store, command, titlesNow } = strip(['A', 'B']);
    expect(command('panel.reopen').unavailable).toBe('No panel has been closed.');
    expect(execute(command('panel.close'))).toBeUndefined();
    expect(execute(command('panel.reopen'))).toBeUndefined();
    expect(titlesNow()).toEqual(['A', 'B']);
    expect(store.getState().panels.find((p) => p.id === store.getState().activePanelId)?.title).toBe('B');
  });

  it('a verb run from a menu that cannot act says why in a refused notice and changes nothing', () => {
    const { store, notify, titlesNow } = strip(['A', 'B']);
    const panel = store.getState().panels[0]!;
    runVerb(MOVE, onNow(store.getState(), STRIP), panel, notify, { where: 'left' });
    expect(notify).toHaveBeenCalledWith('refused', 'move panel: This is the first panel already.');
    runVerb(CLOSE_RIGHT, onNow(store.getState(), STRIP), store.getState().panels[1]!, notify);
    expect(notify).toHaveBeenLastCalledWith('refused', 'close panels to the right: No panel is to the right of this one.');
    runStripVerb(REOPEN, onNow(store.getState(), STRIP), notify);
    expect(notify).toHaveBeenLastCalledWith('refused', 'reopen panel: No panel has been closed.');
    expect(titlesNow()).toEqual(['A', 'B']);
  });

  it('every verb is unavailable on the line while no panel is open', () => {
    const { commands } = strip([]);
    for (const verb of PANEL_VERBS) expect(commands().find((c) => c.id === verb.id)?.unavailable).toBe('No panel is open.');
  });
});
