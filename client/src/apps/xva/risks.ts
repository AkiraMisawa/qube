import type { RegisteredMeasure } from './contracts';
import { diffOf, side, type MetricCell } from './metrics';

// The Risks tab as a reading of the Run (the owner's Risks of 2026-11-08, ADR-0010 §8's note of 2026-11-09): per measure
// of the tab, its credit, IR and FX deltas, Before, After and Diff, for the case and the market in view. The deltas are
// the request's (`portfolio.risks`), each the bumped figure minus the priced one, never computed here. A measure with no
// deltas says why, from the request's own row, and is never a table of zeros.

/** One factor's delta of a measure's headline metric. */
export interface RiskLine {
  readonly key: string;
  readonly riskClass: string;
  readonly factor: string;
  readonly bump: string;
  readonly before: MetricCell;
  readonly after: MetricCell;
  readonly diff: MetricCell;
  /** Why a side has no figure, where the request said. */
  readonly reason: string | undefined;
}

/** A measure's part of the tab: its deltas, or why it has none. */
export interface RiskSection {
  readonly measure: string;
  readonly title: string;
  readonly metricTitle: string | undefined;
  readonly currency: string | undefined;
  readonly lines: readonly RiskLine[];
  /** Why the measure has no deltas for this case and market; none while it has them. */
  readonly reason: string | undefined;
}

type Row = Readonly<Record<string, unknown>>;

const text = (v: unknown): string | undefined => (typeof v === 'string' && v !== '' ? v : undefined);

/**
 * The tab's sections, a measure each in the order the tab lists them, from the rows of the case and the market in view. A
 * row written with no scenario is the base market's. A measure the Run returned no row for says so, rather than show an
 * empty table as if it had no sensitivity.
 */
export function riskSections(rows: readonly Row[], measures: readonly string[], register: readonly RegisteredMeasure[], caseId: string, scenario: string): readonly RiskSection[] {
  const inView = rows.filter((r) => r.case === caseId && (text(r.scenario) ?? 'base') === scenario);
  return measures.map((measure) => {
    const title = register.find((m) => m.id === measure)?.title ?? measure;
    const own = inView.filter((r) => r.measure === measure);
    const first = own[0];
    if (first === undefined)
      return { measure, title, metricTitle: undefined, currency: undefined, lines: [], reason: `This Run returned no deltas of ${title} for this case and market.` };
    const lines = own.flatMap((r, i): RiskLine[] => {
      const factor = text(r.factor);
      if (factor === undefined) return [];
      const riskClass = text(r.riskClass);
      const bump = text(r.bump);
      // A delta that does not say what was bumped, or by how much, is no delta a reader can use (spine 1).
      if (riskClass === undefined || bump === undefined) throw new Error(`The delta of ${title} to ${factor} does not say its risk class or its bump; the Run's deltas cannot be read.`);
      const before = side(r.beforeStatus, r.before);
      const after = side(r.afterStatus, r.after);
      return [{ key: `${measure}:${factor}:${i}`, riskClass, factor, bump, before, after, diff: diffOf(before, after, r.diff), reason: text(r.reason) }];
    });
    // A row with no factor is the request's word for why the measure has none here (risks not asked, the measure refused).
    const none = own.find((r) => text(r.factor) === undefined);
    return {
      measure, title, metricTitle: text(first.metricTitle), currency: text(first.currency), lines,
      reason: lines.length > 0 ? undefined : text(none?.reason) ?? `This Run returned no deltas of ${title} for this case and market.`,
    };
  });
}
