import { createContext, useContext, useEffect, useState, type Dispatch, type SetStateAction } from 'react';
import { useWorkspaceContext } from './workspace/WorkspaceProvider';
import type { PanelKey } from './workspace/panelScreens';

// The hooks by which a page keeps what it shows by its panel (ADR-0006 note 10); what is kept, and for how long, is
// `PanelScreens`'.

export { panelKey, type PanelKey } from './workspace/panelScreens';

const PanelOf = createContext<string | undefined>(undefined);

/** The panel a page is drawn in, for what it keeps (`PanelPage` provides it). */
export const PanelIdProvider = PanelOf.Provider;

/** The id of the panel this component is drawn in. */
export function usePanelId(): string {
  const panelId = useContext(PanelOf);
  if (panelId === undefined) throw new Error('A value kept by panel is read outside a panel\'s page.');
  return panelId;
}

/**
 * React's `useState`, kept by the panel the component is drawn in: drawn again after its panel was hidden, the component
 * starts from the value it last had there, and from `initial` in a panel that kept none. `instance` tells apart two
 * components of one kind in one panel (the trace of each measure).
 */
export function usePanelState<T>(key: PanelKey<T>, initial: T | (() => T), instance?: string): [T, Dispatch<SetStateAction<T>>] {
  const panelId = usePanelId();
  const { screens } = useWorkspaceContext();
  const [value, setValue] = useState<T>(() => {
    if (screens.has(panelId, key, instance)) return screens.get(panelId, key, instance);
    return initial instanceof Function ? initial() : initial;
  });
  useEffect(() => { screens.set(panelId, key, value, instance); }, [screens, panelId, key, value, instance]);
  return [value, setValue];
}

/**
 * Whether the panel kept a value under this key as the component was first drawn here: drawn again after its panel was
 * hidden, rather than opened. Read once, as the component is first drawn.
 */
export function useKeptBefore(key: PanelKey<unknown>, instance?: string): boolean {
  const panelId = usePanelId();
  const { screens } = useWorkspaceContext();
  const [kept] = useState(() => screens.has(panelId, key, instance));
  return kept;
}
