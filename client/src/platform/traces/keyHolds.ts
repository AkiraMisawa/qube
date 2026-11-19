import type { TraceAddress } from './contracts';
import { telemetry } from '../telemetry';

// The trace's keys wait for a term's children before the keys after them are taken (ADR-0011 §6's note of 2026-11-18),
// so that a reader at machine speed ends where a slow one does (spine 6). What the tree and the drill-in list share: the
// bound past which a wait is given up, and the log line that says so.

/**
 * How long a key that waits for a term's children waits: a bound for a server that does not answer, not a wait for a
 * race. While the read answers, the keys after it are taken in their order once the children are shown.
 */
export const CHILDREN_WAIT_MS = 10_000;

/** The bound in words, for what is said on screen. */
export const CHILDREN_WAIT_WORDS = `${CHILDREN_WAIT_MS / 1000} seconds`;

/** What finds a trace in a log line: its Run, case, scenario and measure (CLAUDE.md, "Log enough to diagnose"). */
export const traceWhere = (where: TraceAddress): string =>
  `Run ${where.runId}, case ${where.case}, scenario ${where.scenario}, measure ${where.measure}`;

/** Logs a wait for a term's children given up, with what finds its trace. */
export function reportChildrenNotRead(where: TraceAddress, termId: string, outcome: string): void {
  telemetry.report({
    level: 'warning', kind: 'trace-key',
    message: `The children of trace term ${termId} (${traceWhere(where)}) were not read within ${CHILDREN_WAIT_MS} ms; ${outcome}.`,
  });
}

/**
 * Keys held until something they wait for is there, each let go once `release` finds it ready, or given up once the
 * bound has passed. A hold let go, either way, is let go once: its timer is cleared and `giveUp` is never called late.
 */
export class KeyHolds<H> {
  private holds: readonly { readonly hold: H; readonly settle: () => void }[] = [];

  constructor(private readonly waitMs: number = CHILDREN_WAIT_MS) {}

  /** How many keys are held. */
  get size(): number { return this.holds.length; }

  /** Holds a key: the promise settles once `release` lets it go, or once the bound passes, after `giveUp` is called. */
  hold(hold: H, giveUp: (hold: H) => void): Promise<void> {
    return new Promise<void>((resolve) => {
      const entry = {
        hold,
        settle: () => { clearTimeout(timer); this.holds = this.holds.filter((h) => h !== entry); resolve(); },
      };
      const timer = setTimeout(() => { entry.settle(); giveUp(hold); }, this.waitMs);
      this.holds = [...this.holds, entry];
    });
  }

  /** Lets go every key `ready` says may go on, in the order they were held. */
  release(ready: (hold: H) => boolean): void {
    for (const entry of this.holds) if (ready(entry.hold)) entry.settle();
  }

  /** Lets every key go, giving none up: what held them is gone. */
  releaseAll(): void {
    for (const entry of this.holds) entry.settle();
  }
}
