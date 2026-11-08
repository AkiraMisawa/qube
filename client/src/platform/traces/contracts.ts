// The calculation trace as the server reads it, a term at a time (ADR-0011 §5): the shapes of
// GET /api/runs/{run}/cases/{case}/traces/{measure}, its leaves and the catalogue of kinds. Nothing here knows a
// regulation: a kind's label, symbol and TeX are the registering module's data.

export type TermState = 'value' | 'absent' | 'unavailable' | 'notReturned';
export type Side = 'before' | 'after';

export interface TraceUnit { readonly kind: string; readonly currency?: string | null }
export interface TraceMethod { readonly id: string; readonly version: string; readonly regulation?: string | null }
export interface TraceProducer { readonly name: string; readonly version: string }
export interface TraceSubject { readonly kind: string; readonly id: string; readonly label?: string | null }

export interface TraceOrigin {
  readonly side: Side;
  readonly engine?: TraceProducer | null;
  readonly provenance?: { readonly executionMode: 'actual' | 'simulated'; readonly dataOrigin: string; readonly producerInvocationId: string } | null;
  readonly requestId?: string | null;
  readonly valuationDate?: string | null;
  readonly reportingCurrency?: string | null;
  readonly subject?: TraceSubject | null;
}

export interface TermCrumb { readonly id: string; readonly label: string; readonly symbol: string }

/** What a trace is of: its Run, case, scenario and measure, the method and depth, the sides priced, and the roots. */
export interface TraceHeader {
  readonly runId: string;
  readonly case: string;
  readonly scenario: string;
  readonly measure: string;
  readonly method: TraceMethod;
  readonly depth: string;
  /** `before`, `after`, in that order; one side when the case priced one (ADR-0011 §4). */
  readonly sides: readonly Side[];
  readonly origins: readonly TraceOrigin[];
  readonly roots: readonly TermCrumb[];
}

export type CheckStatus = 'matches' | 'inconsistent' | 'notChecked' | 'notApplicable';
export interface TermCheck { readonly status: CheckStatus; readonly computed?: number | null; readonly reason?: string | null }

/** A value a decision read: what it was, where it came from, and where to find it. */
export interface DecisionInput { readonly value?: unknown; readonly source?: string; readonly where?: string }

/** A term on one side: its value or why it has none, and its reconciliation. */
export interface SideValue {
  readonly state: TermState;
  readonly value?: number | string | null;
  readonly reason?: string | null;
  readonly check: TermCheck;
  /** The operand of a max, min or clamp that the value took, by slot name or position. */
  readonly governed?: string | number | null;
  readonly allocation?: { readonly method: string; readonly value: number } | null;
  readonly inputs?: Readonly<Record<string, DecisionInput | null>> | null;
  readonly because?: readonly string[] | null;
  /** False where the provider did not write the term and the reader stands it in as absent or not returned. */
  readonly written: boolean;
  readonly citation?: string | null;
  readonly leaves?: { readonly leafSet: string; readonly rowCount: number } | null;
}

export type DiffState = 'value' | 'absent' | 'unavailable' | 'notNumeric';
export interface TermDiff { readonly state: DiffState; readonly value?: number | null; readonly reason?: string | null; readonly changed?: boolean | null }

/** One term on every side its case priced; a side the case did not price is not there at all. */
export interface TermRead {
  readonly id: string;
  readonly kind: string;
  readonly label: string;
  readonly symbol: string;
  readonly citation?: string | null;
  readonly unit?: TraceUnit | null;
  readonly key?: Readonly<Record<string, string>> | null;
  readonly childCount: number;
  readonly before?: SideValue | null;
  readonly after?: SideValue | null;
  readonly diff?: TermDiff | null;
  /** The child playing each role of its formula where the provider named it; otherwise `{id}/{role}`. */
  readonly slots?: Readonly<Record<string, string>> | null;
}

export interface TraceMatrix { readonly rows: readonly string[]; readonly columns: readonly string[]; readonly values: readonly (readonly number[])[] }

export interface TraceParameter {
  readonly name: string;
  readonly source: 'regulation' | 'configuration' | 'input' | 'data' | 'derived';
  readonly scope?: string | null;
  readonly value?: number | null;
  readonly text?: string | null;
  readonly matrix?: TraceMatrix | null;
  readonly unit?: TraceUnit | null;
  readonly citation?: string | null;
  readonly config?: { readonly key: string; readonly version: string } | null;
  readonly input?: { readonly entity: string; readonly field: string } | null;
  readonly data?: { readonly source: string; readonly version: string } | null;
  readonly because?: readonly string[] | null;
}

export interface TermPage {
  readonly term: TermRead;
  readonly path: readonly TermCrumb[];
  readonly children: readonly TermRead[];
  readonly childTotal: number;
  readonly offset: number;
  /** The parameters in the term's scope on each side; none for a side the case did not price. */
  readonly beforeParameters?: readonly TraceParameter[] | null;
  readonly afterParameters?: readonly TraceParameter[] | null;
}

export interface TraceAnswer { readonly trace: TraceHeader; readonly term: TermPage }

export interface LeafMismatch { readonly column: string; readonly returned: number; readonly computed: number }

/** A leaf row on both sides, paired by its entity and component. */
export interface PairedLeafRow {
  readonly leafSet: string;
  readonly entity: string;
  readonly component?: string | null;
  readonly before?: Readonly<Record<string, unknown>> | null;
  readonly after?: Readonly<Record<string, unknown>> | null;
  readonly beforeMismatches: readonly LeafMismatch[];
  readonly afterMismatches: readonly LeafMismatch[];
}

export interface TraceLeavesAnswer {
  readonly trace: TraceHeader;
  readonly term: string;
  readonly leaves: { readonly rows: readonly PairedLeafRow[]; readonly total: number; readonly offset: number };
}

/** An expression from ADR-0011 §2's closed set, as data; the view reads only which operands it names. */
export interface TraceExpr { readonly op: string; readonly [field: string]: unknown }

export interface TermKind {
  readonly kind: string;
  readonly label: string;
  readonly symbol: string;
  readonly unit: TraceUnit;
  readonly citation: string;
  /** The formula, with `\slot{role}` for each operand that is a term and `\param{name}` for each parameter. */
  readonly tex: string;
  readonly valueType: 'number' | 'text';
  readonly expression?: TraceExpr | null;
}

export interface LeafColumn { readonly name: string; readonly label: string; readonly unit?: TraceUnit | null; readonly expression?: TraceExpr | null; readonly tex?: string | null }
export interface LeafSetKind { readonly leafSet: string; readonly entityField: string; readonly columns: readonly LeafColumn[]; readonly componentField?: string | null }

export interface TraceCatalog { readonly method: TraceMethod; readonly kinds: readonly TermKind[]; readonly leafSets: readonly LeafSetKind[] }

/** Which trace a view reads: a Run's case, scenario and measure. */
export interface TraceAddress { readonly runId: string; readonly case: string; readonly scenario: string; readonly measure: string }
