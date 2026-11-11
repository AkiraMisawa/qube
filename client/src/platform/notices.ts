// What a command says back (ADR-0006's note 2 of 2026-11-11, ADR-0016 §6): it was done, or it was refused and why.
// The caller states which; nothing reads the kind from the words.

/** `done`: the command ran, and this is what it did, or why there was nothing to do. `refused`: it did not run. */
export type NoticeKind = 'done' | 'refused';

export interface Notice {
  readonly kind: NoticeKind;
  readonly message: string;
}

export type Notify = (kind: NoticeKind, message: string) => void;

/** How long a done notice stays while neither the pointer nor the keyboard is on it, in milliseconds. */
export const DONE_NOTICE_MS = 6000;
