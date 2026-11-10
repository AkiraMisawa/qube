import { ClientTelemetry, MAX_BATCH, MAX_QUEUED, newTraceparent } from './telemetry';

const at = () => new Date('2026-10-28T12:00:00Z');

// ADR-0014 §4: the browser reports to its own server, in bounded batches, in the trace of the call an event is about.
describe('client telemetry', () => {
  it('makes a W3C trace context with non-zero ids', () => {
    expect(newTraceparent()).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
    // A zero id is drawn again rather than sent.
    let draws = 0;
    const random = (bytes: Uint8Array) => { if (draws++ > 0) bytes.fill(1); return bytes; };
    expect(newTraceparent(random)).toBe(`00-${'01'.repeat(16)}-${'01'.repeat(8)}-01`);
  });

  it('sends what it holds in batches the server takes, under one session', async () => {
    const sent: string[] = [];
    const t = new ClientTelemetry(async (body) => { sent.push(body); }, at, () => '/', 60_000, 'page-1');
    for (let i = 0; i < MAX_BATCH + 1; i++) t.report({ level: 'error', kind: 'api', message: `m${i}` });
    await t.flush();
    const batches = sent.map((b) => JSON.parse(b) as { session: string; events: { message: string; url: string }[] });
    expect(batches.map((b) => b.events.length)).toEqual([MAX_BATCH, 1]);
    expect(batches.every((b) => b.session === 'page-1')).toBe(true);
    expect(batches[0]!.events[0]).toMatchObject({ message: 'm0', url: '/', at: '2026-10-28T12:00:00.000Z' });
  });

  it('keeps the first events when it holds too many, and says how many it dropped', async () => {
    const sent: string[] = [];
    const t = new ClientTelemetry(async (body) => { sent.push(body); }, at, () => '/', 60_000, 'page-1');
    for (let i = 0; i < MAX_QUEUED + 3; i++) t.report({ level: 'error', kind: 'api', message: `m${i}` });
    await t.flush();
    const events = sent.flatMap((b) => (JSON.parse(b) as { events: { kind: string; message: string }[] }).events);
    expect(events).toHaveLength(MAX_QUEUED + 1);
    expect(events[0]!.message).toBe('m0');
    expect(events.at(-1)).toMatchObject({ kind: 'dropped', message: '3 event(s) were dropped while the server was not reached.' });
  });

  it('never throws when the server does not take a batch', async () => {
    const t = new ClientTelemetry(async () => { throw new Error('offline'); }, at, () => '/', 60_000, 'page-1');
    t.report({ level: 'error', kind: 'api', message: 'm' });
    await expect(t.flush()).resolves.toBeUndefined();
  });
});
