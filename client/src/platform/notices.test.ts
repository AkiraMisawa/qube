import { describe, expect, it, vi } from 'vitest';
import { hearing, leavesByItself, type Notice } from './notices';

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

// ADR-0016 §9, note 24 of 2026-11-11: the command line says what a chained command said, by hearing the notices.
describe('ADR-0016 note 24 (2026-11-11): what is notified can be heard', () => {
  it('is said as before, and told to whoever hears, until they stop hearing', () => {
    const said = vi.fn();
    const heard: Notice[] = [];
    const notices = hearing(said);

    const stop = notices.hear((notice) => heard.push(notice));
    notices.notify('done', 'Set 3 trades after to off.');
    notices.notify('done', '2 edits carried, 1 in conflict.', { stays: true });
    stop();
    notices.notify('refused', 'Nothing was asked again.');

    expect(said.mock.calls).toEqual([['done', 'Set 3 trades after to off.', undefined], ['done', '2 edits carried, 1 in conflict.', { stays: true }], ['refused', 'Nothing was asked again.', undefined]]);
    expect(heard).toEqual([{ kind: 'done', message: 'Set 3 trades after to off.' }, { kind: 'done', message: '2 edits carried, 1 in conflict.', stays: true }]);
  });
});
