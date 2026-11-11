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
  /** The application whose workspaces the command acts on (ADR-0016 note 27 of 2026-11-11); none for a command of every application's. */
  readonly app?: string;
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

/**
 * A verb of the screen as the catalog lists it (ADR-0016 §3, note 21 of 2026-11-11): its id, what it does, and how it
 * is typed. The server reads its line as it reads a command's, and answers what the page is to do; nothing invokes it.
 */
export interface ViewVerbDescriptor {
  readonly verbId: string;
  readonly title: string;
  readonly command: CommandForm;
}

/**
 * A short form as the catalog lists it (ADR-0016 §3, note 29 of 2026-11-11): words an application's users type for a
 * line of a kernel verb. The server reads it as the line it stands for; what runs is that line's command.
 */
export interface ShortFormDescriptor {
  readonly formId: string;
  readonly title: string;
  readonly command: CommandForm;
}

/** An act on a table's rows that a screen offers by its title (ADR-0016 §4, note 25 of 2026-11-11): the line that does it. */
export interface TableAction {
  readonly title: string;
  readonly line: string;
}

/** The rows a view's selection names (ADR-0016 note 37): their table, and their keys in the order the view names them. */
export interface ViewSelection {
  readonly table: string;
  readonly keys: readonly string[];
}

/** A table as a screen is told of it (note 25): its id, its title, and the actions a grid bound to it offers. */
export interface TableOnScreen {
  readonly tableId: string;
  readonly title: string;
  readonly actions: readonly TableAction[];
}

/** What could stand at the end of a line: the text, where in the line it is written from, and what it is where that helps. */
export interface LineCompletion {
  readonly value: string;
  readonly start: number;
  readonly detail?: string;
}

/**
 * What a user was looking at when the line opened (ADR-0016 §9, note 16 of 2026-11-11), sent with each reading of that
 * opening: the table of the grid the keyboard was last in and its subset; the selection as row keys where the page
 * holds every row, or as ranges of positions with the sort and the order token the grid's reader answered where the
 * server pages it; and the focused cell, its row named the same way. A page that holds every row and filters them
 * names the rows it shows (`shown`), which the word `shown` then means. A grid filtered from the line carries its
 * conditions as typed (`where`, note 41): the server reads them again at each reading, so `shown` is the rows they
 * leave and a position is one among those rows. The server keeps none of it.
 */
export interface LineView {
  readonly table: string;
  readonly subset?: string;
  readonly sorts?: readonly { readonly column: string; readonly descending: boolean }[];
  readonly order?: string;
  readonly keys?: readonly string[];
  readonly ranges?: readonly { readonly start: number; readonly count: number }[];
  readonly focus?: { readonly field: string; readonly key?: string; readonly position?: number };
  readonly shown?: readonly string[];
  readonly where?: string;
}

/** An argument as the server read it: the words read for it, and whether they were typed or come from the view (note 18). */
export interface LineArgument {
  readonly name: string;
  readonly text: string;
  readonly from: 'typed' | 'context';
}

/**
 * Where a view verb asks the page to put the keyboard (ADR-0016 §9, note 21 of 2026-11-11): a cell of the grid behind
 * the line. Its row by key, and by its position in the view's order where the server pages the grid; its column as the
 * view names its columns, none where the grid's Focus had none to keep; and the sentence to say once the keyboard is there.
 */
export interface LinePlace {
  readonly kind: 'place';
  readonly table: string;
  readonly subset?: string;
  readonly key: string;
  readonly position?: number;
  readonly column?: string;
  readonly said: string;
}

/**
 * The sort a view verb asks the page to give the grid behind the line (ADR-0016 note 40 of 2026-11-11): its columns as
 * the view names them, the major key first, and none to take the sort off; and the sentence to say once it is done.
 */
export interface LineSort {
  readonly kind: 'sort';
  readonly table: string;
  readonly subset?: string;
  readonly sorts: readonly { readonly column: string; readonly descending: boolean }[];
  readonly said: string;
}

/**
 * The filter a view verb asks the page to give the grid behind the line (ADR-0016 note 41 of 2026-11-11): the
 * conditions as typed, which the grid's view then carries, and none to take the filter off; and the sentence to say.
 */
export interface LineFilter {
  readonly kind: 'filter';
  readonly table: string;
  readonly subset?: string;
  readonly where?: string;
  readonly said: string;
}

/**
 * The rows a view verb asks the page to place the Selection of the grid behind the line on (ADR-0016 note 43 of
 * 2026-11-11): one run of `count` rows. Where the server pages the grid it says the run by its first `position` in the
 * view's order; where the page holds every row it names the rows by their `keys`, and the page says whether its grid
 * shows them next to one another. `column` is the one the Focus was on, none where it had none to keep.
 */
export interface LineSelect {
  readonly kind: 'select';
  readonly table: string;
  readonly subset?: string;
  readonly keys?: readonly string[];
  readonly position?: number;
  readonly count: number;
  readonly column?: string;
  readonly said: string;
}

/** Cells a view verb asks the page's door to put on the clipboard (ADR-0016 note 44 of 2026-11-11): the door asks for them at Enter. */
export interface LineCopy {
  readonly kind: 'copy';
  readonly said: string;
}

/** Cells a view verb asks the page's door to save as a file of this name (ADR-0016 note 44 of 2026-11-11): the door asks for them at Enter. */
export interface LineExport {
  readonly kind: 'export';
  readonly fileName: string;
  readonly said: string;
}

/**
 * The one thing a view verb's reading asks of the page, said by its kind (ADR-0016 note 42 of 2026-11-11). A place, a
 * sort, a filter and a selection are done with the grid the view was read of; a copy and an export by the door.
 */
export type ViewAsk = LinePlace | LineSort | LineFilter | LineSelect | LineCopy | LineExport;

/**
 * What `copy` or `export` takes out of the workbench (ADR-0016 note 44; `POST /api/commands/content`): the text of the
 * line read again at the revision asked, or why it cannot be read.
 */
export interface LineContent {
  readonly revision: number;
  readonly text?: string;
  readonly refused?: NonNullable<LinePreview['refused']>;
}

/**
 * The rows a view's grid shows (ADR-0016 note 41; `POST /api/workspaces/{id}/shown`): what a page that holds every row
 * reads, at each revision it shows, to show the rows its grid's conditions leave. `of` is how many the grid has with
 * no conditions.
 */
export interface ViewShown {
  readonly table: string;
  readonly revision: number;
  readonly keys: readonly string[];
  readonly of: number;
}

/**
 * A line as the server read it at a revision (ADR-0016 §5; `POST /api/commands/preview`), with nothing executed: the
 * command and either its reading or why it cannot be read, and what could follow the last word. `inputs` invoke the
 * command on the revision read. `at` is the moment the reading speaks of, where it speaks of one: the sentence holds
 * no time, and the line writes this in its user's zone. `view` is the view the reading was asked with, in one sentence
 * (note 19), and `viewMoved` says that its positions are of an order that has moved since, so that its rows can no
 * longer be said, as it is where the view's conditions can no longer be read; a reading asked with no view has
 * neither. A view verb's reading names no command: it answers the one thing it `asks` of the page (note 42), such as
 * the place to put the keyboard on (note 21) or the sort to give the grid behind the line (note 40), and Enter invokes
 * nothing.
 */
export interface LinePreview {
  readonly revision: number;
  readonly command?: { readonly id: string; readonly version: string };
  readonly inputs?: Readonly<Record<string, unknown>>;
  readonly arguments?: readonly LineArgument[];
  readonly rows?: { readonly table: string; readonly title: string; readonly count: number; readonly first: readonly string[] };
  readonly would?: string;
  readonly at?: string;
  readonly refused?: { readonly code: string; readonly sentence: string; readonly word?: string; readonly start?: number; readonly end?: number; readonly candidates: readonly string[] };
  readonly complete: readonly LineCompletion[];
  readonly more: number;
  readonly view?: string;
  readonly viewMoved?: boolean;
  readonly asks?: ViewAsk;
  /** The line a short form stands for (ADR-0016 note 29 of 2026-11-11): the reading, the command and the inputs are that line's. */
  readonly expands?: string;
}

/** A field of one value: text, a number or a flag. */
export interface ValueSchema {
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

/**
 * A field that is a list (ADR-0016 note 37 of 2026-11-11): of values, or of records whose fields are values or lists
 * of values. A command's typed value may be one; a form draws none.
 */
export interface ListSchema {
  readonly type: 'array';
  readonly title?: string;
  readonly description?: string;
  readonly minItems?: number;
  readonly items: ValueSchema | {
    readonly type: 'object';
    readonly properties: Readonly<Record<string, ValueSchema | (ListSchema & { readonly items: ValueSchema })>>;
    readonly required?: readonly string[];
    readonly additionalProperties: false;
  };
}

export type FieldSchema = ValueSchema | ListSchema;

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
  /** The verbs of the screen a line may say (ADR-0016 note 21 of 2026-11-11), listed apart from the capabilities, which they are not. */
  readonly viewVerbs: readonly ViewVerbDescriptor[];
  /** The short forms a line may say (ADR-0016 note 29 of 2026-11-11), listed apart too: what runs is the kernel command each stands for. */
  readonly shortForms: readonly ShortFormDescriptor[];
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
