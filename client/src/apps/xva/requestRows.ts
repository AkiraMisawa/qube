import type { TypedDataRef } from '../../platform/contracts';
import { useDatasets, useRun } from '../../platform/workspace/WorkspaceProvider';
import type { RegisteredMeasure } from './contracts';

// A result tab's rows (ADR-0010 §7, §10): an output each request of the tab's measures returned beside its figures, such
// as XVA's `risks` and `profiles`, read from the Run's retained data and never recomputed. An output the Run's request
// did not return says so by name: a Run priced before the request returned it, or a request that failed.

export type RequestRows =
  | { readonly kind: 'pending' }
  | { readonly kind: 'rows'; readonly rows: readonly Readonly<Record<string, unknown>>[] }
  | { readonly kind: 'none'; readonly reason: string };

/** The rows of `output` from every request the tab's measures are priced by, in the measures' order. */
export function useRequestRows(runId: string, measures: readonly string[], register: readonly RegisteredMeasure[], output: string, what: string): RequestRows {
  const run = useRun(runId);
  const requests = [...new Set(measures.map((id) => register.find((m) => m.id === id)?.request ?? id))];
  const invocations = requests.map((request) => ({ request, invocation: run.data?.invocations.find((i) => i.stepId === request) }));
  const references = invocations.flatMap((i) => (i.invocation?.outputs[output] === undefined ? [] : [i.invocation.outputs[output] as TypedDataRef]));
  const datasets = useDatasets(references);

  if (run.isError) return { kind: 'none', reason: run.error.message };
  if (run.data === undefined) return { kind: 'pending' };
  for (const { request, invocation } of invocations) {
    if (invocation === undefined) return { kind: 'none', reason: `This Run made no ${request} request, so it has no ${what}.` };
    if (invocation.status === 'failed') return { kind: 'none', reason: invocation.error?.message ?? `The ${request} request failed, so there are no ${what}.` };
    if (invocation.outputs[output] === undefined)
      return { kind: 'none', reason: `The ${request} request of this Run returned no ${what}: it was priced before they were returned. Price again to see them.` };
  }
  const failed = datasets.find((d) => d.isError);
  if (failed?.error != null) return { kind: 'none', reason: `The ${what} could not be read: ${failed.error.message}` };
  if (datasets.some((d) => d.data === undefined)) return { kind: 'pending' };
  return { kind: 'rows', rows: datasets.flatMap((d) => (d.data === undefined ? [] : d.data.rows)) };
}
