import { useMemo, useRef, useState } from 'react';
import type { LineDraft } from '../contracts';
import type { Drafts } from '../commands/served';

/** A page's draft as it holds it: what its dialog is drawn of, what its door reads, and how a pending line is taken back. */
export interface DraftHeld {
  /** The open draft, for the dialog to draw; none while no dialog is open. */
  readonly draft: LineDraft | undefined;
  /** The same draft as the page's door reads and changes it. */
  readonly drafts: Drafts;
  /**
   * Takes a pending line back from `seen`, the draft the press was made on: the lines after it are read again on what
   * is left. Answers false, and takes nothing, where the draft has changed since: the line pressed may not be the one
   * at its place any more (spine 6).
   */
  readonly drop: (seen: LineDraft, index: number) => boolean;
}

/**
 * The draft of a page that opens rows' dialogs (ADR-0016 §10, note 54 of 2026-11-12): view state, held here and
 * nowhere else. The door reads it in its own turn, between two drawings, so what it reads is held beside the state
 * that draws the dialog and changes with it at once. Each change makes a new draft: one read earlier is as it was, so a
 * reading can be told from a reading of the draft as it now is.
 */
export function useDraft(): DraftHeld {
  const [draft, setDraft] = useState<LineDraft | undefined>(undefined);
  const held = useRef<LineDraft | undefined>(undefined);
  // The same for the page's life: the door is made once with them.
  const changes = useMemo(() => {
    const hold = (next: LineDraft | undefined) => { held.current = next; setDraft(next); };
    const open = (): LineDraft => {
      if (held.current === undefined) throw new Error('No dialog is open, and its draft was to change.');
      return held.current;
    };
    const drafts: Drafts = {
      current: () => held.current,
      open: (table, key, revision) => hold({ table, key, revision, lines: [] }),
      gain: (line) => hold({ ...open(), lines: [...open().lines, line] }),
      moved: (revision) => hold({ ...open(), revision }),
      close: () => hold(undefined),
    };
    const drop = (seen: LineDraft, index: number): boolean => {
      if (held.current !== seen) return false;
      hold({ ...seen, lines: seen.lines.filter((_, at) => at !== index) });
      return true;
    };
    return { drafts, drop };
  }, []);
  return { draft, ...changes };
}
