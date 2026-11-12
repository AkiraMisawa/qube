import { EventType, type BaseEvent, type Context, type RunAgentInput } from '@ag-ui/core';
import { ApiError, call, send } from '../platform/api';

// The AI pane's side of AG-UI (ADR-0007 §1): a run is posted as RunAgentInput and its events are read as server-sent
// events. A run belongs to the server: a stream that breaks is read again from the last event it delivered, never
// posted again, so a request is never answered twice.

export interface AgentRuntimeInfo {
  readonly name: string;
  readonly label: string;
  readonly simulated: boolean;
  /** The models a conversation may run on (ADR-0017 §6.3); none for a runtime with no model. */
  readonly models: readonly string[];
}

export interface AgentTransport {
  runtime(): Promise<AgentRuntimeInfo>;
  /** Posts one request and delivers every event of its run, in order, until the run finishes or fails. */
  run(input: RunAgentInput, onEvent: (event: BaseEvent) => void): Promise<void>;
  cancel(threadId: string, runId: string): Promise<void>;
}

const TERMINAL = new Set<string>([EventType.RUN_FINISHED, EventType.RUN_ERROR]);

/** How many times a broken stream is read again before the pane says the run's end was not seen. */
const RESUMES = 3;

export const httpAgentTransport: AgentTransport = {
  runtime: () => call('/api/agent/runtime'),

  async run(input, onEvent) {
    let seen = 0;
    let ended = false;
    const deliver = (event: BaseEvent) => {
      seen += 1;
      if (TERMINAL.has(event.type)) ended = true;
      onEvent(event);
    };
    await readStream(await open('/api/agent/runs', { method: 'POST', body: JSON.stringify(input) }), deliver);
    for (let attempt = 0; !ended && attempt < RESUMES; attempt++) {
      const path = `/api/agent/threads/${encodeURIComponent(input.threadId)}/runs/${encodeURIComponent(input.runId)}/events?after=${seen}`;
      try {
        await readStream(await open(path, { method: 'GET' }), deliver);
      } catch (error) {
        if (error instanceof ApiError || attempt === RESUMES - 1) throw error;
      }
    }
    if (!ended) throw new Error('The connection to the agent was lost before its run ended; it may still be running on the server.');
  },

  async cancel(threadId, runId) {
    await send(`/api/agent/threads/${encodeURIComponent(threadId)}/runs/${encodeURIComponent(runId)}/cancel`, { method: 'POST' });
  },
};

async function open(path: string, init: RequestInit): Promise<Response> {
  // In a trace of its own, so the agent's run on the server, and the agent host's turn, are found from the request; a
  // stream the server failed or never answered is reported as any other call is (ADR-0014 §4).
  const response = await send(path, { ...init, headers: { accept: 'text/event-stream' } });
  if (response.body === null) throw new Error('The agent answered without a stream.');
  return response;
}

/** Reads `data:` frames until the stream closes. A frame that is not an AG-UI event fails the read. */
async function readStream(response: Response, deliver: (event: BaseEvent) => void): Promise<void> {
  const reader = response.body!.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += value;
    let end: number;
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const frame = buffer.slice(0, end);
      buffer = buffer.slice(end + 2);
      const event = parseFrame(frame);
      if (event !== undefined) deliver(event);
    }
  }
}

export function parseFrame(frame: string): BaseEvent | undefined {
  const data = frame.split('\n').filter((l) => l.startsWith('data:')).map((l) => l.slice(5).replace(/^ /, '')).join('\n');
  if (data === '') return undefined;
  const event = JSON.parse(data) as BaseEvent;
  if (typeof event.type !== 'string') throw new Error(`The agent sent an event without a type: ${data}`);
  return event;
}

/** The AG-UI context entry an app gives for the panel the user is looking at (ADR-0007 §8). */
export type AgentContext = Context;
