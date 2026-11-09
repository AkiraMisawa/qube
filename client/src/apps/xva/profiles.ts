// The Profile tab as a reading of the Run (the owner's Profile of 2026-11-08, ADR-0010 §8's note of 2026-11-09): the
// profiles a request returned (`portfolio.profiles`), such as the exposure profile CVA and FVA integrate and the capital
// profile KVA charges for, for the case and the market in view. A profile is a table of its dates, each series Before,
// After and Diff, and a chart of the same points. Every figure is the request's; nothing is interpolated or filled here.

/** A profile the Run returned for the case and market in view, as its selector names it. */
export interface ProfileChoice { readonly id: string; readonly title: string; readonly currency: string | undefined }

/** One series of a profile, such as EE or EAD. */
export interface ProfileSeries { readonly id: string; readonly title: string }

/** A side's value at a date, or none where the side has no point there: past its last maturity, never a 0 made up. */
export type ProfileValue = number | undefined;

/** One date of the profile: each series' Before, After and Diff, the Diff only where both sides have a point. */
export interface ProfileRow {
  readonly years: number;
  readonly date: string;
  readonly values: Readonly<Record<string, { readonly before: ProfileValue; readonly after: ProfileValue; readonly diff: ProfileValue }>>;
}

export interface ProfileTable {
  readonly profile: ProfileChoice;
  readonly series: readonly ProfileSeries[];
  readonly rows: readonly ProfileRow[];
  /** Whether any point of a side is there: a side the Run did not price has no column. */
  readonly sides: { readonly before: boolean; readonly after: boolean };
}

type Row = Readonly<Record<string, unknown>>;

const text = (v: unknown): string | undefined => (typeof v === 'string' && v !== '' ? v : undefined);
const number = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) ? v : undefined);

const inView = (rows: readonly Row[], caseId: string, scenario: string) =>
  rows.filter((r) => r.case === caseId && (text(r.scenario) ?? 'base') === scenario);

/** The profiles of the case and market in view, in the order the Run returned them. */
export function profilesOf(rows: readonly Row[], caseId: string, scenario: string): readonly ProfileChoice[] {
  const found = new Map<string, ProfileChoice>();
  for (const r of inView(rows, caseId, scenario)) {
    const id = text(r.profile);
    if (id !== undefined && !found.has(id)) found.set(id, { id, title: text(r.profileTitle) ?? id, currency: text(r.currency) });
  }
  return [...found.values()];
}

/**
 * One profile's table: a row per date either side has a point at, earliest first, each series' sides by date. A point
 * the request wrote with no figure is refused by name rather than drawn as a gap or a 0 (spine 1).
 */
export function profileTable(rows: readonly Row[], profile: ProfileChoice, caseId: string, scenario: string): ProfileTable {
  const own = inView(rows, caseId, scenario).filter((r) => r.profile === profile.id);
  const series = new Map<string, ProfileSeries>();
  const dates = new Map<number, { date: string; values: Record<string, { before: ProfileValue; after: ProfileValue; diff: ProfileValue }> }>();
  const sides = { before: false, after: false };
  for (const r of own) {
    const id = text(r.series);
    const years = number(r.years);
    const value = number(r.value);
    const side = r.side === 'before' || r.side === 'after' ? r.side : undefined;
    const date = text(r.date);
    if (id === undefined || years === undefined || date === undefined || side === undefined || value === undefined)
      throw new Error(`A point of the ${profile.title} profile has no series, time, date, side or figure; the Run's profile cannot be read.`);
    if (!series.has(id)) series.set(id, { id, title: text(r.seriesTitle) ?? id });
    const at = dates.get(years) ?? { date, values: {} };
    const cell = at.values[id] ?? { before: undefined, after: undefined, diff: undefined };
    cell[side] = value;
    at.values[id] = cell;
    dates.set(years, at);
    sides[side] = true;
  }
  const table: ProfileRow[] = [...dates.entries()].sort(([a], [b]) => a - b).map(([years, at]) => ({
    years, date: at.date,
    values: Object.fromEntries(Object.entries(at.values).map(([id, v]) => [id, {
      ...v, diff: v.before !== undefined && v.after !== undefined ? v.after - v.before : undefined,
    }])),
  }));
  return { profile, series: [...series.values()], rows: table, sides };
}

/**
 * A chart's series for each profile series and side the table has: its id, its name and the points it has, by time. A
 * series' After comes before its Before: a chart gives the first line style it is asked for, solid, to After.
 */
export function chartSeries(table: ProfileTable): readonly { readonly id: string; readonly name: string; readonly series: string; readonly side: 'before' | 'after'; readonly points: readonly { readonly x: number; readonly y: number }[] }[] {
  const sides = (['after', 'before'] as const).filter((s) => table.sides[s]);
  return table.series.flatMap((s) => sides.map((side) => ({
    id: `${s.id}.${side}`, name: `${s.title} ${side === 'before' ? 'Before' : 'After'}`, series: s.id, side,
    points: table.rows.flatMap((r) => {
      const y = r.values[s.id]?.[side];
      return y === undefined ? [] : [{ x: r.years, y }];
    }),
  })));
}
