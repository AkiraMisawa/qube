import { useEffect, useMemo, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { MantineThemeProvider, type MantineThemeOverride } from '@mantine/core';
import { NoticeStrip, useNotice } from './Notice';
import { hearUse, isElement, isHTMLElement, portalOf, useOtherWindows, useWindowInUse, type OtherWindow } from './windows';

// ADR-0018 §9: another window of the browser is the main one's. Its shell is drawn by the areas into its document
// while it is open: it takes the main window's look and keeps it, is titled by the panel it shows, says the shell's
// notices while it is the window in use, and tells the main document what Mantine listens for there.

/** The portals of what is drawn here open in `win`: its menus, selects, tooltips and popovers (ADR-0018 §9). */
export function WindowPortals({ win, children }: { readonly win: Window; readonly children: ReactNode }) {
  // Read again as windows open and close: a page drawn as its window opens is drawn before the window is added.
  useOtherWindows();
  const portal = portalOf(win);
  const theme = useMemo<MantineThemeOverride>(
    () => (portal === undefined ? MAIN : { components: { Portal: { defaultProps: { target: portal } } } }), [portal]);
  // The same element in either window, so that what it holds is not drawn anew as the window in use changes: the line
  // opened by a key in another window is opened as that window becomes the one in use.
  return <MantineThemeProvider theme={theme}>{children}</MantineThemeProvider>;
}

/** The main window's portals are Mantine's own, in the main document's body. */
const MAIN: MantineThemeOverride = {};

/** The shell's dialogs and the command line open in the window in use (ADR-0018 §9's note of 2026-11-13). */
export function InUseWindowPortals({ children }: { readonly children: ReactNode }) {
  return <WindowPortals win={useWindowInUse()}>{children}</WindowPortals>;
}

/** The shell of another window: drawn into its body, beside the area dockview put there. */
export function WindowShell({ other, title }: { readonly other: OtherWindow; readonly title: string | undefined }) {
  const { window: win, portal } = other;
  useEffect(() => followLook(win.document), [win]);
  useEffect(() => tellMain(win, portal), [win, portal]);
  useEffect(() => hearUse(win), [win]);
  useEffect(() => { win.document.title = title === undefined ? document.title : `${title} · ${document.title}`; }, [win, title]);
  const { notice, clearNotice } = useNotice();
  const inUse = useWindowInUse() === win;
  return createPortal(
    <WindowPortals win={win}>
      {inUse && <NoticeStrip notice={notice} onClose={clearNotice} inAnotherWindow />}
    </WindowPortals>,
    win.document.body,
  );
}

/**
 * Keeps another window's look the main window's: the attributes of its `<html>` (the colour scheme among them) and the
 * stylesheets the main head gains after the window opened, which dockview did not copy as it opened it, and takes back
 * one the main head loses. Answers what stops following.
 */
function followLook(doc: Document): () => void {
  const main = document.documentElement;
  const attributes = () => {
    for (const { name } of [...doc.documentElement.attributes]) if (!main.hasAttribute(name)) doc.documentElement.removeAttribute(name);
    for (const { name, value } of [...main.attributes]) if (doc.documentElement.getAttribute(name) !== value) doc.documentElement.setAttribute(name, value);
  };
  attributes();
  const copies = new Map<Node, Node>();
  const sheets = (records: readonly MutationRecord[]) => {
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (!isElement(node) || !node.matches('style, link[rel="stylesheet"]')) continue;
        const copy = node.cloneNode(true);
        doc.head.append(copy);
        copies.set(node, copy);
      }
      for (const node of record.removedNodes) {
        const copy = copies.get(node);
        if (copy === undefined) continue;
        copy.parentNode?.removeChild(copy);
        copies.delete(node);
      }
    }
  };
  const html = new MutationObserver(attributes);
  html.observe(main, { attributes: true });
  const head = new MutationObserver(sheets);
  head.observe(document.head, { childList: true });
  return () => {
    html.disconnect();
    head.disconnect();
  };
}

/** What Tab stops at in a dialog, as Mantine's focus trap reads it. */
const TAB_STOPS = 'a[href], input, select, textarea, button, object, summary, iframe, audio[controls], video[controls], [contenteditable]:not([contenteditable="false"]), [tabindex]';

/**
 * Tells the main document what Mantine listens for there, from another window (ADR-0018 §9's note of 2026-11-13): a
 * press outside that window's popovers, which closes an open menu or select as a press outside it does in the main
 * window, and Escape, which a dialog's close listens for. Each is told as a copy of the event on the main document's
 * body; a copy whose default was prevented prevents the original's. Tab is kept inside a dialog open in the window here:
 * the focus trap that would keep it hears the main document, and walks up from what it traps to the main body, which a
 * dialog in another window is not under. Answers what stops it.
 */
function tellMain(win: Window, portal: HTMLElement): () => void {
  const doc = win.document;
  const key = (event: KeyboardEvent) => {
    const copy = new KeyboardEvent('keydown', {
      key: event.key, code: event.code, shiftKey: event.shiftKey, ctrlKey: event.ctrlKey, altKey: event.altKey, metaKey: event.metaKey,
      isComposing: event.isComposing, bubbles: true, cancelable: true,
    });
    document.body.dispatchEvent(copy);
    if (copy.defaultPrevented) event.preventDefault();
  };
  // Escape before anything in the window takes it, as Mantine's dialogs hear it on the main window in the capture
  // phase; not one a popover inside the dialog keeps for itself.
  const onEscape = (event: KeyboardEvent) => {
    if (event.key !== 'Escape' || (isElement(event.target) && event.target.getAttribute('data-mantine-stop-propagation') === 'true')) return;
    key(event);
  };
  // Tab once the window has had it, as a focus trap hears it: within a dialog it goes to the next stop, around the ends.
  const onTab = (event: KeyboardEvent) => {
    if (event.key !== 'Tab' || event.defaultPrevented) return;
    const at = doc.activeElement;
    const dialog = [...doc.querySelectorAll('[role="dialog"][aria-modal="true"]')].find((d) => d.contains(at));
    if (dialog === undefined || !isHTMLElement(at)) return;
    const stops = [...dialog.querySelectorAll(TAB_STOPS)].filter(isHTMLElement)
      .filter((e) => e.tabIndex >= 0 && !e.matches(':disabled') && e.checkVisibility());
    if (stops.length === 0) return;
    event.preventDefault();
    const from = stops.indexOf(at);
    const step = event.shiftKey ? -1 : 1;
    const to = from < 0 ? (event.shiftKey ? stops.length - 1 : 0) : (from + step + stops.length) % stops.length;
    stops[to]?.focus();
  };
  // A press on what holds a popover open, or inside one, is that popover's own.
  const onPress = (event: Event) => {
    const path = event.composedPath();
    if (path.includes(portal) || path.some((n) => isElement(n) && n.getAttribute('aria-expanded') === 'true')) return;
    document.body.dispatchEvent(event.type === 'mousedown' ? new MouseEvent('mousedown', { bubbles: true }) : new Event(event.type, { bubbles: true }));
  };
  doc.addEventListener('keydown', onEscape, true);
  doc.addEventListener('keydown', onTab);
  doc.addEventListener('mousedown', onPress, true);
  doc.addEventListener('touchstart', onPress, true);
  return () => {
    doc.removeEventListener('keydown', onEscape, true);
    doc.removeEventListener('keydown', onTab);
    doc.removeEventListener('mousedown', onPress, true);
    doc.removeEventListener('touchstart', onPress, true);
  };
}
