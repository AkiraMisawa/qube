import { useCallback, useMemo, useState, useSyncExternalStore } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { Api } from '../api';
import type { FilterSaid } from './GridFilterLine';
import { narrows, whereOf, type Filter } from './lineView';

// A grid filtered from the line (ADR-0016 note 41 of 2026-11-11), as its page shows it. The conditions are kept in the
// grid's source; nothing here reads one: which rows they leave is the server's to say.

/** What of a grid's source its conditions are heard from: ExGrid's `subscribe` and the Filter in its `snapshot`. */
export interface Heard {
  subscribe(listener: () => void): () => void;
  snapshot(): { readonly filter: Filter | undefined };
}

/** The conditions a grid's source holds from the line, read again whenever the source changes. */
export function useWhere(source: Heard): string | undefined {
  const subscribe = useCallback((listener: () => void) => source.subscribe(listener), [source]);
  return useSyncExternalStore(subscribe, () => whereOf(source.snapshot().filter));
}

/** Whether a grid's source filters its rows by its own columns, read again whenever the source changes. */
function useNarrows(source: Heard): boolean {
  const subscribe = useCallback((listener: () => void) => source.subscribe(listener), [source]);
  return useSyncExternalStore(subscribe, () => narrows(source.snapshot().filter));
}

/** The grid a page holds every row of: the workspace and the table its rows are of, and the revision the page shows. */
export interface HeldGrid {
  readonly api: Pick<Api, 'shown'>;
  readonly workspaceId: string;
  readonly table: string;
  /** None until the page has read its workspace. */
  readonly revision: number | undefined;
}

/** What the server last answered of a grid's conditions: the rows they left at a revision, or why they could not be read. */
export type Left = { readonly where: string; readonly revision: number } & ({ readonly keys: readonly string[]; readonly of: number } | { readonly unread: string });

/** What a grid shows now: its conditions, the revision its page shows, and whether it filters by columns of its own. */
export interface Showing {
  readonly where: string | undefined;
  readonly revision: number | undefined;
  readonly own: boolean;
}

const NO_ROWS: readonly never[] = [];

/**
 * The rows a grid whose page holds every row is given under the conditions the line gave it, and what it says of them,
 * from what the server last answered (`last`). Until the server answers for what the grid shows now, the grid keeps
 * what that answer left it: the rows of the same conditions at the revision before, the rows of the conditions before
 * these, every row where nothing was answered yet, and no row, with why, where the conditions could not be read. What
 * a line then reads does not depend on which: the grid's view says the conditions and names no row for them.
 *
 * A grid that also filters by its own columns names the rows in its Window in its view, since only it knows which
 * those filters leave, and the server reads the conditions among them. Rows kept from an older answer would leave out a
 * row an edit has since brought into the conditions, so such a grid is given every row until the server has answered
 * for the conditions and the revision it shows.
 */
export function leftOf<Row>(rows: readonly Row[], keyOf: (row: Row) => string, showing: Showing, last: Left | undefined): { readonly rows: readonly Row[]; readonly said: FilterSaid | undefined } {
  const { where, revision, own } = showing;
  if (where === undefined) return { rows, said: undefined };
  const same = last !== undefined && last.where === where;
  if (last === undefined || (own && !(same && last.revision === revision))) return { rows, said: { where } };
  if ('unread' in last) return { rows: NO_ROWS, said: same ? { where, unread: `No row is shown: ${last.unread}` } : { where } };
  const kept = new Set(last.keys);
  const left = rows.filter((row) => kept.has(keyOf(row)));
  return { rows: left, said: same ? { where, counted: { left: left.length, of: last.of } } : { where } };
}

/**
 * The rows a grid whose page holds every row shows under the conditions the line gave it, and what it says of them
 * ({@link leftOf}). The server is asked which rows the conditions leave, again at each revision the page shows; an
 * answer that did not come is said as conditions that could not be read are, and asked for again when the conditions
 * are given again.
 */
export function useHeldFilter<Row>(source: Heard, grid: HeldGrid, rows: readonly Row[], keyOf: (row: Row) => string): { readonly rows: readonly Row[]; readonly said: FilterSaid | undefined } {
  const { api, workspaceId, table, revision } = grid;
  const where = useWhere(source);
  const own = useNarrows(source);
  const answer = useQuery({
    queryKey: ['shown', workspaceId, table, where, revision],
    queryFn: ({ signal }) => {
      if (where === undefined) throw new Error(`The rows ${table} shows were asked for with no conditions to read.`);
      return api.shown(workspaceId, { table, where }, signal);
    },
    enabled: where !== undefined && revision !== undefined,
    staleTime: Infinity,
    retry: false,
  });
  const { data, error } = answer;
  const answered = useMemo((): Left | undefined => {
    if (where === undefined || revision === undefined) return undefined;
    if (error !== null) return { where, revision, unread: error.message };
    return data === undefined ? undefined : { where, revision, keys: data.keys, of: data.of };
  }, [where, revision, data, error]);
  // The last answer is kept while the next is awaited, and dropped with the conditions: a grid filtered again starts
  // from every row, which is what it then has.
  const [last, setLast] = useState<Left>();
  const heard = where === undefined ? undefined : answered ?? last;
  if (heard !== last) setLast(heard);
  return useMemo(() => leftOf(rows, keyOf, { where, revision, own }, heard), [rows, keyOf, where, revision, own, heard]);
}
