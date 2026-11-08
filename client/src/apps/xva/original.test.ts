import { describe, expect, it } from 'vitest';
import { changes, differs, originalMessage } from './original';

// ADR-0010 §2: the page marks what the server says differs from Original, and says it in words.

describe('what differs from Original', () => {
  it('marks only the fields the server names, an empty Original included', () => {
    const row = { original: { curve: 'jpy_ois', fixedRate: null } };

    expect(differs(row, 'curve')).toBe(true);
    expect(differs(row, 'fixedRate')).toBe(true);
    expect(differs(row, 'notional')).toBe(false);
    expect(differs({}, 'curve')).toBe(false);
  });

  it('describes a marked cell by Original and the value now, an empty one as empty, never 0', () => {
    expect(originalMessage({ original: { curve: 'jpy_ois' } }, 'curve', 'usd_sofr')).toBe('Original jpy_ois · now usd_sofr');
    expect(originalMessage({ original: { cvaRecovery: 0.35 } }, 'cvaRecovery', null)).toBe('Original 0.35 · now empty');
    expect(originalMessage({ original: { notional: 1_800_000_000 } }, 'notional', 9_000_000_000)).toBe('Original 1,800,000,000 · now 9,000,000,000');
    expect(originalMessage({}, 'curve', 'usd_sofr')).toBeUndefined();
  });

  it('counts the changed values of a tab', () => {
    expect(changes([{ original: { curve: 'a', frequency: 'daily' } }, {}, { original: null }])).toBe(2);
  });
});
