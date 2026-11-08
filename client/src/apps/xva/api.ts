import { call } from '../../platform/api';
import type { RunRecord } from '../../platform/contracts';
import type { CarrySummary, EffectiveTrade, Registration, Agreement, Resolution, RowPage, ScopeDrop, XvaWorkspaceView } from './contracts';

export interface RowQuery {
  readonly start: number;
  readonly count: number;
  readonly sorts: readonly { readonly column: string; readonly direction: 'ascending' | 'descending' }[];
  /** The New or the Existing grid (ADR-0010 §6). */
  readonly grid: 'new' | 'existing';
}

const base = (workspaceId: string) => `/api/xva/workspaces/${encodeURIComponent(workspaceId)}`;
const post = <T>(path: string, body: unknown, signal?: AbortSignal) =>
  call<T>(path, { method: 'POST', body: JSON.stringify(body), ...(signal === undefined ? {} : { signal }) });

/** The XVA workspace endpoints. Every command names the revision it was taken against. */
export interface XvaApi {
  registrations(): Promise<readonly (Registration & { readonly agreement: Agreement })[]>;
  create(registration: string, valueDate: string, portfolioSize: number): Promise<XvaWorkspaceView>;
  view(workspaceId: string): Promise<XvaWorkspaceView>;
  rows(workspaceId: string, query: RowQuery, signal: AbortSignal): Promise<RowPage>;
  edit(workspaceId: string, revision: number, entity: string, field: string, value: unknown): Promise<XvaWorkspaceView>;
  resolve(workspaceId: string, revision: number, entity: string, field: string, resolution: Resolution): Promise<XvaWorkspaceView>;
  changeValueDate(workspaceId: string, revision: number, valueDate: string): Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: CarrySummary }>;
  previewCounterpartyChange(workspaceId: string): Promise<ScopeDrop>;
  changeCounterparty(workspaceId: string, revision: number, registration: string, confirmedCount: number): Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: CarrySummary }>;
  /** Sets the measures Price prices; an unknown one, or none, is refused by name. */
  measures(workspaceId: string, revision: number, measures: readonly string[]): Promise<XvaWorkspaceView>;
  price(workspaceId: string, revision: number, idempotencyKey: string): Promise<RunRecord>;
  /** Asks again for the failed results on one trade, or on all (`pv01 retry`, ADR-0010 §6). */
  retry(workspaceId: string, tradeId?: string): Promise<{ readonly workspace: XvaWorkspaceView; readonly asked: number }>;
  /** Copy quote's content, read by the server from the Run alone (ADR-0010 §7). */
  quote(runId: string): Promise<{ readonly text: string; readonly html: string }>;
}

export const httpXvaApi: XvaApi = {
  registrations: () => call('/api/xva/registrations'),
  create: (registration, valueDate, portfolioSize) => post('/api/xva/workspaces', { registration, valueDate, portfolioSize }),
  view: (workspaceId) => call(base(workspaceId)),
  rows: (workspaceId, query, signal) => post(`${base(workspaceId)}/rows`, query, signal),
  edit: (workspaceId, revision, entity, field, value) => post(`${base(workspaceId)}/edits`, { revision, entity, field, value }),
  resolve: (workspaceId, revision, entity, field, resolution) => post(`${base(workspaceId)}/resolutions`, { revision, entity, field, resolution }),
  changeValueDate: (workspaceId, revision, valueDate) => post(`${base(workspaceId)}/value-date`, { revision, valueDate }),
  previewCounterpartyChange: (workspaceId) => call(`${base(workspaceId)}/counterparty-change`),
  changeCounterparty: (workspaceId, revision, registration, confirmedCount) =>
    post(`${base(workspaceId)}/counterparty`, { revision, registration, confirmedCount }),
  measures: (workspaceId, revision, measures) => post(`${base(workspaceId)}/measures`, { revision, measures }),
  price: (workspaceId, revision, idempotencyKey) => post(`${base(workspaceId)}/price`, { revision, idempotencyKey }),
  retry: (workspaceId, tradeId) => post(`${base(workspaceId)}/retry`, tradeId === undefined ? {} : { tradeId }),
  quote: (runId) => call(`/api/xva/runs/${encodeURIComponent(runId)}/quote`),
};

export type { EffectiveTrade };
