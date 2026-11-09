import { call } from '../../platform/api';
import type { RunRecord } from '../../platform/contracts';
import type { CarrySummary, ChangesList, EffectiveTrade, Registration, Agreement, Resolution, RowPage, ScenariosMade, ScopeDrop, TradesAdded, Varied, XvaWorkspaceView } from './contracts';

export interface RowQuery {
  readonly start: number;
  readonly count: number;
  readonly sorts: readonly { readonly column: string; readonly direction: 'ascending' | 'descending' }[];
  /** The New or the Existing grid (ADR-0010 §6). */
  readonly grid: 'new' | 'existing';
}

/** A cell `vary` names: as the command line names it, or by its entity and field. */
export type VaryCell = { readonly cell: string } | { readonly entity: string; readonly field: string };

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
  /** Resolves a held edit, or removes a change (`reset`), of the Base or of the case named (ADR-0010 §3). */
  resolve(workspaceId: string, revision: number, entity: string, field: string, resolution: Resolution, caseId?: string): Promise<XvaWorkspaceView>;
  /** The Changes tab's list: each Common change, then each case with its own (ADR-0010 §3's note of 2026-11-08). */
  changes(workspaceId: string): Promise<ChangesList>;
  /** A change's value as written, read and checked by the server as the cell reads it, in the Base or the case named. */
  setChange(workspaceId: string, revision: number, entity: string, field: string, text: string, caseId?: string): Promise<XvaWorkspaceView>;
  /** Removes a case with its changes, named by its title or id (`case remove`). */
  removeCase(workspaceId: string, revision: number, name: string): Promise<XvaWorkspaceView>;
  /** Removes every case (`cases clear`). */
  clearCases(workspaceId: string, revision: number): Promise<XvaWorkspaceView>;
  changeValueDate(workspaceId: string, revision: number, valueDate: string): Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: CarrySummary }>;
  previewCounterpartyChange(workspaceId: string): Promise<ScopeDrop>;
  changeCounterparty(workspaceId: string, revision: number, registration: string, confirmedCount: number): Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: CarrySummary }>;
  /** Sets the measures Price prices; an unknown one, or none, is refused by name. */
  measures(workspaceId: string, revision: number, measures: readonly string[]): Promise<XvaWorkspaceView>;
  price(workspaceId: string, revision: number, idempotencyKey: string): Promise<RunRecord>;
  /** Asks again for the failed results on one trade, or on all (`pv01 retry`, ADR-0010 §6). */
  retry(workspaceId: string, tradeId?: string): Promise<{ readonly workspace: XvaWorkspaceView; readonly asked: number }>;
  /** Acquires the trades again for the same value date; the edits carry or are held (ADR-0010 §1). */
  refresh(workspaceId: string, revision: number): Promise<{ readonly workspace: XvaWorkspaceView; readonly carried: CarrySummary }>;
  /**
   * `vary` (ADR-0010 §3, §9): a case per value of a cell, named as the command line names it (`CSA-A threshold`) or by
   * its entity and field, as *Vary values…* names it. Each value goes as written, for the server to read and check.
   */
  vary(workspaceId: string, revision: number, cell: VaryCell, values: readonly string[]): Promise<Varied>;
  /** `set` (ADR-0010 §9): the edit a typed cell makes on each row named, in the Base; the server names the cell and reads the value. */
  set(workspaceId: string, revision: number, rows: readonly string[], column: string, text: string): Promise<XvaWorkspaceView>;
  /** `add trades` (docs/03 §5): a case per New row with A ticked, each that trade alone; a set made before is replaced. */
  addTrades(workspaceId: string, revision: number): Promise<TradesAdded>;
  /**
   * `scenario s1 jpy_ois +10bp; usdjpy -5%` (ADR-0010 §4): the scenario of that name with every shift written, as typed,
   * for the server to read; one of the same name is replaced, and the answer says so.
   */
  defineScenario(workspaceId: string, revision: number, name: string, shifts: string): Promise<ScenariosMade>;
  /** Copy quote's content, read by the server from the Run alone (ADR-0010 §7), of the market named: the base by default.
   * A scenario's quote names it, by the title the Run gave it. */
  quote(runId: string, scenario?: string): Promise<{ readonly text: string; readonly html: string; readonly scenario?: string | null }>;
}

export const httpXvaApi: XvaApi = {
  registrations: () => call('/api/xva/registrations'),
  create: (registration, valueDate, portfolioSize) => post('/api/xva/workspaces', { registration, valueDate, portfolioSize }),
  view: (workspaceId) => call(base(workspaceId)),
  rows: (workspaceId, query, signal) => post(`${base(workspaceId)}/rows`, query, signal),
  edit: (workspaceId, revision, entity, field, value) => post(`${base(workspaceId)}/edits`, { revision, entity, field, value }),
  resolve: (workspaceId, revision, entity, field, resolution, caseId) =>
    post(`${base(workspaceId)}/resolutions`, { revision, entity, field, resolution, ...(caseId === undefined ? {} : { case: caseId }) }),
  changes: (workspaceId) => call(`${base(workspaceId)}/changes`),
  setChange: (workspaceId, revision, entity, field, text, caseId) =>
    post(`${base(workspaceId)}/changes`, { revision, entity, field, text, ...(caseId === undefined ? {} : { case: caseId }) }),
  removeCase: (workspaceId, revision, name) => post(`${base(workspaceId)}/cases/remove`, { revision, name }),
  clearCases: (workspaceId, revision) => post(`${base(workspaceId)}/cases/clear`, { revision }),
  changeValueDate: (workspaceId, revision, valueDate) => post(`${base(workspaceId)}/value-date`, { revision, valueDate }),
  previewCounterpartyChange: (workspaceId) => call(`${base(workspaceId)}/counterparty-change`),
  changeCounterparty: (workspaceId, revision, registration, confirmedCount) =>
    post(`${base(workspaceId)}/counterparty`, { revision, registration, confirmedCount }),
  measures: (workspaceId, revision, measures) => post(`${base(workspaceId)}/measures`, { revision, measures }),
  price: (workspaceId, revision, idempotencyKey) => post(`${base(workspaceId)}/price`, { revision, idempotencyKey }),
  retry: (workspaceId, tradeId) => post(`${base(workspaceId)}/retry`, tradeId === undefined ? {} : { tradeId }),
  refresh: (workspaceId, revision) => post(`${base(workspaceId)}/refresh`, { revision }),
  vary: (workspaceId, revision, cell, values) => post(`${base(workspaceId)}/vary`, { revision, values, ...cell }),
  set: (workspaceId, revision, rows, column, text) => post(`${base(workspaceId)}/set`, { revision, rows, column, text }),
  addTrades: (workspaceId, revision) => post(`${base(workspaceId)}/add-trades`, { revision }),
  defineScenario: (workspaceId, revision, name, shifts) => post(`${base(workspaceId)}/scenarios`, { revision, name, shifts }),
  quote: (runId, scenario) => call(`/api/xva/runs/${encodeURIComponent(runId)}/quote${scenario === undefined ? '' : `?scenario=${encodeURIComponent(scenario)}`}`),
};

export type { EffectiveTrade };
