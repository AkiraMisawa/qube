import type { RegisteredMeasure, ResultTab } from './contracts';
import { reasons, type MetricsTable } from './metrics';

// The result dock's tabs (ADR-0010 §7, §10): Metrics, then the tabs the selected measures register, such as spot IM's
// SIMM (ADR-0012 §7), where the Run in view priced them, since a tab draws that Run's trace. A measure left out after
// the Price keeps its figures in Metrics but loses its tab, and one added since has no tab until the next Price.

/** A tab of the dock: Metrics, or a measure's own with the measure it draws. */
export type DockTab = { readonly id: 'metrics'; readonly title: 'Metrics'; readonly view: 'metrics' } | MeasureTab;

/**
 * A tab measures register, with the measures it draws: one for a trace, which is one measure's, or several for a tab
 * several register under one id, such as CVA's and FVA's Risks, drawn once (ADR-0010 §8's note of 2026-11-09). `measure`
 * is the first of them.
 */
export type MeasureTab = ResultTab & { readonly measure: string; readonly measures: readonly string[] };

export const METRICS_TAB: DockTab = { id: 'metrics', title: 'Metrics', view: 'metrics' };

/**
 * Metrics, then the tabs of each measure both selected and priced, in the register's order; Metrics alone where the
 * Run names no measures. A tab several measures register is one tab, where the first of them puts it, listing each.
 */
export function dockTabs(register: readonly RegisteredMeasure[], priced: readonly string[] | undefined, selected: readonly string[]): readonly DockTab[] {
  if (priced === undefined) return [METRICS_TAB];
  const tabs = new Map<string, MeasureTab>();
  for (const m of register.filter((r) => priced.includes(r.id) && selected.includes(r.id)))
    for (const t of m.tabs) {
      const seen = tabs.get(t.id);
      tabs.set(t.id, seen === undefined ? { ...t, measure: m.id, measures: [m.id] } : { ...seen, measures: [...seen.measures, m.id] });
    }
  return [METRICS_TAB, ...tabs.values()];
}

/**
 * Why a measure's tab has nothing to draw: the measure's reasons, where no side of any of its lines was priced (a
 * counterparty with no IM CSA has no spot IM, and no trace of it). None while a side was priced or is still pending, when
 * the tab draws its view, which says for itself what it cannot read.
 */
export function unpricedReasons(table: MetricsTable | undefined, measure: string): readonly { readonly text: string; readonly failed: boolean }[] | undefined {
  if (table === undefined) return undefined;
  const lines = table.lines.filter((l) => l.measure === measure);
  if (lines.length === 0 || lines.some((l) => [l.before.status, l.after.status].some((s) => s === 'priced' || s === 'pending'))) return undefined;
  return reasons({ ...table, lines }).map((r) => ({ text: r.text, failed: r.failed }));
}

/** The rate a measure's figures were converted at, said once over its tab; none where its lines name none. */
export function conversionOf(table: MetricsTable | undefined, measure: string): string | undefined {
  const said = [...new Set((table?.lines ?? []).flatMap((l) => (l.measure === measure && l.conversion !== undefined ? [l.conversion] : [])))];
  return said.length === 0 ? undefined : said.join(' ');
}
