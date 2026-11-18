import { useEffect, useRef, useState, type KeyboardEvent, type RefObject } from 'react';
import type { TermCrumb } from './contracts';
import type { ChildrenRead } from './tree';
import { telemetry } from '../telemetry';

// Below `sm` the trace drills in a level at a time (ADR-0011 §6), and its list of the selected term's children takes the
// arrow keys (the note of 2026-11-18, after Akira: "exploring the drill-down with the arrow keys, one after another"):
// ↑ and ↓ choose a child, Home and End the first and the last, → or Enter goes into the child, ← or Backspace goes up to
// the parent. The list is drawn anew for every term, so a move hands the keyboard to the list's frame until the new list
// is drawn, and the keys pressed meanwhile are taken in their order once it is: a reader at machine speed ends where a
// slow one does (spine 6). The keyboard then goes to the first child of the term gone into, or back to the child come up
// from, or stays on the frame where the term has no children, so that ← still goes up.

/** The keys the list takes, pressed without a modifier. */
const KEYS: ReadonlySet<string> = new Set(['ArrowDown', 'ArrowUp', 'Home', 'End', 'ArrowRight', 'Enter', ' ', 'ArrowLeft', 'Backspace']);

/**
 * How long a move waits for its term's children to be read before it is given up: a bound for a server that does not
 * answer, not a wait for a race. Past it the keys pressed meanwhile are dropped, said under the list and logged.
 */
export const DRILL_WAIT_MS = 10_000;

/** A move the list waits to draw: from the term selected, to a term, with the keyboard to go to one of its children. */
interface Move {
  readonly from: string | undefined;
  readonly term: string;
  readonly label: string;
  readonly child: string | undefined;
  /** By key the page scrolls to the child given the keyboard; by pointer it does not, so a tap leaves the formula in view. */
  readonly scroll: boolean;
  readonly timer: ReturnType<typeof setTimeout>;
}

export interface DrillKeys {
  /** The frame around the term and its list: it takes the keys, and holds the keyboard while a list is drawn anew. */
  readonly frame: RefObject<HTMLDivElement | null>;
  readonly onKeyDown: (event: KeyboardEvent<HTMLDivElement>) => void;
  /** A child pressed by pointer: the list goes into it, and the keyboard follows as it does by key. */
  readonly pick: (id: string, label: string) => void;
  /** Why keys were not taken, said under the list until the next move. */
  readonly note: string | undefined;
}

/**
 * The keys of the drill-in list. `selected` is the term selected, `path` its path from the root as read with it,
 * `children` its children as read, and `select` selects another term. Items of the list carry `data-drill-item`, and a
 * child's item its id in `data-child-id` and its label in `data-child-label`.
 */
export function useDrillKeys({ selected, path, children, select }: {
  readonly selected: string | undefined;
  readonly path: readonly TermCrumb[] | undefined;
  readonly children: ChildrenRead | undefined;
  readonly select: (id: string) => void;
}): DrillKeys {
  const frame = useRef<HTMLDivElement>(null);
  const move = useRef<Move | undefined>(undefined);
  const queued = useRef<string[]>([]);
  const [note, setNote] = useState<string | undefined>(undefined);
  // What this render read, for keys taken after it: the parent to go up to is the one the term was read with.
  const latest = useRef({ selected, path, select });
  latest.current = { selected, path, select };

  const settle = (wanted: Move) => {
    clearTimeout(wanted.timer);
    if (move.current === wanted) move.current = undefined;
    if (frame.current !== null) delete frame.current.dataset.moving;
  };

  const go = (term: string, label: string, child: string | undefined, scroll: boolean) => {
    const at = frame.current;
    // The item the keyboard is on goes with the list it is in: the frame holds the keyboard until the new list shows.
    at?.focus({ preventScroll: true });
    if (at !== null) at.dataset.moving = '';
    setNote(undefined);
    const wanted: Move = {
      from: latest.current.selected, term, label, child, scroll,
      timer: setTimeout(() => {
        if (move.current !== wanted) return;
        settle(wanted);
        const dropped = queued.current.length;
        queued.current = [];
        setNote(`The terms below ${label} are still being read${dropped > 0 ? ', so the keys pressed meanwhile were not taken' : ''}.`);
        telemetry.report({ level: 'warning', kind: 'trace-key', message: `The children of trace term ${term} were not read within ${DRILL_WAIT_MS} ms; ${dropped} key(s) pressed in the drill-in list meanwhile were dropped.` });
      }, DRILL_WAIT_MS),
    };
    // Only a press can move while a move waits (keys wait behind it): the keys pressed for the list it replaces go too.
    if (move.current !== undefined) { settle(move.current); queued.current = []; }
    move.current = wanted;
    latest.current.select(term);
  };

  /** One key, at the item the keyboard is on (none: the frame). */
  const act = (key: string, item: HTMLElement | undefined) => {
    const at = frame.current;
    if (at === null) return;
    const items = [...at.querySelectorAll<HTMLElement>('[data-drill-item]')];
    const index = item === undefined ? -1 : items.indexOf(item);
    switch (key) {
      case 'ArrowDown': (items[index + 1] ?? item)?.focus(); return;
      case 'ArrowUp': items[Math.max(index - 1, 0)]?.focus(); return;
      case 'Home': items[0]?.focus(); return;
      case 'End': items.at(-1)?.focus(); return;
      case 'ArrowRight': case 'Enter': case ' ': {
        const child = item?.dataset.childId;
        if (child !== undefined) go(child, item?.dataset.childLabel ?? child, undefined, true);
        // The button that reads more children is pressed by Enter and Space, as a button is.
        else if (item !== undefined && key !== 'ArrowRight') item.click();
        return;
      }
      case 'ArrowLeft': case 'Backspace': {
        const { selected: from, path: shown } = latest.current;
        const parent = shown?.at(-2);
        if (parent !== undefined && from !== undefined) go(parent.id, parent.label, from, true);
        return;
      }
    }
  };

  const itemOf = (element: Element | null): HTMLElement | undefined =>
    element instanceof HTMLElement && element !== frame.current && element.hasAttribute('data-drill-item') && frame.current?.contains(element) === true
      ? element : undefined;

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || !KEYS.has(event.key)) return;
    const item = itemOf(event.target as Element | null);
    // Keys pressed in anything else in the frame, the leaf rows' grid or a formula's operand, stay its own.
    if (item === undefined && event.target !== frame.current) return;
    event.preventDefault();
    if (move.current !== undefined) { queued.current.push(event.key); return; }
    act(event.key, item);
  };

  // Once the term moved to is selected, read and its children read (or not readable), the keyboard goes to its list and
  // the keys pressed meanwhile are taken; a move another hand made first (the breadcrumb, an operand, a command) drops
  // them, since they were pressed for a list that is no longer shown.
  useEffect(() => {
    const wanted = move.current;
    const at = frame.current;
    if (wanted === undefined || at === null || selected === wanted.from) return;
    if (selected !== wanted.term) { settle(wanted); queued.current = []; return; }
    const failed = children?.kind === 'failed';
    if (!failed && (path?.at(-1)?.id !== wanted.term || children === undefined || children.kind === 'loading')) return;
    settle(wanted);
    // A reader who put the keyboard elsewhere meanwhile keeps it there.
    const active = at.ownerDocument.activeElement;
    if (active !== null && active !== at && active !== at.ownerDocument.body && !at.contains(active)) { queued.current = []; return; }
    const items = [...at.querySelectorAll<HTMLElement>('[data-child-id]')];
    (items.find((i) => i.dataset.childId === wanted.child) ?? items[0] ?? at).focus({ preventScroll: !wanted.scroll });
    while (move.current === undefined) {
      const key = queued.current.shift();
      if (key === undefined) break;
      act(key, itemOf(at.ownerDocument.activeElement));
    }
  });

  // A view taken away waits for nothing.
  useEffect(() => () => { if (move.current !== undefined) settle(move.current); }, []);

  return { frame, onKeyDown, pick: (id, label) => go(id, label, undefined, false), note };
}
