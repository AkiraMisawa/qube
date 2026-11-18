import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TermCrumb } from './contracts';
import { DRILL_WAIT_MS, useDrillKeys } from './drillKeys';
import type { ChildrenRead } from './tree';

// Akira, 2026-11-18: exploring the drill-down with the arrow keys, one press after another. Below `sm` the list of a
// term's children takes them (ADR-0011 §6's note of that day), at machine speed as by hand (spine 6). Synthetic terms.

const TREE: Readonly<Record<string, readonly string[]>> = {
  capital: ['rwa'], rwa: ['rw', 'ead'], rw: ['class'], ead: ['ns1', 'ns2'], class: [], ns1: [], ns2: [],
};
const parentOf = (id: string) => Object.keys(TREE).find((p) => TREE[p]!.includes(id));
const pathOf = (id: string): readonly TermCrumb[] => {
  const above = parentOf(id);
  return [...(above === undefined ? [] : pathOf(above)), { id, label: id.toUpperCase(), symbol: id }];
};

/** The list as TraceView draws it below `sm`: a term is read, its path and children with it, when the test says. */
function Harness({ readAtOnce, onSelected }: { readonly readAtOnce: boolean; readonly onSelected?: (id: string) => void }) {
  const [selected, setSelected] = useState('capital');
  const [read, setRead] = useState<ReadonlySet<string>>(() => new Set(['capital']));
  const select = (id: string) => {
    setSelected(id);
    onSelected?.(id);
    if (readAtOnce) setRead((r) => new Set(r).add(id));
  };
  const isRead = read.has(selected);
  const children: ChildrenRead = isRead ? { kind: 'pages', pages: [] } : { kind: 'loading' };
  const drill = useDrillKeys({ selected, path: isRead ? pathOf(selected) : undefined, children, select });
  return (
    <div>
      <button type="button" onClick={() => select('capital')}>Breadcrumb capital</button>
      <button type="button" onClick={() => setRead((r) => new Set(r).add(selected))}>Answer the read</button>
      <div ref={drill.frame} tabIndex={-1} onKeyDown={drill.onKeyDown} data-testid="frame">
        <span data-testid="term">{selected}</span>
        {isRead
          ? TREE[selected]!.map((id) => (
            <button type="button" key={id} data-drill-item data-child-id={id} data-child-label={id.toUpperCase()} onClick={() => drill.pick(id, id.toUpperCase())}>{id}</button>
          ))
          : <span>Reading its terms…</span>}
        {drill.note !== undefined && <span role="status">{drill.note}</span>}
      </div>
    </div>
  );
}

const keys = (...pressed: string[]) => {
  for (const key of pressed) fireEvent.keyDown(document.activeElement ?? document.body, { key });
};
const focused = () => (document.activeElement as HTMLElement | null)?.dataset.childId ?? (document.activeElement === screen.getByTestId('frame') ? 'frame' : 'elsewhere');

afterEach(() => { vi.useRealTimers(); });

describe('the drill-in list\'s keys (ADR-0011 §6)', () => {
  it('↓ chooses a child, → goes into it with the keyboard on its first child, and ← comes back up to the child left', () => {
    render(<Harness readAtOnce />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    keys('ArrowRight');
    expect(screen.getByTestId('term')).toHaveTextContent('rwa');
    expect(focused()).toBe('rw');
    keys('ArrowDown', 'Enter');
    expect(screen.getByTestId('term')).toHaveTextContent('ead');
    expect(focused()).toBe('ns1');
    keys('Backspace');
    expect(screen.getByTestId('term')).toHaveTextContent('rwa');
    expect(focused()).toBe('ead');
  });

  it('keeps ← on a term with no children: the keyboard stays on the list\'s frame', () => {
    render(<Harness readAtOnce />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    keys('ArrowRight', 'ArrowRight', 'ArrowRight');
    expect(screen.getByTestId('term')).toHaveTextContent('class');
    expect(focused()).toBe('frame');
    keys('ArrowLeft');
    expect(screen.getByTestId('term')).toHaveTextContent('rw');
    expect(focused()).toBe('class');
  });

  it('takes the keys pressed while a term is read in their order once its list is drawn, as if pressed after it', () => {
    const selectedInTurn: string[] = [];
    render(<Harness readAtOnce={false} onSelected={(id) => selectedInTurn.push(id)} />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    // Into rwa, then ↓ and → pressed before rwa's children are read: they wait for its list.
    keys('ArrowRight', 'ArrowDown', 'ArrowRight');
    expect(screen.getByTestId('term')).toHaveTextContent('rwa');
    expect(focused()).toBe('frame');
    fireEvent.click(screen.getByRole('button', { name: 'Answer the read' }));
    // The read was answered by a press outside the frame; the keys still went where they were pressed for.
    expect(selectedInTurn).toEqual(['rwa', 'ead']);
    expect(screen.getByTestId('term')).toHaveTextContent('ead');
  });

  it('drops the keys pressed for a list that another move replaced, rather than taking them on the new one', () => {
    render(<Harness readAtOnce={false} />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    keys('ArrowRight', 'ArrowDown', 'ArrowRight');
    fireEvent.click(screen.getByRole('button', { name: 'Breadcrumb capital' }));
    expect(screen.getByTestId('term')).toHaveTextContent('capital');
    fireEvent.click(screen.getByRole('button', { name: 'Answer the read' }));
    expect(screen.getByTestId('term')).toHaveTextContent('capital');
  });

  it('gives a read that does not answer up after its bound, says the keys were not taken, and then takes keys again', () => {
    vi.useFakeTimers();
    render(<Harness readAtOnce={false} />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    keys('ArrowRight', 'ArrowDown');
    act(() => { vi.advanceTimersByTime(DRILL_WAIT_MS); });
    expect(screen.getByRole('status')).toHaveTextContent('The terms below RWA are still being read, so the keys pressed meanwhile were not taken.');
    // The read answering late takes none of the dropped keys.
    fireEvent.click(screen.getByRole('button', { name: 'Answer the read' }));
    expect(screen.getByTestId('term')).toHaveTextContent('rwa');
    screen.getByTestId('frame').focus();
    keys('ArrowDown', 'ArrowRight');
    expect(screen.getByTestId('term')).toHaveTextContent('rw');
  });

  it('leaves the keys of anything else in the frame alone, and keys pressed with a modifier', () => {
    render(<Harness readAtOnce />);
    const item = screen.getByRole('button', { name: 'rwa' });
    item.focus();

    fireEvent.keyDown(item, { key: 'ArrowRight', altKey: true });
    fireEvent.keyDown(screen.getByTestId('term'), { key: 'ArrowRight' });
    expect(screen.getByTestId('term')).toHaveTextContent('capital');
  });
});
