import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TermCrumb, TraceAddress } from './contracts';
import { useDrillKeys } from './drillKeys';
import { CHILDREN_WAIT_MS } from './keyHolds';
import type { ChildrenRead } from './tree';
import { telemetry } from '../telemetry';

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

const WHERE: TraceAddress = { runId: 'run-1', case: 'after', scenario: 'base', measure: 'ccr.spotCapital' };

/**
 * The list as TraceView draws it below `sm`: a term is read, its path and children with it, when the test says, or its
 * read fails, path and children alike; and the view widened, which draws the tree in the list's place.
 */
function Harness({ readAtOnce, onSelected }: { readonly readAtOnce: boolean; readonly onSelected?: (id: string) => void }) {
  const [selected, setSelected] = useState('capital');
  const [read, setRead] = useState<ReadonlySet<string>>(() => new Set(['capital']));
  const [failed, setFailed] = useState<ReadonlySet<string>>(() => new Set());
  const [wide, setWide] = useState(false);
  const select = (id: string) => {
    setSelected(id);
    onSelected?.(id);
    if (readAtOnce) setRead((r) => new Set(r).add(id));
  };
  const isRead = read.has(selected);
  const children: ChildrenRead = isRead ? { kind: 'pages', pages: [] } : failed.has(selected) ? { kind: 'failed', reason: 'TraceExpired' } : { kind: 'loading' };
  const drill = useDrillKeys({ where: WHERE, selected, path: isRead ? pathOf(selected) : undefined, children, select });
  return (
    <div>
      <button type="button" onClick={() => select('capital')}>Breadcrumb capital</button>
      <button type="button" onClick={() => setRead((r) => new Set(r).add(selected))}>Answer the read</button>
      <button type="button" onClick={() => setFailed((f) => new Set(f).add(selected))}>Fail the read</button>
      <button type="button" onClick={() => setWide(true)}>Widen the view</button>
      {wide ? <span data-testid="tree">The tree</span> : (
        <div ref={drill.frame} tabIndex={-1} onKeyDown={drill.onKeyDown} data-testid="frame">
          <span data-testid="term">{selected}</span>
          {isRead
            ? TREE[selected]!.map((id) => (
              <button type="button" key={id} data-drill-item data-child-id={id} data-child-label={id.toUpperCase()} onClick={() => drill.pick(id, id.toUpperCase())}>{id}</button>
            ))
            : <span>{children.kind === 'failed' ? children.reason : 'Reading its terms…'}</span>}
          {drill.note !== undefined && <span role="status">{drill.note}</span>}
        </div>
      )}
    </div>
  );
}

const keys = (...pressed: string[]) => {
  for (const key of pressed) fireEvent.keyDown(document.activeElement ?? document.body, { key });
};
const focused = () => (document.activeElement as HTMLElement | null)?.dataset.childId ?? (document.activeElement === screen.getByTestId('frame') ? 'frame' : 'elsewhere');

afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

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

  it('gives a read that does not answer up after its bound, says and logs that the keys were not taken, and then takes keys again', () => {
    vi.useFakeTimers();
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => undefined);
    render(<Harness readAtOnce={false} />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    keys('ArrowRight', 'ArrowDown');
    act(() => { vi.advanceTimersByTime(CHILDREN_WAIT_MS - 1); });
    expect(screen.queryByRole('status')).toBeNull();
    act(() => { vi.advanceTimersByTime(1); });
    expect(screen.getByRole('status')).toHaveTextContent('The terms below RWA were not read within 10 seconds, so the keys pressed meanwhile were not taken.');
    expect(report).toHaveBeenCalledTimes(1);
    expect(report.mock.calls[0]?.[0]).toMatchObject({ level: 'warning', kind: 'trace-key' });
    expect(report.mock.calls[0]?.[0].message).toContain('trace term rwa (Run run-1, case after, scenario base, measure ccr.spotCapital)');
    expect(report.mock.calls[0]?.[0].message).toContain('1 key(s) pressed in the drill-in list meanwhile were dropped');
    // The read answering late takes none of the dropped keys.
    fireEvent.click(screen.getByRole('button', { name: 'Answer the read' }));
    expect(screen.getByTestId('term')).toHaveTextContent('rwa');
    screen.getByTestId('frame').focus();
    keys('ArrowDown', 'ArrowRight');
    expect(screen.getByTestId('term')).toHaveTextContent('rw');
  });

  it('lands a move whose read failed at once, the keyboard on the frame beside the reason, rather than waiting out the bound', () => {
    vi.useFakeTimers();
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => undefined);
    render(<Harness readAtOnce={false} />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    keys('ArrowRight', 'ArrowDown', 'ArrowRight');
    fireEvent.click(screen.getByRole('button', { name: 'Fail the read' }));
    expect(screen.getByTestId('term')).toHaveTextContent('rwa');
    expect(screen.getByTestId('frame')).toHaveTextContent('TraceExpired');
    // The keys pressed meanwhile were taken on a list with no child to choose or go into.
    expect(focused()).toBe('frame');
    act(() => { vi.advanceTimersByTime(CHILDREN_WAIT_MS); });
    expect(screen.queryByRole('status')).toBeNull();
    expect(report).not.toHaveBeenCalled();
  });

  it('drops the keys of a move whose list goes, the view widened into the tree, and says nothing of it later', () => {
    vi.useFakeTimers();
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => undefined);
    render(<Harness readAtOnce={false} />);
    screen.getByRole('button', { name: 'rwa' }).focus();

    keys('ArrowRight', 'ArrowDown');
    fireEvent.click(screen.getByRole('button', { name: 'Widen the view' }));
    expect(screen.getByTestId('tree')).toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(CHILDREN_WAIT_MS); });
    expect(report).not.toHaveBeenCalled();
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
