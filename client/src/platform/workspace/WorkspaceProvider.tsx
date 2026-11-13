import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useStore } from 'zustand';
import type { Api } from '../api';
import type { Catalog, Dataset, RunRecord, TypedDataRef } from '../contracts';
import { hearing, type Hearing, type Notify } from '../notices';
import { PanelScreens } from './panelScreens';
import { createWorkspaceStore, type WorkspaceState, type WorkspaceStore } from './workspaceStore';

interface Workspace {
  readonly api: Api;
  readonly catalog: Catalog;
  readonly store: WorkspaceStore;
  readonly notify: Notify;
  /** Hears what `notify` says (ADR-0016 note 24): the command line, kept open, says it too. */
  readonly hear: Hearing['hear'];
  /** What each open panel's page shows, kept while it is hidden (ADR-0006 note 10). */
  readonly screens: PanelScreens;
}

const WorkspaceContext = createContext<Workspace | undefined>(undefined);

export const CONTEXT_CONTRACT = 'qube.context';

export function WorkspaceProvider({ api, catalog, initialContext, notify, children }: {
  readonly api: Api;
  readonly catalog: Catalog;
  readonly initialContext: Readonly<Record<string, unknown>>;
  readonly notify: Notify;
  readonly children: ReactNode;
}) {
  const contract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT);
  if (contract === undefined) throw new Error(`No module registered the ${CONTEXT_CONTRACT} contract; the workbench has no context to bind.`);
  const [store] = useState(() => createWorkspaceStore(initialContext, contract));
  // A page's state is its panel's: a panel closed takes it with it (ADR-0006 note 10).
  const [screens] = useState(() => new PanelScreens((panelId) => store.getState().panels.some((p) => p.id === panelId)));
  useEffect(() => store.subscribe((state) => screens.keepOnly(state.panels.map((p) => p.id))), [store, screens]);
  // One value while its parts stand: a notice draws the application again, and a value made anew then would have every
  // page make its command door anew in the middle of the command that said it.
  const value = useMemo(() => { const { notify: said, hear } = hearing(notify); return { api, catalog, store, notify: said, hear, screens }; }, [api, catalog, store, notify, screens]);
  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}

export function useWorkspaceContext(): Workspace {
  const workspace = useContext(WorkspaceContext);
  if (workspace === undefined) throw new Error('useWorkspaceContext is used outside a WorkspaceProvider.');
  return workspace;
}

/** Subscribes to one slice of the workspace store; components re-render only when that slice changes. */
export function useWorkspace<T>(selector: (state: WorkspaceState) => T): T {
  return useStore(useWorkspaceContext().store, selector);
}

export function useRun(runId: string) {
  const { api } = useWorkspaceContext();
  return useQuery<RunRecord>({
    queryKey: ['run', runId],
    queryFn: () => api.run(runId),
    // A finished Run never changes; a running one is polled until it finishes.
    refetchInterval: (query) => (query.state.data?.status === 'running' ? 300 : false),
    staleTime: (query) => (query.state.data?.status === 'running' ? 0 : Infinity),
  });
}

/** Retained data is immutable by revision, so it is fetched once and never refetched. */
export function useDataset(reference: TypedDataRef | undefined) {
  const { api } = useWorkspaceContext();
  return useQuery<Dataset>({
    queryKey: ['data', reference?.dataId, reference?.revision],
    queryFn: () => {
      if (reference === undefined) throw new Error('No reference to read.');
      return api.dataset(reference);
    },
    enabled: reference !== undefined,
    staleTime: Infinity,
    retry: false,
  });
}

/** Several retained datasets at once, each fetched once and shared with `useDataset`'s cache. */
export function useDatasets(references: readonly TypedDataRef[]) {
  const { api } = useWorkspaceContext();
  return useQueries({
    queries: references.map((reference) => ({
      queryKey: ['data', reference.dataId, reference.revision],
      queryFn: () => api.dataset(reference),
      staleTime: Infinity,
      retry: false,
    })),
  });
}
