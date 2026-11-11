import { call } from '../../platform/api';
import type { CarrySummary, CashflowsView, LiveFrame, MarketView, RatesWorkspaceView, Resolution } from './contracts';

const base = (workspaceId: string) => `/api/rates/workspaces/${encodeURIComponent(workspaceId)}`;
const post = <T>(path: string, body: unknown) => call<T>(path, { method: 'POST', body: JSON.stringify(body) });

/** A running live subscription's frames. `close` stops hearing; it does not stop the subscription. */
export interface FrameStream { close(): void }

/**
 * The Rates line pricer's own endpoints: what its page reads, and what its grids send as they are typed in, each
 * naming the revision it was taken against. Its commands (`new`, `add line`, `price`, `live start`, `live stop`,
 * `capture`, and `set`, `reset`, `remove` over its lines and defaults) are the server's, run through the page's door
 * to the command gateway (ADR-0016 notes 34 to 36), and have no endpoint here.
 */
export interface RatesApi {
  market(): Promise<MarketView>;
  create(valuationDate: string): Promise<RatesWorkspaceView>;
  view(workspaceId: string): Promise<RatesWorkspaceView>;
  editLine(workspaceId: string, revision: number, line: string, field: string, value: unknown): Promise<RatesWorkspaceView>;
  cashflows(workspaceId: string, line: string): Promise<CashflowsView>;
  editCashflow(workspaceId: string, revision: number, line: string, period: string, field: string, value: unknown): Promise<RatesWorkspaceView>;
  resolve(workspaceId: string, revision: number, scope: string, entity: string, field: string, resolution: Resolution): Promise<RatesWorkspaceView>;
  changeValuationDate(workspaceId: string, revision: number, valuationDate: string): Promise<{ readonly workspace: RatesWorkspaceView; readonly carried: CarrySummary }>;
  /** Attaches to a running subscription's frames. `onEnded` hears that the server ended the stream for good. */
  frames(subscriptionId: string, onFrame: (frame: LiveFrame) => void, onEnded: () => void): FrameStream;
}

export const httpRatesApi: RatesApi = {
  market: () => call('/api/rates/market'),
  create: (valuationDate) => post('/api/rates/workspaces', { valuationDate }),
  view: (workspaceId) => call(base(workspaceId)),
  editLine: (workspaceId, revision, line, field, value) => post(`${base(workspaceId)}/lines/${encodeURIComponent(line)}/edits`, { revision, field, value }),
  cashflows: (workspaceId, line) => call(`${base(workspaceId)}/lines/${encodeURIComponent(line)}/cashflows`),
  editCashflow: (workspaceId, revision, line, period, field, value) =>
    post(`${base(workspaceId)}/lines/${encodeURIComponent(line)}/cashflows/edits`, { revision, period, field, value }),
  resolve: (workspaceId, revision, scope, entity, field, resolution) => post(`${base(workspaceId)}/resolutions`, { revision, scope, entity, field, resolution }),
  changeValuationDate: (workspaceId, revision, valuationDate) => post(`${base(workspaceId)}/valuation-date`, { revision, valuationDate }),
  frames: (subscriptionId, onFrame, onEnded) => {
    const source = new EventSource(`/api/rates/live/${encodeURIComponent(subscriptionId)}/frames`);
    source.addEventListener('frame', (event) => onFrame(JSON.parse((event as MessageEvent<string>).data) as LiveFrame));
    // A dropped connection is retried by the browser, and the server answers it with the frame shown now. When
    // the subscription has stopped (by Stop, or idle), the retry is refused and the browser gives up: the page
    // then reads the workspace again to learn why.
    source.onerror = () => { if (source.readyState === EventSource.CLOSED) onEnded(); };
    return { close: () => source.close() };
  },
};
