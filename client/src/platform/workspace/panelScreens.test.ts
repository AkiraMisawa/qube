import { panelKey, PanelScreens } from './panelScreens';

const TAB = panelKey<string>('test.tab');
const OPEN = panelKey<readonly string[]>('test.open');
const PICKED = panelKey<string | undefined>('test.picked');

// ADR-0006 note 10: what a page shows is kept by its panel while the panel is open, and goes with it.
describe('what a page keeps by its panel', () => {
  it('keeps a value by panel, key and instance, apart from every other', () => {
    const screens = new PanelScreens();
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

  it('drops what a closed panel kept, so a panel reopened comes back as a page opened anew', () => {
    const screens = new PanelScreens();
    screens.set('panel-1', TAB, 'csa');
    screens.set('panel-2', TAB, 'credit');
    screens.keepOnly(['panel-2']);
    expect(screens.has('panel-1', TAB)).toBe(false);
    expect(screens.get('panel-2', TAB)).toBe('credit');
  });

  it('refuses a value asked of a panel that kept none, and a second key of one name', () => {
    const screens = new PanelScreens();
    expect(() => screens.get('panel-1', TAB)).toThrow('Panel panel-1 kept nothing under test.tab.');
    expect(() => panelKey<number>('test.tab')).toThrow('Two values a page keeps by panel are named test.tab.');
  });
});
