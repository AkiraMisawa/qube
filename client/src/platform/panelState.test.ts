import { createElement, type ReactNode } from 'react';
import { act, renderHook } from '@testing-library/react';
import { panelKey, PanelPlaceProvider, useKeptBefore, usePanelState } from './panelState';
import { PanelScreens } from './workspace/panelScreens';

const TERM = panelKey<string>('test.state.term');

const placed = (screens: PanelScreens) => ({ children }: { readonly children: ReactNode }) =>
  createElement(PanelPlaceProvider, { value: { panelId: 'panel-1', screens } }, children);

describe('ADR-0006 note 10: a component\'s state kept by its panel', () => {
  it('starts from what the panel kept, drawn again, and from its initial value where the panel kept none', () => {
    const screens = new PanelScreens(() => true);
    const first = renderHook(() => usePanelState(TERM, 'root'), { wrapper: placed(screens), reactStrictMode: true });
    expect(first.result.current[0]).toBe('root');
    act(() => first.result.current[1]('Risk weight'));
    first.unmount();
    const again = renderHook(() => [usePanelState(TERM, 'root')[0], useKeptBefore(TERM)] as const, { wrapper: placed(screens) });
    expect(again.result.current).toEqual(['Risk weight', true]);
  });

  it('starts from what another instance kept when a component is given it without being drawn anew', () => {
    const screens = new PanelScreens(() => true);
    const drawn = renderHook(({ instance }) => [usePanelState(TERM, 'root', instance), useKeptBefore(TERM, instance)] as const, {
      wrapper: placed(screens), initialProps: { instance: 'CVA' },
    });
    act(() => drawn.result.current[0][1]('EAD'));
    drawn.rerender({ instance: 'FVA' });
    expect(drawn.result.current[0][0]).toBe('root');
    expect(drawn.result.current[1]).toBe(false);
    drawn.rerender({ instance: 'CVA' });
    expect(drawn.result.current[0][0]).toBe('EAD');
    expect(drawn.result.current[1]).toBe(true);
  });
});
