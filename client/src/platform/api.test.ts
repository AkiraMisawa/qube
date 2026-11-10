import { afterEach, vi } from 'vitest';
import { httpAgentTransport } from '../ai/agui';
import { ApiError, send } from './api';
import { telemetry } from './telemetry';

// ADR-0014 §4: a call the server failed (5xx) or never answered reaches the server's log in the trace the call carried;
// a refusal (4xx) is the server answering and is not reported again. Every call goes through one path, the AI pane's
// stream included.

const answer = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
/** A fetch that answers every call with one response, keeping what it was sent. */
const fetching = (respond: () => Promise<Response>) => vi.fn(async (_path: string, _init?: RequestInit) => respond());
const sentHeader = (fetched: ReturnType<typeof fetching>, name: string) => new Headers(fetched.mock.calls[0]![1]?.headers).get(name);

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('calls to the server', () => {
  it('reports a failure of the server in the trace it sent, and throws its envelope', async () => {
    const fetched = fetching(async () => answer(503, { code: 'Unavailable', message: 'down', issues: [] }));
    vi.stubGlobal('fetch', fetched);
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => {});

    await expect(send('/api/runs', { method: 'POST' })).rejects.toMatchObject({ status: 503, envelope: { code: 'Unavailable' } });
    expect(report).toHaveBeenCalledTimes(1);
    expect(report.mock.calls[0]![0]).toMatchObject({ level: 'error', kind: 'api', message: 'POST /api/runs failed: 503 Unavailable: down' });
    expect(report.mock.calls[0]![0].traceparent).toBe(sentHeader(fetched, 'traceparent'));
  });

  it('does not report a refusal, which the server logged itself', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => answer(422, { code: 'ValidationFailed', message: 'no', issues: [] })));
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => {});

    await expect(send('/api/runs')).rejects.toBeInstanceOf(ApiError);
    expect(report).not.toHaveBeenCalled();
  });

  it('reports a call nobody answered and throws what the network said', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch'); }));
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => {});

    await expect(send('/api/catalog')).rejects.toThrow('Failed to fetch');
    expect(report.mock.calls[0]![0]).toMatchObject({ kind: 'api', message: 'GET /api/catalog was not answered: Failed to fetch' });
  });

  it("reports the AI pane's run that the server failed, as any other call", async () => {
    const fetched = fetching(async () => answer(502, { code: 'AgentHostUnavailable', message: 'no host', issues: [] }));
    vi.stubGlobal('fetch', fetched);
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => {});

    const input = { threadId: 't', runId: 'r', messages: [], tools: [], context: [], state: {}, forwardedProps: {} };
    await expect(httpAgentTransport.run(input, () => {})).rejects.toMatchObject({ status: 502 });
    expect(sentHeader(fetched, 'accept')).toBe('text/event-stream');
    expect(report.mock.calls[0]![0]).toMatchObject({ kind: 'api', message: 'POST /api/agent/runs failed: 502 AgentHostUnavailable: no host' });
  });
});
