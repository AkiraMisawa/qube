import type { Catalog, Dataset, ErrorEnvelope, LinePreview, LineView, RunRecord, TypedDataRef, ViewDefinition, WorkflowDefinition } from './contracts';
import { telemetry, traced } from './telemetry';

/** A refusal from the server, carrying its code and the located issues. Never turned into an empty result. */
export class ApiError extends Error {
  constructor(readonly status: number, readonly envelope: ErrorEnvelope) {
    super(envelope.message);
  }
}

/**
 * Sends one call to the server in a trace of its own (ADR-0014) and returns its successful answer. A failure of the
 * server's (5xx) or of the network is reported to the server's log in that trace; a refusal (4xx) is the server
 * answering, and its own log already says so. Any answer but a success is thrown as an {@link ApiError}.
 */
export async function send(path: string, init?: RequestInit): Promise<Response> {
  const { init: sent, traceparent } = traced({ ...init, headers: { 'content-type': 'application/json', ...init?.headers } });
  const method = init?.method ?? 'GET';
  let response: Response;
  try {
    response = await fetch(path, sent);
  } catch (error) {
    telemetry.report({ level: 'error', kind: 'api', message: `${method} ${path} was not answered: ${error instanceof Error ? error.message : String(error)}`, traceparent });
    throw error;
  }
  if (response.ok) return response;
  const text = await response.text();
  let envelope: ErrorEnvelope;
  try {
    envelope = JSON.parse(text) as ErrorEnvelope;
  } catch {
    envelope = { code: `Http${response.status}`, message: text || response.statusText, issues: [] };
  }
  if (response.status >= 500) telemetry.report({ level: 'error', kind: 'api', message: `${method} ${path} failed: ${response.status} ${envelope.code}: ${envelope.message}`, traceparent });
  throw new ApiError(response.status, envelope);
}

/** Calls the server as {@link send} does and reads its answer as JSON. */
export async function call<T>(path: string, init?: RequestInit): Promise<T> {
  return (await (await send(path, init)).json()) as T;
}

export type ActionOutcome =
  | { readonly kind: 'openView'; readonly view: ViewDefinition }
  | { readonly kind: 'run'; readonly run: RunRecord };

/** What a command answered: its outputs by port, as its contracts say (ADR-0005). */
export interface CommandAnswer {
  readonly capabilityId: string;
  readonly version: string;
  readonly outputs: Readonly<Record<string, unknown>>;
}

/** A command's output on one of its ports. A command that answers without it has broken its descriptor, and that is said. */
export function outputOf<T>(answer: CommandAnswer, port: string): T {
  const value = answer.outputs[port];
  if (value === undefined || value === null) throw new Error(`${answer.capabilityId} answered without its ${port}.`);
  return value as T;
}

export interface Api {
  catalog(): Promise<Catalog>;
  /**
   * Invokes a command capability through the page's door to the command gateway (ADR-0016 §5): the server names the
   * caller and that it came from a page. A repeated key answers the first result instead of acting twice.
   */
  command(capabilityId: string, version: string, inputs: Readonly<Record<string, unknown>>, idempotencyKey: string): Promise<CommandAnswer>;
  /**
   * Has the server read a line as typed for a workspace (ADR-0016 §5), as it stands or at the revision given, which a
   * workspace that moved refuses. Nothing is executed. A line that cannot be read is an answer, not an error.
   */
  /**
   * Reads a line and executes nothing (ADR-0016 §5): at `revision`, or as the workspace stands where none is given, and
   * with the view the line was opened on, where its page gave one (note 16).
   */
  preview(line: string, workspaceId: string, read?: { readonly revision?: number; readonly view?: LineView }, signal?: AbortSignal): Promise<LinePreview>;
  submitRun(workflow: WorkflowDefinition, context: Record<string, unknown>, input: Record<string, unknown>, idempotencyKey: string): Promise<RunRecord>;
  run(runId: string): Promise<RunRecord>;
  dataset(reference: TypedDataRef): Promise<Dataset>;
  invokeAction(actionId: string, parameters: Record<string, unknown>, context: Record<string, unknown>, idempotencyKey: string): Promise<ActionOutcome>;
}

export const httpApi: Api = {
  catalog: () => call('/api/catalog'),
  command: (capabilityId, version, inputs, idempotencyKey) =>
    call(`/api/commands/${encodeURIComponent(capabilityId)}/${encodeURIComponent(version)}`, { method: 'POST', body: JSON.stringify({ inputs, idempotencyKey }) }),
  preview: (line, workspaceId, read, signal) =>
    call('/api/commands/preview', { method: 'POST', body: JSON.stringify({ line, workspaceId, ...read }), ...(signal === undefined ? {} : { signal }) }),
  submitRun: (workflow, context, input, idempotencyKey) =>
    call('/api/runs', { method: 'POST', body: JSON.stringify({ workflow, context, input, idempotencyKey }) }),
  run: (runId) => call(`/api/runs/${encodeURIComponent(runId)}`),
  dataset: (reference) => call('/api/data/read', { method: 'POST', body: JSON.stringify(reference) }),
  invokeAction: (actionId, parameters, context, idempotencyKey) =>
    call(`/api/actions/${encodeURIComponent(actionId)}/invoke`, { method: 'POST', body: JSON.stringify({ parameters, context, idempotencyKey }) }),
};
