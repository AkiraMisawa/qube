import { call } from '../../platform/api';
import type { CdsCatalogue, ChangesList, EffectiveTrade, PackageRoles, PricePlan, Registration, Agreement, Resolution, RowPage, ScopeDrop, ShiftEdit, XvaWorkspaceView } from './contracts';

export interface RowQuery {
  readonly start: number;
  readonly count: number;
  readonly sorts: readonly { readonly column: string; readonly direction: 'ascending' | 'descending' }[];
  /** The New or the Existing grid (ADR-0010 §6). */
  readonly grid: 'new' | 'existing';
  /** The conditions the grid holds from the line (ADR-0016 note 41), as typed: the page is of the rows they leave. */
  readonly where?: string;
}

const base = (workspaceId: string) => `/api/xva/workspaces/${encodeURIComponent(workspaceId)}`;
const post = <T>(path: string, body: unknown, signal?: AbortSignal) =>
  call<T>(path, { method: 'POST', body: JSON.stringify(body), ...(signal === undefined ? {} : { signal }) });

/**
 * The XVA workspace endpoints the page itself calls: its reads, and the edits of its cells. Every edit names the
 * revision it was taken against. What a command does (Price, Refresh, a scenario by name, the value date and the
 * counterparty; ADR-0016 notes 32, 33 and 100) is not here: it goes through the page's door to the command gateway.
 */
export interface XvaApi {
  registrations(): Promise<readonly (Registration & { readonly agreement: Agreement })[]>;
  /** A new workspace; one on the dummy counterparty is made only under a project (ADR-0010 §1's note of 2026-11-14). */
  create(registration: string, valueDate: string, portfolioSize: number, project: string | null): Promise<XvaWorkspaceView>;
  view(workspaceId: string): Promise<XvaWorkspaceView>;
  rows(workspaceId: string, query: RowQuery, signal: AbortSignal): Promise<RowPage>;
  edit(workspaceId: string, revision: number, entity: string, field: string, value: unknown): Promise<XvaWorkspaceView>;
  /** Resolves a held edit, or removes a change (`reset`), of the Base or of the case named (ADR-0010 §3). */
  resolve(workspaceId: string, revision: number, entity: string, field: string, resolution: Resolution, caseId?: string): Promise<XvaWorkspaceView>;
  /** The Changes tab's list: each Common change, then each case with its own (ADR-0010 §3's note of 2026-11-08). */
  changes(workspaceId: string): Promise<ChangesList>;
  /** A change's value as written, read and checked by the server as the cell reads it, in the Base or the case named. */
  setChange(workspaceId: string, revision: number, entity: string, field: string, text: string, caseId?: string): Promise<XvaWorkspaceView>;
  /** What a change of counterparty would drop, by kind, for the toolbar's confirmation (ADR-0003 §6). */
  previewCounterpartyChange(workspaceId: string): Promise<ScopeDrop>;
  /** What Price would price as the workspace stands, for Price's confirmation (ADR-0010 §3's note of 2026-11-19). */
  pricePlan(workspaceId: string): Promise<PricePlan>;
  /** Which of the trades named a package would add and which it would take out, for *Make package…* (ADR-0016 note 96). */
  packageRoles(workspaceId: string, trades: readonly string[]): Promise<PackageRoles>;
  /** The Scenarios grid's edits as one batch (ADR-0010 §4): a cell typed, a block pasted, or cells cleared. */
  editShifts(workspaceId: string, revision: number, rows: readonly ShiftEdit[]): Promise<XvaWorkspaceView>;
  /** The CDS catalogue the Credit tab searches: its single names, a proxy's keys and the tenors. */
  cdsCatalogue(): Promise<CdsCatalogue>;
  /** Copy quote's content, read by the server from the Run alone (ADR-0010 §7), of the market named: the base by default.
   * A scenario's quote names it, by the title the Run gave it. */
  quote(runId: string, scenario?: string): Promise<{ readonly text: string; readonly html: string; readonly scenario?: string | null }>;
}

export const httpXvaApi: XvaApi = {
  registrations: () => call('/api/xva/registrations'),
  create: (registration, valueDate, portfolioSize, project) => post('/api/xva/workspaces', { registration, valueDate, portfolioSize, project }),
  view: (workspaceId) => call(base(workspaceId)),
  rows: (workspaceId, query, signal) => post(`${base(workspaceId)}/rows`, query, signal),
  edit: (workspaceId, revision, entity, field, value) => post(`${base(workspaceId)}/edits`, { revision, entity, field, value }),
  resolve: (workspaceId, revision, entity, field, resolution, caseId) =>
    post(`${base(workspaceId)}/resolutions`, { revision, entity, field, resolution, ...(caseId === undefined ? {} : { case: caseId }) }),
  changes: (workspaceId) => call(`${base(workspaceId)}/changes`),
  setChange: (workspaceId, revision, entity, field, text, caseId) =>
    post(`${base(workspaceId)}/changes`, { revision, entity, field, text, ...(caseId === undefined ? {} : { case: caseId }) }),
  previewCounterpartyChange: (workspaceId) => call(`${base(workspaceId)}/counterparty-change`),
  pricePlan: (workspaceId) => call(`${base(workspaceId)}/price-plan`),
  packageRoles: (workspaceId, trades) => post(`${base(workspaceId)}/package-roles`, { trades }),
  editShifts: (workspaceId, revision, rows) => post(`${base(workspaceId)}/shifts`, { revision, rows }),
  cdsCatalogue: () => call('/api/xva/cds'),
  quote: (runId, scenario) => call(`/api/xva/runs/${encodeURIComponent(runId)}/quote${scenario === undefined ? '' : `?scenario=${encodeURIComponent(scenario)}`}`),
};

export type { EffectiveTrade };
