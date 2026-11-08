import type { RegisteredMeasure, ResultTab } from './contracts';
import { reasons, type MetricsTable } from './metrics';

// The result dock's tabs (ADR-0010 §7, §10): Metrics, then the tabs the measures the Run in view priced register, such
// as spot IM's SIMM (ADR-0012 §7). They follow the Run, as its Metrics do: a measure left out after the Price still
// shows its figures and its tab, and one added since has neither until the next Price.

/** A tab of the dock: Metrics, or a measure's own with the measure it draws. */
export type DockTab = { readonly id: 'metrics'; readonly title: 'Metrics'; readonly view: 'metrics' } | MeasureTab;

/** A tab a measure registers, with the measure it draws. */
export type MeasureTab = ResultTab & { readonly measure: string };

export const METRICS_TAB: DockTab = { id: 'metrics', title: 'Metrics', view: 'metrics' };

/** Metrics, then each priced measure's tabs in the register's order; Metrics alone where the Run names no measures. */
export function dockTabs(register: readonly RegisteredMeasure[], priced: readonly string[] | undefined): readonly DockTab[] {
  if (priced === undefined) return [METRICS_TAB];
  return [METRICS_TAB, ...register.filter((m) => priced.includes(m.id)).flatMap((m) => m.tabs.map((t) => ({ ...t, measure: m.id })))];
}

/**
 * Why a measure's tab has nothing to draw: the measure's reasons, where no side of any of its lines was priced (a
 * counterparty with no IM CSA has no spot IM, and no trace of it). None while a side was priced or is still pending, when
 * the tab draws its view, which says for itself what it cannot read.
 */
export function unpricedReasons(table: MetricsTable | undefined, measure: string): readonly { readonly text: string; readonly failed: boolean }[] | undefined {
  const lines = table?.lines.filter((l) => l.measure === measure) ?? [];
  if (lines.length === 0 || lines.some((l) => [l.before.status, l.after.status].some((s) => s === 'priced' || s === 'pending'))) return undefined;
  return reasons({ ...table!, lines }).map((r) => ({ text: r.text, failed: r.failed }));
}
