import { createContext, useCallback, useContext, useEffect, useState, type Dispatch, type SetStateAction } from 'react';
import type { PanelKey, PanelScreens } from './workspace/panelScreens';

// The hooks by which a page keeps what it shows by its panel (ADR-0006 note 10); what is kept, and for how long, is
// `PanelScreens`'.

export { panelKey, type PanelKey } from './workspace/panelScreens';

/** The panel a page is drawn in, and what the panels keep of their pages: `PanelPage` provides it. */
export interface PanelPlace {
  readonly panelId: string;
  readonly screens: PanelScreens;
}

const PanelOf = createContext<PanelPlace | undefined>(undefined);

/** Tells a page which panel it is drawn in, and where its panel keeps what it shows. */
export const PanelPlaceProvider = PanelOf.Provider;

/** The panel this component is drawn in, and what the panels keep. */
export function usePanelPlace(): PanelPlace {
  const place = useContext(PanelOf);
  if (place === undefined) throw new Error('A value kept by panel is read outside a panel\'s page.');
  return place;
}

/** Names one value of one panel's page: a component given another names another, and starts from what that one kept. */
const slotOf = (panelId: string, key: PanelKey<unknown>, instance: string | undefined): string => JSON.stringify([panelId, key.name, instance ?? null]);

/**
 * React's `useState`, kept by the panel the component is drawn in: drawn again after its panel was hidden, the component
 * starts from the value it last had there, and from `initial` in a panel that kept none. `instance` tells apart two
 * components of one kind in one panel (the trace of each measure); a component given another instance as it draws again
 * starts from what that instance kept, as one drawn anew would.
 */
export function usePanelState<T>(key: PanelKey<T>, initial: T | (() => T), instance?: string): [T, Dispatch<SetStateAction<T>>] {
  const { panelId, screens } = usePanelPlace();
  const slot = slotOf(panelId, key, instance);
  const start = (): T => {
    if (screens.has(panelId, key, instance)) return screens.get(panelId, key, instance);
    return initial instanceof Function ? initial() : initial;
  };
  const [held, setHeld] = useState(() => ({ slot, value: start() }));
  let current = held;
  if (held.slot !== slot) {
    current = { slot, value: start() };
    setHeld(current);
  }
  const value = current.value;
  useEffect(() => { screens.set(panelId, key, value, instance); }, [screens, panelId, key, value, instance]);
  const setValue = useCallback<Dispatch<SetStateAction<T>>>((action) => {
    setHeld((before) => ({ slot: before.slot, value: action instanceof Function ? action(before.value) : action }));
  }, []);
  return [value, setValue];
}

/**
 * Whether the panel kept a value under this key as the component was first drawn here, or first given this instance:
 * drawn again after its panel was hidden, rather than opened.
 */
export function useKeptBefore(key: PanelKey<unknown>, instance?: string): boolean {
  const { panelId, screens } = usePanelPlace();
  const slot = slotOf(panelId, key, instance);
  const [held, setHeld] = useState(() => ({ slot, kept: screens.has(panelId, key, instance) }));
  if (held.slot === slot) return held.kept;
  const next = { slot, kept: screens.has(panelId, key, instance) };
  setHeld(next);
  return next.kept;
}
