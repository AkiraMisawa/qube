import { useState } from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import '../test/ui';
import { PageHost, pagePlaces, PagePlacesProvider, PageSlot, useCarried } from './pageHost';

// ADR-0018 §5's note of 2026-11-15: the page in view as the panels change between the strip and the areas is moved, not
// drawn anew. What a browser alone shows (a grid's scroll, a field that commits as it is left, `moveBefore`) is layer
// 3's (areas.spec.ts); here, which page is the same page and which is drawn anew. jsdom has no `moveBefore`, so a page
// is moved here as in a browser without it.

/** A page with something typed in it and not committed: its own state, which only a page not drawn anew keeps. */
function Draft({ id }: { readonly id: string }) {
  const [text, setText] = useState('');
  return <input aria-label={`Draft of ${id}`} value={text} onChange={(e) => setText(e.currentTarget.value)} />;
}

/**
 * The panels as `Panels` draws them: the host, and the place that shows the active panel's page, the strip's or an
 * area's; an area's is `drawn` a moment after the areas are made, as dockview draws it.
 */
function Harness({ shows, active = 'p1', drawn = true, beside = [] }: {
  readonly shows: 'strip' | 'areas';
  readonly active?: string;
  readonly drawn?: boolean;
  /** The panels shown in the other areas, beside the active one's. */
  readonly beside?: readonly string[];
}) {
  const [places] = useState(() => pagePlaces(document));
  const { carried, landed } = useCarried(shows, active);
  return (
    <PagePlacesProvider value={places}>
      <PageHost places={places} carried={carried} landed={landed} page={(id) => <Draft id={id} />} />
      {drawn && shows === 'areas' && <section aria-label="Area"><PageSlot key={active} pageId={active} className="qube-area-page" /></section>}
      {drawn && shows === 'areas' && beside.map((id) => <section key={id} aria-label={`Area of ${id}`}><PageSlot pageId={id} className="qube-area-page" /></section>)}
      {drawn && shows === 'strip' && <section aria-label="Strip"><PageSlot key={active} pageId={active} className="qube-strip-place" /></section>}
    </PagePlacesProvider>
  );
}

const draft = (id = 'p1') => screen.getByRole('textbox', { name: `Draft of ${id}` });

function typeIn(text: string): HTMLInputElement {
  const field = draft();
  if (!(field instanceof HTMLInputElement)) throw new Error('The draft is not a field.');
  fireEvent.change(field, { target: { value: text } });
  return field;
}

describe('a page moved between the strip and the areas (ADR-0018 §5\'s note of 2026-11-15)', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  it('is the same page in its next place, with what was typed in it, where it is carried across', () => {
    const { rerender } = render(<Harness shows="areas" />);
    const typed = typeIn('Typed not committed');
    rerender(<Harness shows="strip" />);
    expect(draft()).toBe(typed);
    expect(draft()).toHaveValue('Typed not committed');
    expect(screen.getByRole('region', { name: 'Strip' })).toContainElement(typed);
  });

  it('waits out of sight, in a box of the place it left, until its next place comes (the areas, drawn a moment after)', () => {
    const { rerender } = render(<Harness shows="strip" />);
    const typed = typeIn('Typed in the strip');
    rerender(<Harness shows="areas" drawn={false} />);
    const hold = typed.closest('.qube-page-holds > *');
    expect(hold).not.toBeNull();
    expect(hold).toHaveClass('qube-strip-place');
    rerender(<Harness shows="areas" />);
    expect(draft()).toBe(typed);
    expect(draft()).toHaveValue('Typed in the strip');
    expect(screen.getByRole('region', { name: 'Area' })).toContainElement(typed);
    // The box it waited in goes with the wait.
    expect(document.querySelector('.qube-page-holds')?.childElementCount).toBe(0);
  });

  it('is drawn anew where it is hidden and shown again, as a tab chosen in the strip: only the pages in view are mounted', () => {
    const { rerender } = render(<Harness shows="strip" />);
    const typed = typeIn('Typed, then hidden');
    rerender(<Harness shows="strip" active="p2" />);
    expect(draft('p2')).toHaveValue('');
    expect(screen.queryByRole('textbox', { name: 'Draft of p1' })).toBeNull();
    expect(document.querySelectorAll('.qube-page-host')).toHaveLength(1);
    rerender(<Harness shows="strip" />);
    expect(draft()).not.toBe(typed);
    expect(draft()).toHaveValue('');
  });

  it('is the one page carried where other areas show other pages: theirs go as the strip, which does not show them, is drawn', () => {
    const { rerender } = render(<Harness shows="areas" beside={['p2']} />);
    typeIn('Typed in the area in view');
    fireEvent.change(draft('p2'), { target: { value: 'Typed in the area beside' } });
    rerender(<Harness shows="strip" />);
    expect(draft()).toHaveValue('Typed in the area in view');
    expect(screen.queryByRole('textbox', { name: 'Draft of p2' })).toBeNull();
    expect(document.querySelectorAll('.qube-page-host')).toHaveLength(1);
    expect(document.querySelector('.qube-page-holds')?.childElementCount).toBe(0);
    rerender(<Harness shows="areas" beside={['p2']} />);
    expect(draft()).toHaveValue('Typed in the area in view');
    expect(draft('p2')).toHaveValue('');
  });

  it('is not carried where another panel is shown before its place comes', () => {
    const { rerender } = render(<Harness shows="strip" />);
    typeIn('Typed in the strip');
    rerender(<Harness shows="areas" drawn={false} />);
    rerender(<Harness shows="areas" active="p2" drawn={false} />);
    rerender(<Harness shows="areas" active="p2" />);
    expect(screen.queryByRole('textbox', { name: 'Draft of p1' })).toBeNull();
    expect(document.querySelectorAll('.qube-page-host')).toHaveLength(1);
  });

  it('keeps the keyboard where it was where the browser has no moveBefore, as moveBefore would', () => {
    expect('moveBefore' in Element.prototype).toBe(false);
    const { rerender } = render(<Harness shows="strip" />);
    const typed = typeIn('Typed with the keyboard in it');
    act(() => {
      typed.focus();
      typed.setSelectionRange(3, 8);
    });
    rerender(<Harness shows="areas" drawn={false} />);
    rerender(<Harness shows="areas" />);
    expect(draft()).toBe(typed);
    expect(document.activeElement).toBe(typed);
    expect([typed.selectionStart, typed.selectionEnd]).toEqual([3, 8]);
  });

  it('is shown in the last place that asks for it, and goes back to the one before as that lets go', () => {
    const places = pagePlaces(document);
    const [first, second, holds] = [document.createElement('div'), document.createElement('div'), document.createElement('div')];
    document.body.append(first, second, holds);
    places.holds(holds);
    const letGo = places.claim('p1', first);
    const page = first.firstElementChild;
    const letGoSecond = places.claim('p1', second);
    expect(second.firstElementChild).toBe(page);
    letGoSecond?.();
    expect(first.firstElementChild).toBe(page);
    letGo?.();
    expect(holds.querySelector('.qube-page-host')).toBe(page);
  });

  it('stays in the place in the document where one out of it asks after, as dockview\'s area that drew it before as a panel comes back from another window, and goes there as the one in view lets go', () => {
    const places = pagePlaces(document);
    const [shown, out, holds] = [document.createElement('div'), document.createElement('div'), document.createElement('div')];
    document.body.append(shown, holds);
    places.holds(holds);
    const letGo = places.claim('p1', shown);
    const page = shown.firstElementChild;
    places.claim('p1', out);
    expect(shown.firstElementChild).toBe(page);
    expect(out.childElementCount).toBe(0);
    // dockview takes the area out of the document as another panel is chosen there, and shows the other again later.
    shown.remove();
    letGo?.();
    expect(out.firstElementChild).toBe(page);
  });

  it('is not shown in a place in another window: that window draws the page itself', () => {
    const places = pagePlaces(document);
    const other = document.implementation.createHTMLDocument('another window');
    const place = other.createElement('div');
    other.body.append(place);
    expect(places.claim('p1', place)).toBeUndefined();
    expect(place.childElementCount).toBe(0);
    expect(places.pages()).toEqual([]);
  });
});
