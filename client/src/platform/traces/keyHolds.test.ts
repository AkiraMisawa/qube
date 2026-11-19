import { afterEach, describe, expect, it, vi } from 'vitest';
import { CHILDREN_WAIT_MS, KeyHolds, reportChildrenNotRead } from './keyHolds';
import { telemetry } from '../telemetry';

// ADR-0011 §6's note of 2026-11-18: a key that waits for a term's children holds the keys after it, and is given up past
// a bound, for a server that does not answer. Synthetic terms.

afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

describe('the trace\'s keys held for a term\'s children (ADR-0011 §6)', () => {
  it('lets a key go once its children are there, in the order held, and never gives it up after', async () => {
    vi.useFakeTimers();
    const holds = new KeyHolds<string>();
    const giveUp = vi.fn();
    const letGo: string[] = [];
    const rwa = holds.hold('rwa', giveUp).then(() => letGo.push('rwa'));
    const ead = holds.hold('ead', giveUp).then(() => letGo.push('ead'));
    expect(holds.size).toBe(2);

    holds.release((id) => id === 'ead');
    await ead;
    expect(letGo).toEqual(['ead']);
    holds.release(() => true);
    await rwa;
    expect(letGo).toEqual(['ead', 'rwa']);
    vi.advanceTimersByTime(CHILDREN_WAIT_MS);
    expect(giveUp).not.toHaveBeenCalled();
    expect(holds.size).toBe(0);
  });

  it('gives a key up once the bound has passed, saying which, and lets the keys after it go', async () => {
    vi.useFakeTimers();
    const holds = new KeyHolds<string>();
    const giveUp = vi.fn();
    const held = holds.hold('rwa', giveUp);

    vi.advanceTimersByTime(CHILDREN_WAIT_MS - 1);
    expect(giveUp).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(giveUp).toHaveBeenCalledWith('rwa');
    await held;
    expect(holds.size).toBe(0);
    // Their children arriving late lets nothing go twice.
    holds.release(() => true);
    expect(giveUp).toHaveBeenCalledTimes(1);
  });

  it('lets every key go, giving none up, once what held them is gone', async () => {
    vi.useFakeTimers();
    const holds = new KeyHolds<string>();
    const giveUp = vi.fn();
    const held = Promise.all([holds.hold('rwa', giveUp), holds.hold('ead', giveUp)]);
    holds.releaseAll();
    await held;
    vi.advanceTimersByTime(CHILDREN_WAIT_MS);
    expect(giveUp).not.toHaveBeenCalled();
  });

  it('logs a wait given up with the Run, case, scenario and measure that find its trace', () => {
    const report = vi.spyOn(telemetry, 'report').mockImplementation(() => undefined);
    reportChildrenNotRead({ runId: 'run-1', case: 'after', scenario: 'base', measure: 'ccr.spotCapital' }, 'rwa', 'the keys pressed meanwhile were not taken');
    expect(report).toHaveBeenCalledWith({
      level: 'warning', kind: 'trace-key',
      message: `The children of trace term rwa (Run run-1, case after, scenario base, measure ccr.spotCapital) were not read within ${CHILDREN_WAIT_MS} ms; the keys pressed meanwhile were not taken.`,
    });
  });
});
