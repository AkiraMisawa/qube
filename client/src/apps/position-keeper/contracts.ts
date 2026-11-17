// The Position Keeper's wire shapes, as the server's `pk.workspace.create`, `pk.position.read` and `tables.read` answer them
// (server/src/Qube.Samples.PositionKeeper, ADR-0019). The wire leaves out what there is none of: an absent field is none, never
// a value of its own.

/** What a figure is, as the server marks it (ADR-0019 §3, §7): the page draws these and infers none. */
export type Mark = 'WHAT-IF' | 'SIMULATED' | 'FIRST ORDER';

/** A Position Keeper workspace as it is made: which book it is of. */
export interface PkCreated {
  readonly workspaceId: string;
  readonly revision: number;
  readonly resultSet: string;
  readonly engine: string;
  readonly engineCommit: string;
  readonly inputSet: string;
  readonly valuationDate: string;
}

export interface PositionValue {
  readonly counterparty: string;
  /** Absent where the result set maps the counterparty to none. */
  readonly legalEntity?: string;
  readonly metric: string;
  readonly value: number;
  readonly stdError: number;
}

export interface PositionHedge {
  readonly hedge: string;
  readonly value: number;
  /** Who computed the hedge's risk, and whether that is a model (`Actual`) or not (`Simulated`). */
  readonly provider: string;
  readonly executionMode: string;
  readonly marks: readonly Mark[];
}

/** Book, What-if and Net of one line: What-if is absent where no hedge has a figure there. */
export interface Split {
  readonly book: number;
  readonly whatIf?: number;
  readonly net: number;
  readonly marks: readonly Mark[];
}

export interface PositionRisk extends Split {
  readonly kind: string;
  readonly input: string;
  readonly key?: string;
  /** `rate`: per 1.0 of a rate; `level`: per unit of the quote. Absent where the result set declares no unit. */
  readonly per?: 'rate' | 'level';
}

export interface PositionPnl extends Split {
  readonly kind: string;
  readonly input: string;
  readonly move: number;
  /** What the move is in: `bp` or `quote` where it is absolute, `%` where it is of each key's level. */
  readonly typedIn: string;
}

export interface PkPosition extends Omit<PkCreated, 'revision'> {
  readonly revision: number;
  readonly counterparties: readonly string[];
  readonly values: readonly PositionValue[];
  readonly totals: readonly { readonly metric: string; readonly value: number }[];
  readonly hedges: readonly PositionHedge[];
  readonly risk: readonly PositionRisk[];
  readonly pnl: readonly PositionPnl[];
  /** The predicted profit and loss of every move held; absent while no move is held. */
  readonly pnlTotalBook?: number;
  readonly pnlTotalWhatIf?: number;
  readonly pnlTotalNet?: number;
  readonly pnlTotalMarks?: readonly Mark[];
}

/** A table of the workspace as `tables.read` answers it: each cell as the server writes it for a reader. */
export interface TableRead {
  readonly revision: number;
  readonly fields: readonly string[];
  readonly rows: readonly { readonly key: string; readonly cells: readonly string[] }[];
  /** The rows the answer left out. */
  readonly more: number;
}

/** A Position Keeper workspace as its page shows it, read at one revision. */
export interface PkView {
  readonly position: PkPosition;
  readonly hedges: TableRead;
  readonly moves: TableRead;
}
