// The browser's part of ADR-0014: what goes wrong on the page reaches the server's log, joined to the trace of the call
// it is about. No SDK: each call to the server carries a W3C `traceparent` header, and events are sent in small
// batches to /api/telemetry/client, which writes them into the server's log as `Qube.Client`.

export type ClientLevel = 'error' | 'warning' | 'info';

export interface ClientEvent {
  readonly level: ClientLevel;
  /** What happened, in lower-case words joined by hyphens: `uncaught`, `rejection`, `render`, `api`, `notice`. */
  readonly kind: string;
  readonly message: string;
  readonly at: string;
  readonly url: string;
  readonly traceparent?: string;
  readonly stack?: string;
}

export type ReportedEvent = Omit<ClientEvent, 'at' | 'url'>;

/** The server takes at most this many events in one batch. */
export const MAX_BATCH = 50;
/** Events held while the server is not reached; past this, new ones are counted and dropped, the first kept. */
export const MAX_QUEUED = 200;

const hex = (bytes: Uint8Array) => Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');

/** A new W3C trace context for one call: a random trace id and span id, sampled. */
export function newTraceparent(random: (bytes: Uint8Array<ArrayBuffer>) => Uint8Array = (b) => crypto.getRandomValues(b)): string {
  const id = (length: number) => {
    // An all-zero id is invalid; drawing it again is as good as never drawing it.
    for (;;) {
      const value = hex(random(new Uint8Array(length)));
      if (/[^0]/.test(value)) return value;
    }
  };
  return `00-${id(16)}-${id(8)}-01`;
}

export class ClientTelemetry {
  readonly session: string;
  private queue: ClientEvent[] = [];
  private dropped = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(
    private readonly send: (body: string) => Promise<void>,
    private readonly now: () => Date = () => new Date(),
    private readonly where: () => string = () => location.pathname,
    private readonly flushAfterMs = 5000,
    session: string = crypto.randomUUID(),
  ) {
    this.session = session;
  }

  report(event: ReportedEvent): void {
    if (this.queue.length >= MAX_QUEUED) { this.dropped++; return; }
    this.queue.push({ ...event, at: this.now().toISOString(), url: this.where() });
    this.timer ??= setTimeout(() => void this.flush(), this.flushAfterMs);
  }

  /** Sends what is held, a batch at a time. A batch the server does not take is not retried: it is not worth a loop. */
  async flush(): Promise<void> {
    if (this.timer !== undefined) { clearTimeout(this.timer); this.timer = undefined; }
    if (this.dropped > 0) {
      this.queue.push({ level: 'warning', kind: 'dropped', message: `${this.dropped} event(s) were dropped while the server was not reached.`, at: this.now().toISOString(), url: this.where() });
      this.dropped = 0;
    }
    while (this.queue.length > 0) {
      const events = this.queue.splice(0, MAX_BATCH);
      try {
        await this.send(JSON.stringify({ session: this.session, events }));
      } catch {
        // The page's own reporting must never become an error the page reports.
      }
    }
  }
}

/** Posts a batch; `keepalive` lets the last one leave while the page unloads. */
async function post(body: string): Promise<void> {
  const response = await fetch('/api/telemetry/client', { method: 'POST', body, keepalive: true, headers: { 'content-type': 'application/json' } });
  if (!response.ok) throw new Error(`The server refused the page's events: ${response.status}.`);
}

export const telemetry = new ClientTelemetry(post);

const describe = (reason: unknown) => (reason instanceof Error ? reason.message : String(reason));
const stackOf = (reason: unknown) => (reason instanceof Error && reason.stack !== undefined ? reason.stack : undefined);

/** Reports what the page did not catch, and sends what is held when the page is hidden or left. */
export function startTelemetry(target: ClientTelemetry = telemetry): void {
  window.addEventListener('error', (event) => {
    const stack = stackOf(event.error);
    target.report({ level: 'error', kind: 'uncaught', message: event.message || describe(event.error), ...(stack === undefined ? {} : { stack }) });
  });
  window.addEventListener('unhandledrejection', (event) => {
    const stack = stackOf(event.reason);
    target.report({ level: 'error', kind: 'rejection', message: describe(event.reason), ...(stack === undefined ? {} : { stack }) });
  });
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') void target.flush(); });
  window.addEventListener('pagehide', () => void target.flush());
}

/** A request's init with a fresh trace context, and that context, so a failure can be reported in its trace. */
export function traced(init?: RequestInit): { readonly init: RequestInit; readonly traceparent: string } {
  const traceparent = newTraceparent();
  return { init: { ...init, headers: { ...init?.headers, traceparent } }, traceparent };
}
