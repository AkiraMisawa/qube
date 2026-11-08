// The XVA workspace's wire shapes, as the server's XvaWorkspaces answers them (server/src/Workbench.Samples.Xva).
// The figures and identifiers are synthetic; the shapes are the contract.

export type Ownership = 'source' | 'automatic' | 'user';

export interface EffectiveTrade {
  readonly tradeId: string;
  readonly isNew: boolean;
  readonly product: string;
  readonly currency: string;
  readonly notional: number;
  readonly maturity: string;
  /** Absent where the product has none (an FX forward): the server leaves nulls out. */
  readonly fixedRate?: number | null;
  readonly schedule?: string | null;
  readonly lastFixing?: number | null;
  /** The books' PV of an existing trade, the source's (ADR-0010 §6); absent for a new trade. */
  readonly officialPv?: number | null;
  readonly csa: string;
  readonly csaOwner: Ownership;
  readonly curve: string;
  readonly curveOwner: Ownership;
  readonly before: boolean;
  readonly after: boolean;
  /** Why this row cannot be priced as it stands; Price is refused while any priced row has one. */
  readonly issues: readonly string[];
  /** Edits held back on this row, as `field: conflict` or `field: lost`. */
  readonly held: readonly string[];
  /** The fields the user set on this row. */
  readonly overridden: readonly string[];
  /** Each declared measure's membership, by measure id (ADR-0010 §10); FVA's is the first (§5). */
  readonly membership?: Readonly<Record<string, Membership>>;
}

/** Whether a measure prices a trade, and why. Out is never silent: it says why. */
export interface Membership { readonly in: boolean; readonly reason: string }

export interface Registration { readonly key: string; readonly code: string; readonly legalEntity: string; readonly name: string }

export interface Agreement {
  readonly maId: string;
  readonly nettingSetId: string;
  readonly registrationKey: string;
  readonly csas: readonly { readonly csaId: string }[];
  /** Absent for an agreement with no CSA: the server leaves nulls out. */
  readonly mainCsaId?: string | null;
  readonly collateralised: boolean;
  readonly noCsa: string;
  /** The source's, read-only (docs/03 §2.2). */
  readonly enforceable: boolean;
}

/** A CSA's terms as the workspace has them (ADR-0010 §8): the source's, or the user's where `overridden` names the field. */
export interface EffectiveCsa {
  readonly csaId: string;
  readonly type: string;
  readonly currency: string;
  readonly curve: string;
  readonly curveOwner: Ownership;
  readonly fvaCurve: string;
  readonly frequency: string;
  readonly exchange: boolean;
  readonly oneWay: boolean;
  readonly counterpartyThreshold: number;
  readonly counterpartyMta: number;
  readonly counterpartyCurrency: string;
  readonly selfThreshold: number;
  readonly selfMta: number;
  readonly selfCurrency: string;
  readonly overridden: readonly string[];
  /** Each measure rule's standing on this CSA, such as FVA's Imperfect (ADR-0010 §5). */
  readonly membership?: Readonly<Record<string, Membership>>;
}

/** The netting set's CVA recovery: absent when it is empty (the server leaves nulls out), and then the CDS recovery applies. */
export interface EffectiveCredit { readonly cvaRecovery?: number | null; readonly cvaRecoveryOwner: Ownership; readonly cdsRecovery: number }

export interface HeldEdit {
  readonly scope: string;
  readonly entity: string;
  readonly field: string;
  readonly value: unknown;
  readonly source: Readonly<Record<string, unknown>> | null;
  readonly acknowledged: boolean;
}

/** One figure a measure reports, such as CVA's CVA Charge. */
export interface MetricDefinition { readonly id: string; readonly title: string }

/** A measure as its module registered it (ADR-0010 §10). The page knows no measure's rules; it reads these. */
export interface RegisteredMeasure {
  readonly id: string;
  readonly title: string;
  readonly module: string;
  readonly metrics: readonly MetricDefinition[];
  /** The metric the collapsed result line shows. */
  readonly headline: string;
  readonly declaresMembership: boolean;
  readonly traced: boolean;
}

export interface XvaWorkspaceView {
  readonly workspaceId: string;
  readonly revision: number;
  readonly context: { readonly registration: string; readonly valueDate: string; readonly portfolioSize: number };
  readonly sourceVersion: string;
  readonly registration: Registration;
  readonly agreement: Agreement;
  readonly csas: readonly EffectiveCsa[];
  readonly credit: EffectiveCredit;
  readonly trades: number;
  readonly newTrades: number;
  /** How many trades are in Before and in After: a side with none is not priced (ADR-0010 §3). */
  readonly inBefore: number;
  readonly inAfter: number;
  readonly overrides: number;
  readonly conflicts: readonly HeldEdit[];
  readonly lost: readonly HeldEdit[];
  readonly runs: readonly { readonly runId: string; readonly revision: number }[];
  /** Every registered measure, in the order the page shows them. */
  readonly measures: readonly RegisteredMeasure[];
  /** What Price prices; part of the workspace, so Price freezes it. */
  readonly selectedMeasures: readonly string[];
}

export interface RowPage {
  readonly rows: readonly EffectiveTrade[];
  readonly start: number;
  readonly totalCount: number;
  readonly orderToken: string;
  readonly revision: number;
}

export interface CarrySummary { readonly carried: number; readonly conflicted: number; readonly lost: number }

export interface ScopeDrop { readonly scope: string; readonly count: number; readonly byKind: Readonly<Record<string, number>> }

export type Resolution = 'reset' | 'keep' | 'takeSource' | 'acknowledge';
