import { createContext, useCallback, useContext, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

// A page in the main window is drawn once, and moved into the place that shows it: a panel's page into the strip's page
// or an area's, the AI pane into the shell's side or its drawer (ADR-0018 §5's note of 2026-11-15). So a change between
// the strip and the areas moves the page in view on both sides, the active panel's, with what it holds: what the user
// typed and has not committed, a dialog, a notice, a busy state, the keyboard's place and a grid's scroll. A place goes
// before the next one comes, at once for the strip and a moment after for the areas, which dockview draws once it is
// made: between the two the page is held out of sight, in a box of the place it left, its size as the browser last
// reported it, so that the page's layout does not change while it waits (a grid made as tall as its rows by another
// layout would lose its scroll). A page no place shows, and that is not carried across such a change, goes as it did
// before: only the pages in view are mounted (ADR-0018 §6). A page in another window is drawn there, as a grid reads the
// window it is created in (§9). The AI pane changes place at the same width, the side being no room for it below 768px
// (ADR-0007 §7).

/** Where each page in the main window stands, by its id: the place that shows it, or held between one place and the next. */
export interface PagePlaces {
  /**
   * Shows the panel's page in this place: the one drawn already, moved here, or one drawn anew. Answers its release; none
   * for a place in another window, which shows no page drawn here.
   */
  readonly claim: (pageId: string, place: HTMLElement) => (() => void) | undefined;
  /** Where the pages are held between one place and the next; none while the panels are not drawn. */
  readonly holds: (element: HTMLElement | null) => void;
  readonly subscribe: (heard: () => void) => () => void;
  /** Changes as a page is placed or let go. */
  readonly version: () => number;
  /** The pages, each with the element its content is drawn in and whether a place shows it. */
  readonly pages: () => readonly HostedPage[];
  /** Takes away the pages no place shows, but those `stays` names. */
  readonly drop: (stays: (pageId: string) => boolean) => void;
}

export interface HostedPage {
  readonly pageId: string;
  readonly node: HTMLElement;
  readonly placed: boolean;
}

interface Page {
  readonly node: HTMLElement;
  /**
   * The places that ask for the page, each behind those that asked before it, but one out of the document behind those
   * in it: it is shown in the last. Two ask at once as dockview brings a panel back from another window: the area it is
   * drawn in, and the one that drew it before, out of the document, which dockview shows again as the panel is next
   * chosen in its area.
   */
  readonly places: HTMLElement[];
  /** A place's size as the browser last reported it, and the place, for a box like it to hold the page in. */
  size: { readonly width: number; readonly height: number; readonly of: HTMLElement } | undefined;
  /** Hears the place's size while it shows the page. */
  sized: ResizeObserver | undefined;
  /** The box the page is held in between one place and the next. */
  hold: HTMLElement | undefined;
  /** What a move without `moveBefore` loses, read before its first move and put back at the next place. */
  kept: Kept | undefined;
}

/** The element a page is drawn in (`shell.css`), which lays it out as if it stood in its place. */
const PAGE_HOST = 'qube-page-host';

/** The height a page has to lay itself out in (`shell.css`), which a place may set. */
const ROOM = '--qube-panel-room';

export function pagePlaces(doc: Document): PagePlaces {
  const pages = new Map<string, Page>();
  const heard = new Set<() => void>();
  let holds: HTMLElement | null = null;
  let version = 0;
  const changed = () => {
    version += 1;
    heard.forEach((h) => h());
  };
  /** Hears the size of the place that shows the page. */
  const observe = (page: Page, at: HTMLElement) => {
    page.sized?.disconnect();
    page.sized = new ResizeObserver(([entry]) => {
      const box = entry?.borderBoxSize[0];
      if (box !== undefined) page.size = { width: box.inlineSize, height: box.blockSize, of: at };
    });
    page.sized.observe(at);
  };
  const release = (pageId: string, at: HTMLElement) => {
    const page = pages.get(pageId);
    const asked = page === undefined ? -1 : page.places.indexOf(at);
    if (page === undefined || asked < 0) throw new Error(`The page of ${pageId} is let go by a place that did not ask for it.`);
    const shown = asked === page.places.length - 1;
    page.places.splice(asked, 1);
    if (!shown) return;
    page.sized?.disconnect();
    page.sized = undefined;
    // A place dockview has taken into another window with its area (§9) takes the page with it, which is drawn anew there
    // and is not carried: it goes from there once no place here shows it.
    if (at.ownerDocument !== doc) {
      changed();
      return;
    }
    // A place goes before its element leaves the document (React runs a component's cleanups first), and the page is
    // moved out of it while both are in the document: into the place that asked for it before, or into a box of the
    // kind and size of the place it leaves. Where the panels themselves go, their holds have gone first.
    const before = page.places.at(-1);
    if (before !== undefined) {
      moveInto(before, page.node);
      observe(page, before);
    } else if (holds !== null) {
      const hold = doc.createElement('div');
      hold.className = at.className;
      // A place given up before the browser reported its size (two changes in one frame, or React's checks in
      // development, which give a place up as it comes) is read now, while it stands: a box of no size, or of another
      // place's, would leave the page another width to draw in.
      const size = page.size !== undefined && page.size.of === at ? page.size : sizeOf(at);
      hold.style.width = `${size.width}px`;
      hold.style.height = `${size.height}px`;
      // The room a page lays itself out in (ADR-0018 §5), as the place had it: the strip's reads the strip's tabs.
      const view = doc.defaultView;
      if (view === null) throw new Error('A page is held in a document without its window.');
      hold.style.setProperty(ROOM, view.getComputedStyle(at).getPropertyValue(ROOM));
      holds.append(hold);
      if (page.kept === undefined && !movesWhole(hold, page.node)) page.kept = keep(page.node);
      moveInto(hold, page.node);
      page.hold = hold;
    }
    changed();
  };
  return {
    claim: (pageId, at) => {
      // A place in another window shows no page drawn here: the page is drawn in that window (§9).
      if (at.ownerDocument !== doc) return undefined;
      const page = pages.get(pageId);
      if (page === undefined) {
        const node = doc.createElement('div');
        node.className = PAGE_HOST;
        at.append(node);
        const made: Page = { node, places: [at], size: undefined, sized: undefined, hold: undefined, kept: undefined };
        observe(made, at);
        pages.set(pageId, made);
      } else {
        // A place out of the document, which cannot show the page now, asks behind one in it that shows it.
        if (!at.isConnected && page.places.some((p) => p.isConnected)) {
          page.places.unshift(at);
          return () => release(pageId, at);
        }
        // Chromium keeps a page's scrolls across one move whole, but not across two with no layout between: a grid moved
        // into the hold and on into the strip in one step was found at 0. A page held since the last layout is laid out
        // in its hold first, where it stands as it stood in its place, so that nothing in it changes.
        if (page.hold !== undefined && movesWhole(at, page.node)) layOut(page.hold);
        moveInto(at, page.node, page.kept);
        page.kept = undefined;
        page.hold?.remove();
        page.hold = undefined;
        page.places.push(at);
        observe(page, at);
      }
      changed();
      return () => release(pageId, at);
    },
    holds: (element) => { holds = element; },
    subscribe: (h) => {
      heard.add(h);
      return () => { heard.delete(h); };
    },
    version: () => version,
    pages: () => [...pages].map(([pageId, p]) => ({ pageId, node: p.node, placed: p.places.length > 0 })),
    drop: (stays) => {
      for (const [pageId, page] of pages) {
        if (page.places.length > 0 || stays(pageId)) continue;
        page.node.remove();
        page.hold?.remove();
        pages.delete(pageId);
      }
    },
  };
}

/** What a move by `append` loses, put back once the page is in its place: each scroll inside it and the keyboard's. */
interface Kept {
  readonly restore: () => void;
}

/**
 * Whether the browser moves a page whole: `moveBefore` keeps what the page holds in the browser (the keyboard's place,
 * a selection, each scroll) and neither blurs nor focuses anything. Where it has none, a page is appended, which takes
 * the keyboard off it and each scroll to 0: those, read before the first move, are put back at the next place.
 */
function movesWhole(into: HTMLElement, node: HTMLElement): boolean {
  // Only within one document: a page dockview has taken into another window with its area (§9) is appended back, as
  // the page is drawn anew in that window anyway.
  return typeof into.moveBefore === 'function' && node.isConnected && into.isConnected && node.getRootNode() === into.getRootNode();
}

/** The size of a place as it is laid out now. */
function sizeOf(at: HTMLElement): { readonly width: number; readonly height: number } {
  const box = at.getBoundingClientRect();
  return { width: box.width, height: box.height };
}

/** Has the browser lay the document out now, the element with it. */
function layOut(element: HTMLElement): void {
  element.getBoundingClientRect();
}

/** Moves a page into an element, and puts back at its place what a move by `append` lost (`kept`). */
function moveInto(into: HTMLElement, node: HTMLElement, kept?: Kept): void {
  if (node.parentNode === into && kept === undefined) return;
  if (movesWhole(into, node)) {
    into.moveBefore(node, null);
    return;
  }
  // Appended, a field the keyboard is in is left, and taken again as the keyboard is put back: neither is let through to
  // the page, as `moveBefore` makes neither, or a field that commits as it is left would commit what the user had not
  // (Chrome, without `moveBefore`, says the field is left as it is appended). A composition the keyboard was in the
  // middle of is not put back.
  unheard(node.ownerDocument, () => {
    if (node.parentNode !== into) into.append(node);
    kept?.restore();
  });
}

const FOCUS_EVENTS = ['focus', 'blur', 'focusin', 'focusout'] as const;

/** Runs a change with the keyboard's moves it makes heard by nothing in the document, the page's own listeners included. */
function unheard(doc: Document, change: () => void): void {
  const win = doc.defaultView;
  if (win === null) throw new Error('A page is moved in a document without its window.');
  const stop = (event: Event) => event.stopImmediatePropagation();
  FOCUS_EVENTS.forEach((type) => win.addEventListener(type, stop, true));
  try {
    change();
  } finally {
    FOCUS_EVENTS.forEach((type) => win.removeEventListener(type, stop, true));
  }
}

function keep(node: HTMLElement): Kept {
  const scrolls = [...node.querySelectorAll('*')].filter((e) => e.scrollTop !== 0 || e.scrollLeft !== 0).map((e) => ({ e, top: e.scrollTop, left: e.scrollLeft }));
  const active = node.ownerDocument.activeElement;
  const focus = active instanceof HTMLElement && node.contains(active) ? active : undefined;
  const text = focus instanceof HTMLInputElement || focus instanceof HTMLTextAreaElement ? focus : undefined;
  const range = text === undefined || text.selectionStart === null || text.selectionEnd === null || text.selectionDirection === null ? undefined
    : { start: text.selectionStart, end: text.selectionEnd, direction: text.selectionDirection };
  return {
    restore: () => {
      for (const { e, top, left } of scrolls) {
        e.scrollTop = top;
        e.scrollLeft = left;
      }
      if (focus === undefined || !focus.isConnected) return;
      focus.focus({ preventScroll: true });
      if (text !== undefined && range !== undefined) text.setSelectionRange(range.start, range.end, range.direction);
    },
  };
}

/**
 * A page drawn in its place, not moved there: a page in another window, drawn anew in that window (§9). Its element is
 * the one a page of the main window is drawn in, for the same layout.
 */
export function PageInPlace({ children }: { readonly children: ReactNode }) {
  return <div className={PAGE_HOST}>{children}</div>;
}

const Places = createContext<PagePlaces | undefined>(undefined);
export const PagePlacesProvider = Places.Provider;

function usePlaces(): PagePlaces {
  const places = useContext(Places);
  if (places === undefined) throw new Error('A page\'s place is drawn outside the panels.');
  return places;
}

/**
 * A place that shows a panel's page in the main window: the strip's page, or an area's. Its class is the kind of place
 * it is, which a box holding the page after it is given too.
 */
export function PageSlot({ pageId, className }: { readonly pageId: string; readonly className: string }) {
  const places = usePlaces();
  const self = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const place = self.current;
    if (place === null) throw new Error(`The place of ${pageId}'s page is not drawn.`);
    return places.claim(pageId, place);
  }, [places, pageId]);
  return <div ref={self} className={className} />;
}

/**
 * The page carried across a change of how the pages are shown (`shows`, the strip or the areas): the active panel's, the
 * one in view on both sides, from the render that makes the change until its place on the other side takes it
 * (`landed`), or another panel is shown first.
 */
export function useCarried(shows: string | undefined, active: string | undefined): {
  readonly carried: string | undefined;
  readonly landed: () => void;
} {
  const [shown, setShown] = useState(shows);
  const [carried, setCarried] = useState<string | undefined>(undefined);
  if (shown !== shows) {
    setShown(shows);
    setCarried(shown !== undefined && shows !== undefined ? active : undefined);
  } else if (carried !== undefined && carried !== active) setCarried(undefined);
  const landed = useCallback(() => setCarried(undefined), []);
  return { carried, landed };
}

/**
 * The pages of the main window, each drawn by `page` into its own element, which its place holds: those a place shows,
 * and the page `carried` across a change between the strip and the areas until its next place takes it (`landed`).
 */
export function PageHost({ places, carried, landed, page }: {
  readonly places: PagePlaces;
  /** The panel whose page is moved, not drawn anew, as the panels change between the strip and the areas. */
  readonly carried: string | undefined;
  readonly landed: () => void;
  readonly page: (pageId: string) => ReactNode;
}) {
  useSyncExternalStore(places.subscribe, places.version);
  const drawn = places.pages().filter((p) => p.placed || p.pageId === carried);
  useLayoutEffect(() => {
    // A page no longer drawn has left its element empty; one drawn stays until it is not.
    const now = new Set(drawn.map((p) => p.pageId));
    places.drop((pageId) => now.has(pageId));
    if (carried !== undefined && places.pages().some((p) => p.pageId === carried && p.placed)) landed();
  });
  return (
    <>
      <div ref={places.holds} className="qube-page-holds" />
      {drawn.map((p) => createPortal(page(p.pageId), p.node, p.pageId))}
    </>
  );
}
