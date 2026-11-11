import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api';
import { EditQueue } from './editQueue';

const view = (revision: number) => ({ revision });

describe('EditQueue follows the revision on screen (ADR-0003, spine 6)', () => {
  it('chains the next edit on a revision someone else moved, once the page shows it', async () => {
    // The AI created a line (revision 5 to 6) and the page read the workspace again.
    const sent: number[] = [];
    const queue = new EditQueue<string, { revision: number }>(5, async (revision) => {
      if (revision !== 6) throw new ApiError(409, { code: 'RevisionMoved', message: 'moved', issues: [] });
      sent.push(revision);
      return view(revision + 1);
    }, () => {}, () => { throw new Error('refused'); });

    queue.shown(6);
    await queue.push('L1 notional');

    expect(sent).toEqual([6]);
    expect(queue.revision).toBe(7);
  });

  it('ignores a read while an edit of its own is waiting, and a read older than its own answer', async () => {
    let answer: ((v: { revision: number }) => void) | undefined;
    const sent: number[] = [];
    const queue = new EditQueue<string, { revision: number }>(5, (revision) => {
      sent.push(revision);
      return new Promise((resolve) => { answer = resolve; });
    }, () => {}, () => {});

    const first = queue.push('L1 notional');
    await Promise.resolve();
    queue.shown(9);
    expect(queue.revision).toBe(5);
    answer!(view(6));
    await first;

    queue.shown(5);
    expect(queue.revision).toBe(6);
    expect(sent).toEqual([5]);
  });

  it('runs a command after the edits sent before it, on the revision they leave, and holds the edits after it (spine 6)', async () => {
    const answers: ((v: { revision: number }) => void)[] = [];
    const sent: string[] = [];
    const queue = new EditQueue<string, { revision: number }>(5, (revision, edit) => {
      sent.push(`${edit}@${revision}`);
      return new Promise((resolve) => { answers.push(resolve); });
    }, () => {}, () => {});

    const edit = queue.push('measures');
    const price = queue.after(async (revision) => { sent.push(`price@${revision}`); return `run of ${revision}`; });
    const later = queue.push('margin');
    await Promise.resolve();
    expect(sent).toEqual(['measures@5']);

    answers[0]!(view(6));
    await edit;
    expect(await price).toBe('run of 6');
    await vi.waitFor(() => expect(sent).toEqual(['measures@5', 'price@6', 'margin@6']));
    answers[1]!(view(7));
    await later;
  });

  it('refuses a command after a refused edit, as it refuses the edits after it', async () => {
    const queue = new EditQueue<string, { revision: number }>(5, () =>
      Promise.reject(new ApiError(409, { code: 'RevisionMoved', message: 'moved', issues: [] })), () => {}, () => {});

    void queue.push('notional');
    await expect(queue.after(async () => 'priced')).rejects.toThrow('An earlier edit was refused');
  });

  it('is settled once the edits sent so far are answered, and waits for none sent later (ADR-0016 §5)', async () => {
    const answers: ((v: { revision: number }) => void)[] = [];
    const queue = new EditQueue<string, { revision: number }>(5, () => new Promise((resolve) => { answers.push(resolve); }), () => {}, () => {});
    let settled = false;

    const edit = queue.push('notional');
    void queue.settled().then(() => { settled = true; });
    void queue.push('margin');
    await Promise.resolve();
    expect(settled).toBe(false);

    answers[0]!(view(6));
    await edit;
    expect(settled).toBe(true);
    // The edit sent after the reading was asked for is on its way, and was not waited for.
    await vi.waitFor(() => expect(answers).toHaveLength(2));
  });
});
