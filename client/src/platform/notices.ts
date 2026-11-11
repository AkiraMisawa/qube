// What a command says back (ADR-0006's note 2 of 2026-11-11, ADR-0016 §6): it was done, or it was refused and why.
// The caller states which; nothing reads the kind from the words.

/** `done`: the command ran, and this is what it did, or why there was nothing to do. `refused`: it did not run. */
export type NoticeKind = 'done' | 'refused';

export interface Notice {
  readonly kind: NoticeKind;
  readonly message: string;
  /** A done notice that says something is left for the user to decide stays until it is closed, as a refusal does. */
  readonly stays?: boolean;
}

export type Notify = (kind: NoticeKind, message: string, options?: { readonly stays?: boolean }) => void;

/** What the application says, and how it is heard by something other than the strip that draws it. */
export interface Hearing {
  /** Says a notice as before, and tells whoever hears. */
  readonly notify: Notify;
  /** Hears every notice said from now on; the answer stops hearing. */
  hear(listener: (notice: Notice) => void): () => void;
}

/**
 * A `notify` that can be heard (ADR-0016 §9, note 24 of 2026-11-11): the command line, kept open by its chain key, says
 * what the command it ran said, whichever page or command said it and however late.
 */
export function hearing(notify: Notify): Hearing {
  const listeners = new Set<(notice: Notice) => void>();
  return {
    notify: (kind, message, options) => {
      notify(kind, message, options);
      const notice: Notice = { kind, message, ...(options?.stays === true ? { stays: true } : {}) };
      for (const listener of [...listeners]) listener(notice);
    },
    hear: (listener) => {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
  };
}

/** How long a done notice stays while neither the pointer nor the keyboard is on it, in milliseconds. */
export const DONE_NOTICE_MS = 6000;

/**
 * Whether a notice leaves by itself once `DONE_NOTICE_MS` has passed: a done one that leaves nothing to decide, while
 * nothing holds it. A refusal never does: its reason is read until it is closed or replaced.
 */
export function leavesByItself(notice: Notice, held: { readonly pointer: boolean; readonly keyboard: boolean }): boolean {
  return notice.kind === 'done' && notice.stays !== true && !held.pointer && !held.keyboard;
}
