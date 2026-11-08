import { call } from '../api';
import type { TraceAddress, TraceAnswer, TraceCatalog, TraceLeavesAnswer } from './contracts';

// The trace endpoints (ADR-0011 §5): a term at a time, its leaf rows paged and sorted by the server, and the
// catalogue of kinds once per method version. A refusal arrives as an ApiError with its code, never as an empty tree.

export type ChildOrder = 'formula' | 'size';

export interface TraceApi {
  term(address: TraceAddress, path: string | undefined, options?: { readonly order?: ChildOrder; readonly offset?: number; readonly limit?: number }): Promise<TraceAnswer>;
  leaves(address: TraceAddress, path: string, options: { readonly offset: number; readonly limit: number; readonly sort?: string; readonly descending?: boolean }, signal?: AbortSignal): Promise<TraceLeavesAnswer>;
  catalog(method: string, version: string): Promise<TraceCatalog>;
}

const base = (a: TraceAddress) =>
  `/api/runs/${encodeURIComponent(a.runId)}/cases/${encodeURIComponent(a.case)}/traces/${encodeURIComponent(a.measure)}`;

const query = (entries: Readonly<Record<string, string | number | boolean | undefined>>) =>
  new URLSearchParams(Object.entries(entries).flatMap(([k, v]) => (v === undefined ? [] : [[k, String(v)]]))).toString();

/** The most rows one page of children or leaves takes; the server refuses more. */
export const MAX_PAGE = 500;

export const httpTraceApi: TraceApi = {
  term: (address, path, options = {}) =>
    call(`${base(address)}?${query({ scenario: address.scenario, path, order: options.order, offset: options.offset, limit: options.limit })}`),
  leaves: (address, path, options, signal) =>
    call(`${base(address)}/leaves?${query({ scenario: address.scenario, path, ...options })}`, signal === undefined ? undefined : { signal }),
  catalog: (method, version) => call(`/api/traces/catalogs/${encodeURIComponent(method)}/${encodeURIComponent(version)}`),
};
