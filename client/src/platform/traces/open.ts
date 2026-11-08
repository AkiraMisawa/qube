import { createStore } from 'zustand/vanilla';
import type { WorkspaceState } from '../workspace/workspaceStore';
import type { TraceAddress } from './contracts';

// Opening a trace (ADR-0011 §6, §7): one panel per trace (Run, case, scenario, measure), and a request to show a term
// in it, which a Metrics cell, `explain` and the panel's own links all make. The panel's parameters name the trace
// only, so asking for another term of an open trace shows it in that panel rather than opening a second one.

export const TRACE_VIEW = 'trace.view';

/** The case and scenario a Price of one case writes its traces under (ADR-0011 §5, DEFAULT 2026-11-08). */
export const BASE_CASE = 'base';
export const BASE_SCENARIO = 'base';

export const traceParams = (a: TraceAddress): Readonly<Record<string, string>> =>
  ({ runId: a.runId, case: a.case, scenario: a.scenario, measure: a.measure });

/** A panel's trace, from its parameters; refused by name when one is missing. */
export function traceAddressOf(params: Readonly<Record<string, string>>): TraceAddress {
  const { runId, case: caseId, scenario, measure } = params;
  if (runId === undefined || caseId === undefined || scenario === undefined || measure === undefined)
    throw new Error(`A trace panel names its Run, case, scenario and measure; it was given ${JSON.stringify(params)}.`);
  return { runId, case: caseId, scenario, measure };
}

/** The term each trace panel was last asked to show, numbered so that asking for the same term again shows it again. */
export interface TermRequest { readonly path: string | undefined; readonly number: number }

export const termRequests = createStore<{ readonly requests: Readonly<Record<string, TermRequest>> }>(() => ({ requests: {} }));
let asked = 0;

/** Asks the trace panel to show a term; none is its first root. */
export function requestTerm(panelId: string, path: string | undefined): void {
  termRequests.setState((s) => ({ requests: { ...s.requests, [panelId]: { path, number: ++asked } } }));
}

/** Opens a trace in its panel, or brings its panel forward, and shows the term asked for. */
export function openTrace(state: Pick<WorkspaceState, 'showApp'>, address: TraceAddress, title: string, path?: string): string {
  const panelId = state.showApp(TRACE_VIEW, title, traceParams(address));
  requestTerm(panelId, path);
  return panelId;
}

/** `explain <measure> [path]`: the measure, then a term's path, which may hold spaces (`NS-001 CSA-A/m`). */
export function explainTyped(text: string): { readonly measure: string; readonly path: string | undefined } {
  const [measure, ...rest] = text.trim().split(/\s+/);
  if (measure === undefined || measure === '') throw new Error('explain needs the measure to explain, then the term if one is wanted.');
  const path = rest.join(' ');
  return { measure, path: path === '' ? undefined : path };
}
