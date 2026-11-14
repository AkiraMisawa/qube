// What an event's target is, read so that it holds in every window of the browser: a page drawn in another window
// (ADR-0018 §9) makes its nodes in that window, whose `Element` is not the main window's, so `instanceof Element` holds
// only for the main window's nodes.

/** Whether a target is an element, of whichever window made it. */
export function isElement(target: unknown): target is Element {
  return typeof target === 'object' && target !== null && 'nodeType' in target && target.nodeType === 1;
}

/** Whether a target is an HTML element, of whichever window made it. */
export function isHTMLElement(target: unknown): target is HTMLElement {
  return isElement(target) && target.namespaceURI === 'http://www.w3.org/1999/xhtml';
}
