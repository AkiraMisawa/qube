// The XVA workspace's wire shapes, as the server's XvaWorkspaces answers them (server/src/Qube.Samples.Xva).
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
  /** The trade's PV (ADR-0016 note 49): its PV legs' in the display currency; absent where it has no leg. */
  readonly pv?: number | null;
  /** What SA-CCR reads of the trade (ADR-0013 §3): its start, its direction (`payFixed`, `receiveFixed`, `buy` or
   * `sell`) and an FX forward's counter leg. Absent where the source has none. */
  readonly start?: string | null;
  readonly direction?: string | null;
  readonly counterCurrency?: string | null;
  readonly counterNotional?: number | null;
  readonly csa: string;
  readonly csaOwner: Ownership;
  readonly curve: string;
  readonly curveOwner: Ownership;
  /** The IM CSA collecting the trade's IM and the one posting it (ADR-0012 §2): assignments, as its CSA is. Absent
   * where it has none, which is no IM CSA in that direction. */
  readonly imR?: string | null;
  readonly imROwner: Ownership;
  readonly imP?: string | null;
  readonly imPOwner: Ownership;
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
  /** The fields whose value differs from Original, with Original's value (ADR-0010 §2). */
  readonly original?: Readonly<Record<string, unknown>> | null;
  /** The selected modules' cells on this trade, by field (ADR-0010 §10), such as XVA's `xva.pv01`. */
  readonly cells?: Readonly<Record<string, TradeCell>> | null;
}

/**
 * A module column's cell (ADR-0010 §6): its value, or none while it is pending or after it failed, never 0 for either;
 * a note saying where the value came from or why there is none; and whether it may be typed now.
 */
export interface TradeCell {
  /** A figure in a number column, a word in a text column (SA-CCR's class of a trade). */
  readonly value?: number | string | null;
  readonly state: 'value' | 'pending' | 'failed';
  readonly note?: string | null;
  readonly editable: boolean;
}

/** A column a module adds to the trade grids (ADR-0010 §10), by its field on a trade. `editable` says whether it is ever
 * typed; whether it may be now is its cell's. */
/** A module's trade column; `amount` is money, shown as every amount is (C48), `number` any other figure. */
export interface RegisteredColumn { readonly field: string; readonly title: string; readonly type: 'amount' | 'number' | 'text'; readonly editable: boolean; readonly module: string }

/**
 * Whether a measure prices a trade, and why. Out is never silent: it says why. A measure priced per scope, as spot IM
 * is per direction and regime (ADR-0012 §2), lists each scope with its own standing, and its reason is then the
 * compact form (`R: JFSA, CFTC · P: JFSA`).
 */
export interface Membership { readonly in: boolean; readonly reason: string; readonly scopes?: readonly MembershipScope[] | null }

/** One scope of a membership, such as `collect/JFSA`, titled as the page shows it (`IM-R JFSA`). */
export interface MembershipScope { readonly id: string; readonly title: string; readonly in: boolean; readonly reason: string }

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
  /** The counterparty's IM CSAs, at most one per direction, the source's and read-only (ADR-0012 §2, §3). */
  readonly imCsas: readonly ImCsa[];
}

/** An IM CSA (ADR-0012 §3): the direction it margins, the regimes it serves from their dates, and how SIMM computes it. */
export interface ImCsa {
  readonly imCsaId: string;
  /** `collect` (IM-R, collected from the counterparty) or `post` (IM-P, posted to it). */
  readonly direction: string;
  readonly regimes: readonly { readonly regime: string; readonly since: string }[];
  readonly calculationCurrency: string;
  /** Names the parameter table SIMM computes with. */
  readonly simmVersion: string;
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
  /** The terms whose value differs from the source's, with the source's value (ADR-0010 §2). */
  readonly original?: Readonly<Record<string, unknown>> | null;
}

/**
 * The netting set's CVA recovery and the counterparty's CDS (ADR-0010 §8's note of 2026-11-09). The CVA recovery is
 * absent when it is empty (the server leaves nulls out), and then the CDS recovery applies: the chosen curve's, or none
 * while no curve is chosen, when `missing` says why CVA is refused.
 */
export interface EffectiveCredit {
  readonly cvaRecovery?: number | null;
  readonly cvaRecoveryOwner: Ownership;
  readonly cdsRecovery?: number | null;
  readonly choice: CdsChoice;
  readonly cds?: EffectiveCds | null;
  readonly missing?: string | null;
  /** Present when the CVA recovery or the CDS chosen differs from the source's, with the source's value (ADR-0010 §2). */
  readonly original?: Readonly<Record<string, unknown>> | null;
}

/** A CDS type of the catalogue: one name's own curve, or a proxy's by sector, region and rating. */
export type CdsType = 'singleName' | 'proxy';

/** The counterparty's CDS as chosen: its type (none for no CDS), and the keys of each type, kept while the other is chosen. */
export interface CdsChoice {
  readonly type?: CdsType | null;
  readonly reference?: string | null;
  readonly sector?: string | null;
  readonly region?: string | null;
  readonly rating?: string | null;
}

/** A curve of the synthetic CDS catalogue: a single name's, or a proxy's. Spreads are fractions by tenor (0.0052 is 52bp). */
export interface CdsCurve {
  readonly id: string;
  readonly type: CdsType;
  readonly ticker: string;
  readonly currency: string;
  readonly seniority: string;
  readonly docClause: string;
  readonly name: string;
  readonly recovery: number;
  readonly spreads: Readonly<Record<string, number>>;
  readonly sector?: string | null;
  readonly region?: string | null;
  readonly rating?: string | null;
  /** As the search writes it: `ticker | ccy | seniority | doc clause | name`. */
  readonly reference: string;
}

/** The chosen curve with the user's spreads: those set are listed, and those that differ from the catalogue's carry it. */
export interface EffectiveCds {
  readonly curveId: string;
  readonly type: CdsType;
  readonly ticker: string;
  readonly currency: string;
  readonly seniority: string;
  readonly docClause: string;
  readonly name: string;
  readonly reference: string;
  readonly recovery: number;
  readonly spreads: Readonly<Record<string, number>>;
  readonly overridden: readonly string[];
  readonly sector?: string | null;
  readonly region?: string | null;
  readonly rating?: string | null;
  readonly original?: Readonly<Record<string, unknown>> | null;
}

/** The catalogue the Credit tab searches: the single names, the keys a proxy is chosen by, and the tenors of every curve. */
export interface CdsCatalogue {
  readonly singleNames: readonly CdsCurve[];
  readonly sectors: readonly string[];
  readonly regions: readonly string[];
  readonly ratings: readonly string[];
  readonly tenors: readonly string[];
}

/** How a setting is written: a whole number of days, a rate (12%), a number, or on and off. */
export type SettingForm = 'days' | 'rate' | 'number' | 'flag';

/**
 * A module's setting as the workspace has it (ADR-0010 §8): its value (absent where it is empty, the server leaving nulls
 * out) and as written, its default and as written, what empty means where it may be, the measures it serves, and whether
 * it differs from the default.
 */
export interface SettingView {
  readonly id: string;
  readonly title: string;
  readonly form: SettingForm;
  readonly value?: unknown;
  readonly text: string;
  readonly default?: unknown;
  readonly defaultText: string;
  readonly empty?: string | null;
  readonly measures: readonly string[];
  readonly changed: boolean;
}

/** A module's section of the Settings tab: those of its settings a selected measure needs. */
export interface ModuleSettings { readonly module: string; readonly title: string; readonly settings: readonly SettingView[] }

export interface HeldEdit {
  readonly scope: string;
  readonly entity: string;
  readonly field: string;
  readonly value: unknown;
  readonly source: Readonly<Record<string, unknown>> | null;
  readonly acknowledged: boolean;
  /** The case the edit is a change of; none for a Common edit (ADR-0010 §3). */
  readonly case: string | null;
}

/**
 * One case of `add each` (ADR-0016 note 37 of 2026-11-11): the new trades it adds together, and the existing trades it
 * takes out of After.
 */
export interface Iteration {
  readonly add: readonly string[];
  readonly without: readonly string[];
}

/**
 * A case Price prices (ADR-0010 §3): the workspace itself, the `base`, first; a case `vary` made, or an iteration of
 * `add each`, which names its trades. `changes` counts its own edits.
 */

export interface CaseView {
  readonly id: string;
  readonly title: string;
  readonly kind: 'base' | 'vary' | 'add';
  readonly changes: number;
  // A field the server has no value for is left out of its answer, as every empty field of the wire is.
  /** What a case of `add each` adds together and takes out of After (ADR-0016 note 37); none on any other case. */
  readonly iteration?: Iteration;
  /** The common change to the existing trades a case of `add each` carries, said with it (docs/03 §5). */
  readonly note?: string;
}

/**
 * One change as the Changes tab lists it (ADR-0010 §3's note of 2026-11-08): the case it is a change of (none for a
 * Common change), its title as a cell is named, its value and Original's as written, whether it is set, held as a
 * conflict or lost, and whether its value is typed there.
 */
export interface ChangeItem {
  readonly entity: string;
  readonly field: string;
  readonly case: string | null;
  readonly title: string;
  readonly value: unknown;
  readonly text: string;
  readonly original: unknown;
  readonly originalText: string;
  readonly state: 'set' | 'conflict' | 'lost';
  readonly editable: boolean;
}

/** Every change of the workspace: the Common ones, then each case with its own, and how many in all. */
export interface ChangesList {
  readonly revision: number;
  readonly common: readonly ChangeItem[];
  readonly cases: readonly { readonly case: CaseView; readonly changes: readonly ChangeItem[] }[];
  readonly count: number;
}

/**
 * A row of the Scenarios grid (ADR-0010 §4): one shift, the scenario it belongs to by name, and what it lacks, said on
 * the row. The Category and the Shift type are derived from the index and the size; none is a guess.
 */
export interface ShiftRow {
  // A field the server has no value for is left out of its answer, as every empty field of the wire is.
  readonly id: string;
  readonly row: number;
  readonly scenario?: string | null;
  readonly category?: string | null;
  readonly index?: string | null;
  readonly tenor?: string | null;
  /** The risk factor as the grid shows it: the tenor, `all` for a whole curve, `spot` for an FX rate. */
  readonly factor?: string | null;
  readonly type?: string | null;
  readonly size?: number | null;
  readonly sizeText?: string | null;
  readonly description?: string | null;
  readonly issue?: string | null;
  /** The column the issue is of, which the grid marks. */
  readonly issueColumn?: string | null;
}

/** An edit of a row of the Scenarios grid: an existing shift by its id, or a blank row by none, where the grid shows it,
 * and its cells as typed, by column; an empty text clears a cell. */
export interface ShiftEdit {
  readonly id: string | null;
  readonly row: number;
  readonly cells: Readonly<Record<string, string | null>>;
}

/** A scenario as the list beside the grid shows it: its shift count, whether Price prices it, and what blocks it. */
export interface ScenarioView {
  readonly name: string;
  readonly shifts: number;
  readonly included: boolean;
  readonly issue?: string | null;
}

/** One figure a measure reports, such as CVA's CVA Charge, and the trace term its cell opens where it is not the root
 * (ADR-0010 §7's note of 2026-11-08: spot IM's IM-R opens at `collect`). */
export interface MetricDefinition { readonly id: string; readonly title: string; readonly term?: string | null }

/** A measure as its module registered it (ADR-0010 §10). The page knows no measure's rules; it reads these. */
export interface RegisteredMeasure {
  readonly id: string;
  readonly title: string;
  readonly module: string;
  /** The module whose request prices it, and whose step's result holds its rows: its own, or another's (spot capital
   * is SA-CCR's measure, and XVA's request returns it with KVA on, ADR-0013 §2). */
  readonly request: string;
  readonly metrics: readonly MetricDefinition[];
  /** The metric the collapsed result line shows. */
  readonly headline: string;
  readonly declaresMembership: boolean;
  readonly traced: boolean;
  /** Whether a Run that priced it has a quote to copy (ADR-0010 §7, §11: XVA's, from CVA). */
  readonly quoted: boolean;
  /** The result tabs it draws beside Metrics (ADR-0010 §7, §10), such as spot IM's SIMM; none for most. */
  readonly tabs: readonly ResultTab[];
}

/** A result tab a measure registers (ADR-0010 §7, §10). `trace` draws the measure's trace: a card per root, then the
 * drill-down (ADR-0011 §6, ADR-0012 §7). `risks` draws the deltas the measure's request returned, and `profile` its
 * profiles (ADR-0010 §8's note of 2026-11-09); several measures may register one, and the dock draws it once. */
export interface ResultTab { readonly id: string; readonly title: string; readonly view: 'trace' | 'risks' | 'profile' }

/** A shared input tab's field a selected measure needs (ADR-0010 §10), such as CVA's recovery on Netting. */
export interface MeasureInput { readonly tab: string; readonly field: string; readonly measure: string }

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
  /** The selected modules' trade columns, such as XVA's PV01, Margin and PV Adj (ADR-0010 §6, §10). */
  readonly tradeColumns: readonly RegisteredColumn[];
  /** The shared input tabs' fields the selected measures need: only these show, and a hidden one keeps its value. */
  readonly inputs: readonly MeasureInput[];
  /** How many of those cells on the new trades still wait for a result: the page asks again while any does. */
  readonly pendingCells: number;
  /** The currency the figures are shown in, and those it may be (ADR-0010 §1). */
  readonly displayCurrency: string;
  readonly currencies: readonly string[];
  /** The selected modules whose figures come from a simulated provider: the page says `SIMULATED` while any is. */
  readonly simulated: readonly string[];
  /** Every case Price prices, the Base first (ADR-0010 §3). */
  readonly cases: readonly CaseView[];
  /** How many changes the Changes tab lists, a case's among them. */
  readonly changes: number;
  /** The scenarios in order of their first row, and every shift row in the order made (ADR-0010 §4). Price prices the
   * base market and each included scenario, crossed with every case, and the cap counts those pairs. */
  readonly scenarios: readonly ScenarioView[];
  readonly shifts: readonly ShiftRow[];
  /** The selected modules' settings a selected measure needs, a section a module (ADR-0010 §8); none hides the tab. */
  readonly settings: readonly ModuleSettings[];
}

export interface RowPage {
  readonly rows: readonly EffectiveTrade[];
  readonly start: number;
  /** How many rows the grid has: under conditions (ADR-0016 note 41), how many they leave. */
  readonly totalCount: number;
  readonly orderToken: string;
  readonly revision: number;
  /** The currency every row's `pv` is in: the display currency as this page was read (ADR-0016 note 49). */
  readonly pvCurrency: string;
  /** Under conditions, how many rows the grid has with none. */
  readonly of?: number;
}

export interface CarrySummary { readonly carried: number; readonly conflicted: number; readonly lost: number }

export interface ScopeDrop { readonly scope: string; readonly count: number; readonly byKind: Readonly<Record<string, number>> }

export type Resolution = 'reset' | 'keep' | 'takeSource' | 'acknowledge';
