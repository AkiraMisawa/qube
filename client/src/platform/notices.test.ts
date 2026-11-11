import { describe, expect, it } from 'vitest';
import { leavesByItself, type Notice } from './notices';

// ADR-0006's note 2 of 2026-11-11: which notice leaves by itself, and what holds one that would.
describe('ADR-0006 note 2 (2026-11-11): a notice that leaves by itself', () => {
  const free = { pointer: false, keyboard: false };
  const done: Notice = { kind: 'done', message: 'Set CSA-A threshold to 5mm.' };

  it('is a done one that nothing holds', () => {
    expect(leavesByItself(done, free)).toBe(true);
  });

  it('is held by the pointer and by the keyboard, each by itself', () => {
    expect(leavesByItself(done, { pointer: true, keyboard: false })).toBe(false);
    expect(leavesByItself(done, { pointer: false, keyboard: true })).toBe(false);
  });

  it('is never a refusal', () => {
    expect(leavesByItself({ kind: 'refused', message: 'set changed nothing: -1mm is not an amount of 0 or more.' }, free)).toBe(false);
  });

  it('is not a done one that leaves something to decide', () => {
    expect(leavesByItself({ ...done, message: 'As of 2026-10-28: 1 edit(s) carried, 1 in conflict, 0 lost.', stays: true }, free)).toBe(false);
  });
});
