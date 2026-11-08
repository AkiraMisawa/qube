import { createStore } from 'zustand/vanilla';
import type { WorkspaceState } from '../workspace/workspaceStore';
import type { ChildOrder } from './api';
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

/**
 * How a trace view is drawn, which its controls and the trace commands set alike (ADR-0006): kept per view, so that a
 * card chosen, or another term selected, keeps it.
 */
export interface TraceViewSettings {
  readonly order: ChildOrder;
  /** The rows below the selected term: by entity, or row by row (ADR-0012 §6). */
  readonly below: 'entities' | 'rows';
  /** Whether the parameters scoped to the terms above the selected one are shown (ADR-0011 §6's note of 2026-11-08). */
  readonly above: boolean;
}

export const DEFAULT_TRACE_VIEW: TraceViewSettings = { order: 'formula', below: 'entities', above: false };

export const traceViews = createStore<{ readonly views: Readonly<Record<string, TraceViewSettings>> }>(() => ({ views: {} }));

/** Changes how a trace view draws: a trace panel's, or a page's trace tab's, by the id its term requests take. */
export function setTraceView(viewId: string, change: Partial<TraceViewSettings>): void {
  traceViews.setState((s) => ({ views: { ...s.views, [viewId]: { ...(s.views[viewId] ?? DEFAULT_TRACE_VIEW), ...change } } }));
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

/**
 * The `explain` line that comes back to a term (ADR-0011 §7), or none where the path would not read back as one: a path
 * with a run of spaces, or one at either end, is not what `explainTyped` reads.
 */
export function explainLine(measure: string, path: string): string | undefined {
  const line = `${measure} ${path}`;
  const read = explainTyped(line);
  return read.measure === measure && read.path === path ? `explain ${line}` : undefined;
}
