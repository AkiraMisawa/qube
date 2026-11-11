// The wire vocabulary of contracts/composition.schema.json as the client reads it. The server's
// records are the authority; these types describe what it sends and accepts, nothing more.

export interface TypedDataRef {
  readonly dataId: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly revision: string;
}

export interface Port {
  readonly name: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly cardinality: 'one' | 'many';
}

/**
 * How a command is typed (ADR-0016 §4): the words that name it, each a spelling of its own, and its arguments in the
 * order typed. The line builds its entry from this and reads nothing of the line itself; the server does (§5).
 */
export interface CommandForm {
  readonly words: readonly string[];
  readonly arguments: readonly { readonly name: string; readonly kind: 'rows' | 'assignments' | 'field' | 'text'; readonly required: boolean }[];
}

export interface CapabilityDescriptor {
  readonly capabilityId: string;
  readonly version: string;
  readonly title: string;
  readonly inputPorts: readonly Port[];
  readonly outputPorts: readonly Port[];
  readonly executionKind: 'requestResponse' | 'job' | 'subscription';
  readonly effects: readonly string[];
  /** Absent for a capability no line names. */
  readonly command?: CommandForm;
}

/** What could stand at the end of a line: the text, where in the line it is written from, and what it is where that helps. */
export interface LineCompletion {
  readonly value: string;
  readonly start: number;
  readonly detail?: string;
}

/**
 * A line as the server read it at a revision (ADR-0016 §5; `POST /api/commands/preview`), with nothing executed: the
 * command and either its reading or why it cannot be read, and what could follow the last word. `inputs` invoke the
 * command on the revision read. `at` is the moment the reading speaks of, where it speaks of one: the sentence holds
 * no time, and the line writes this in its user's zone.
 */
export interface LinePreview {
  readonly revision: number;
  readonly command?: { readonly id: string; readonly version: string };
  readonly inputs?: Readonly<Record<string, unknown>>;
  readonly arguments?: readonly { readonly name: string; readonly text: string }[];
  readonly rows?: { readonly table: string; readonly title: string; readonly count: number; readonly first: readonly string[] };
  readonly would?: string;
  readonly at?: string;
  readonly refused?: { readonly code: string; readonly sentence: string; readonly word?: string; readonly start?: number; readonly end?: number; readonly candidates: readonly string[] };
  readonly complete: readonly LineCompletion[];
  readonly more: number;
}

export interface FieldSchema {
  readonly type: 'string' | 'integer' | 'number' | 'boolean';
  readonly title?: string;
  readonly description?: string;
  readonly enum?: readonly string[];
  readonly minimum?: number;
  readonly maximum?: number;
  readonly minLength?: number;
  readonly format?: 'date';
  readonly default?: unknown;
}

export interface ContractDefinition {
  readonly contractId: string;
  readonly contractVersion: string;
  readonly title: string;
  readonly schema: {
    readonly type: 'object';
    readonly title?: string;
    readonly properties: Readonly<Record<string, FieldSchema>>;
    readonly required?: readonly string[];
    readonly additionalProperties: false;
  };
}

export type Binding =
  | { readonly kind: 'input'; readonly path: string }
  | { readonly kind: 'context'; readonly path: string }
  | { readonly kind: 'constant'; readonly value: unknown }
  | { readonly kind: 'output'; readonly stepId: string; readonly port: string; readonly path?: string }
  | { readonly kind: 'item'; readonly path: string }
  | { readonly kind: 'dataReference'; readonly reference: TypedDataRef };

export interface WorkflowStep {
  readonly stepId: string;
  readonly capabilityId: string;
  readonly capabilityVersion: string;
  readonly dependsOn: readonly string[];
  readonly bindings: Readonly<Record<string, Binding>>;
  readonly failurePolicy: 'stop' | 'collect';
  readonly forEach?: Binding;
  readonly maxConcurrency?: number;
  readonly itemMode?: 'value' | 'outcomeValue';
  readonly itemKeyPath?: string;
}

export interface WorkflowDefinition {
  readonly documentType: 'workflow';
  readonly schemaVersion: string;
  readonly definitionId: string;
  readonly version: string;
  readonly title: string;
  readonly inputContractId: string;
  readonly inputContractVersion: string;
  readonly steps: readonly WorkflowStep[];
  readonly outputs: Readonly<Record<string, Binding>>;
}

export type ViewData =
  | { readonly kind: 'workflowOutput'; readonly name: string }
  | { readonly kind: 'dataReference'; readonly reference: TypedDataRef };

export type ColumnFormat = 'text' | 'number' | 'currency' | 'percent' | 'date' | 'status';

export interface ViewColumn {
  readonly field: string;
  readonly label: string;
  readonly format: ColumnFormat;
  readonly currencyField?: string;
}

export interface ViewAction {
  readonly actionId: string;
  readonly label: string;
  readonly bindings: Readonly<Record<string, { readonly kind: 'row'; readonly field: string }>>;
}

export type ComponentKind = 'form' | 'choice' | 'grid' | 'chart' | 'metric' | 'progress' | 'details';

export interface ViewComponent {
  readonly id: string;
  readonly kind: ComponentKind;
  readonly title: string;
  readonly data: ViewData;
  readonly columns?: readonly ViewColumn[];
  readonly actions?: readonly ViewAction[];
}

export interface ViewDefinition {
  readonly documentType: 'view';
  readonly schemaVersion: string;
  readonly definitionId: string;
  readonly version: string;
  readonly title: string;
  readonly layout: { readonly wideColumns: number; readonly narrowColumns: 1 };
  readonly components: readonly ViewComponent[];
}

export type ActionTarget =
  | { readonly kind: 'openView'; readonly viewId: string; readonly viewVersion: string; readonly dataParameter: string }
  | { readonly kind: 'runWorkflow'; readonly workflowId: string; readonly workflowVersion: string; readonly inputFromParameters: Readonly<Record<string, string>> };

export interface ActionDefinition {
  readonly actionId: string;
  readonly title: string;
  readonly parameters: readonly { readonly name: string; readonly kind: 'dataRef' | 'text' }[];
  readonly target: ActionTarget;
}

export interface Catalog {
  readonly capabilities: readonly CapabilityDescriptor[];
  readonly contracts: readonly ContractDefinition[];
  readonly workflows: readonly WorkflowDefinition[];
  readonly views: readonly ViewDefinition[];
  readonly actions: readonly ActionDefinition[];
}

export type DatasetColumnType = 'string' | 'number' | 'integer' | 'boolean' | 'date' | 'dataRef';

export interface Dataset {
  readonly documentType: 'dataset';
  readonly schemaVersion: string;
  readonly reference: TypedDataRef;
  readonly title: string;
  readonly columns: readonly { readonly field: string; readonly type: DatasetColumnType; readonly nullable: boolean }[];
  readonly rows: readonly Readonly<Record<string, unknown>>[];
  readonly provenance: {
    readonly executionMode: 'actual' | 'simulated';
    readonly dataOrigin: 'source' | 'synthetic' | 'mixed';
    readonly producerInvocationId: string;
  };
}

export type RunStatus = 'running' | 'succeeded' | 'partiallySucceeded' | 'failed';

export interface OutcomeError {
  readonly code: string;
  readonly message: string;
  readonly causedByItemKey?: string;
}

export interface InvocationRecord {
  readonly invocationKey: string;
  readonly stepId: string;
  readonly capabilityId: string;
  readonly capabilityVersion: string;
  readonly status: 'running' | 'succeeded' | 'failed' | 'blocked';
  readonly outputs: Readonly<Record<string, TypedDataRef>>;
  readonly error?: OutcomeError;
}

export interface RunRecord {
  readonly runId: string;
  readonly workflow: WorkflowDefinition;
  readonly context: Readonly<Record<string, unknown>>;
  readonly input: Readonly<Record<string, unknown>>;
  readonly status: RunStatus;
  readonly invocations: readonly InvocationRecord[];
  readonly outputs: Readonly<Record<string, TypedDataRef>>;
  readonly error?: OutcomeError;
  readonly acceptedAt: string;
  readonly completedAt?: string;
  readonly owner: string;
  readonly origin: RunOrigin;
  readonly relations: RunRelations;
  readonly resultContracts: readonly string[];
}

/** What started a Run (docs/05 §7). */
export type RunOrigin = 'pricer' | 'ai' | 'workflow' | 'action';

/**
 * What `workspace.undo` and `workspace.redo` answer (ADR-0016 §11; the contract `workspace.taken-back`): the command the
 * answer is, the one it took back or put back, and the sentence that names it as its screen does.
 */
export interface TakenBack {
  readonly workspaceId: string;
  readonly revision: number;
  /** This command's own id, by which it can be taken back in turn. */
  readonly command: string;
  readonly said: string;
  /** The command taken back or put back. */
  readonly took: string;
  readonly by: string;
  readonly at: string;
  readonly edits: number;
  readonly origin?: RunOrigin;
}

/**
 * What `entity.set`, `entity.reset` and `entity.remove` answer (ADR-0016 §6; the contract `entity.rows-edited`): what was
 * done in one sentence, the table, how many rows matched, changed and already held the value, and the first rows to look
 * at. `command` is the id to take it back by, absent where nothing changed.
 */
export interface RowsEdited {
  readonly workspaceId: string;
  readonly revision: number;
  readonly command?: string;
  readonly said: string;
  readonly table: string;
  readonly matched: number;
  readonly changed: number;
  readonly held: number;
  /** The first rows' keys, separated by commas. */
  readonly rows: string;
}

/** A Run's explicit links to related work; never inferred from names or timing. */
export interface RunRelations {
  readonly workspaceId?: string;
  readonly workspaceRevision?: number;
  readonly parentRunId?: string;
  readonly analysisGroupId?: string;
  readonly conversationId?: string;
}

export interface CompositionIssue {
  readonly code: string;
  readonly path: string;
  readonly message: string;
  /** The word at fault, where a command refused something written (ADR-0016 §6). */
  readonly word?: string;
  /** What the word could have been: the fields a table has, the rows the end of an id fits. */
  readonly candidates?: readonly string[];
}

export interface ErrorEnvelope {
  readonly code: string;
  readonly message: string;
  readonly issues: readonly CompositionIssue[];
}
