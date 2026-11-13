import { panelKey, PanelScreens } from './panelScreens';

const TAB = panelKey<string>('test.tab');
const OPEN = panelKey<readonly string[]>('test.open');
const PICKED = panelKey<string | undefined>('test.picked');

/** Screens whose panels are the ones named open, as their store would say. */
function screensOf(open: Set<string>): PanelScreens {
  return new PanelScreens((panelId) => open.has(panelId));
}

describe('ADR-0006 note 10: what a page keeps by its panel', () => {
  it('keeps a value by panel, key and instance, apart from every other', () => {
    const screens = screensOf(new Set(['panel-1', 'panel-2', 'panel-3']));
    screens.set('panel-1', TAB, 'csa');
    screens.set('panel-1', OPEN, ['a'], 'trace/CVA');
    screens.set('panel-2', TAB, 'credit');

    expect(screens.get('panel-1', TAB)).toBe('csa');
    expect(screens.get('panel-2', TAB)).toBe('credit');
    expect(screens.get('panel-1', OPEN, 'trace/CVA')).toEqual(['a']);
    expect(screens.has('panel-1', OPEN)).toBe(false);
    expect(screens.has('panel-1', OPEN, 'trace/FVA')).toBe(false);
    // A value kept as undefined is kept: the page had it so.
    screens.set('panel-3', PICKED, undefined);
    expect(screens.has('panel-3', PICKED)).toBe(true);
    expect(screens.get('panel-3', PICKED)).toBeUndefined();
  });

  it('drops what a closed panel kept, and keeps nothing its page writes as it goes after the close: a panel reopened comes back as a page opened anew', () => {
    const open = new Set(['panel-1', 'panel-2']);
    const screens = screensOf(open);
    screens.set('panel-1', TAB, 'csa');
    screens.set('panel-2', TAB, 'credit');
    open.delete('panel-1');
    screens.keepOnly(open);
    expect(screens.has('panel-1', TAB)).toBe(false);
    expect(screens.get('panel-2', TAB)).toBe('credit');
    // The closed panel's page is unmounted after the store said it closed, and writes what it showed.
    screens.set('panel-1', TAB, 'csa');
    expect(screens.has('panel-1', TAB)).toBe(false);
  });

  it('refuses a value asked of a panel that kept none, and a second key of one name', () => {
    const screens = screensOf(new Set(['panel-1']));
    expect(() => screens.get('panel-1', TAB)).toThrow('Panel panel-1 kept nothing under test.tab.');
    expect(() => panelKey<number>('test.tab')).toThrow('Two values a page keeps by panel are named test.tab.');
  });
});
