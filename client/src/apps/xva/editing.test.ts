import { ApiError } from '../../platform/api';
import type { XvaWorkspaceView } from './contracts';
import { EditQueue, csaFields, parseNumber, parseRecovery, refusedEdit, sideSaid, tradeFields, type PendingEdit } from './editing';

const view = (revision: number) => ({ revision }) as unknown as XvaWorkspaceView;

describe('what a cell accepts', () => {
  it('reads a number only one way', () => {
    expect(parseNumber('1,000,000')).toBe(1_000_000);
    expect(parseNumber('-0.0125')).toBe(-0.0125);
    expect(parseNumber('1,00')).toBeUndefined();
    expect(parseNumber('1.000,5')).toBeUndefined();
    expect(parseNumber('1e6')).toBeUndefined();
  });

  it('keeps an empty recovery and an empty fixed rate empty, never zero', () => {
    expect(parseRecovery('')).toEqual({ ok: true, value: null });
    expect(tradeFields.fixedRate!('')).toEqual({ ok: true, value: null });
    expect(parseRecovery('1').ok).toBe(false);
  });

  it('refuses a zero notional and a schedule it does not know', () => {
    expect(tradeFields.notional!('0').ok).toBe(false);
    expect(tradeFields.schedule!('1Y').ok).toBe(false);
    expect(tradeFields.after!('false')).toEqual({ ok: true, value: false });
  });

  it('ADR-0006 note 4 (2026-11-11): reads a flag as `set` reads it, in a cell too, and refuses a word that is none', () => {
    for (const on of ['on', 'yes', 'true', 'y', 'ON', 'Yes', 'TRUE']) expect(tradeFields.before!(on), on).toEqual({ ok: true, value: true });
    for (const off of ['off', 'no', 'false', 'n', 'OFF', 'No', 'FALSE']) expect(tradeFields.after!(off), off).toEqual({ ok: true, value: false });
    expect(csaFields.oneWay!(' off ')).toEqual({ ok: true, value: false });
    for (const none of ['maybe', '1', '0', '', 'onn']) expect(tradeFields.before!(none), none).toEqual({ ok: false, message: 'Type on or off.' });
  });
});

describe('what a CSA term accepts (ADR-0010 §8)', () => {
  it('reads amounts of 0 or more, currency codes and the three frequencies', () => {
    expect(csaFields.counterpartyThreshold!('200,000,000')).toEqual({ ok: true, value: 200_000_000 });
    expect(csaFields.selfMta!('-1').ok).toBe(false);
    expect(csaFields.selfCurrency!('gbp')).toEqual({ ok: true, value: 'GBP' });
    expect(csaFields.counterpartyCurrency!('pounds').ok).toBe(false);
    expect(csaFields.frequency!('Weekly')).toEqual({ ok: true, value: 'weekly' });
    expect(csaFields.frequency!('hourly').ok).toBe(false);
    expect(csaFields.oneWay!('TRUE')).toEqual({ ok: true, value: true });
  });

  it('has no field for the type, which is the source\'s', () => {
    expect(csaFields.type).toBeUndefined();
  });
});

describe('how a refused edit is said (ADR-0006 note 2 of 2026-11-11)', () => {
  it('names the row and the column in words, never an entity\'s key', () => {
    expect(refusedEdit({ entity: 'trade:ABC-LE-01-E00006', field: 'fixedRate' }, 'Type a rate.')).toBe('ABC-LE-01-E00006 fixed rate was not changed: Type a rate.');
    expect(refusedEdit({ entity: 'csa:CSA-A', field: 'counterpartyThreshold' }, 'No.')).toBe('CSA-A counterparty threshold was not changed: No.');
    // A module's column is named without its module (ADR-0010 §10).
    expect(refusedEdit({ entity: 'trade:DRAFT-1', field: 'xva.margin' }, 'No.')).toBe('DRAFT-1 margin was not changed: No.');
  });

  it('names the measures, the display currency and a grid\'s side as the page does', () => {
    expect(refusedEdit({ entity: 'selection:measures', field: 'measures' }, 'Choose one.')).toBe('The measures were not changed: Choose one.');
    expect(refusedEdit({ entity: 'settings:workspace', field: 'displayCurrency' }, 'XYZ is not offered.')).toBe('The display currency was not changed: XYZ is not offered.');
    expect(refusedEdit({ entity: 'grid:existing', field: 'before' }, 'No.')).toBe('Before was not changed on the existing trades: No.');
    expect(refusedEdit({ entity: 'grid:new', field: 'after' }, 'No.')).toBe('After was not changed on the new trades: No.');
  });
});

describe('what `before` and `after` say back (ADR-0006 note 2 of 2026-11-11)', () => {
  it('says the side, what was done to it and on how many trades of which grid', () => {
    expect(sideSaid('before', false, 'existing', 215)).toBe('Cleared Before on 215 existing trades.');
    expect(sideSaid('after', true, 'new', 3)).toBe('Ticked After on 3 new trades.');
    expect(sideSaid('after', false, 'new', 1)).toBe('Cleared After on 1 new trade.');
  });
});

describe('EditQueue (ADR-0003, spine 6)', () => {
  it("chains this session's edits on the revision each produced", async () => {
    const sent: number[] = [];
    const queue = new EditQueue<PendingEdit, XvaWorkspaceView>(2, async (revision) => { sent.push(revision); return view(revision + 1); }, () => {}, () => {});

    await Promise.all([queue.push({ entity: 'trade:A', field: 'notional', value: 1 }), queue.push({ entity: 'trade:B', field: 'notional', value: 2 })]);

    expect(sent).toEqual([2, 3]);
    expect(queue.revision).toBe(4);
  });

  it('drops what was queued behind a revision moved elsewhere, until the workspace is read again', async () => {
    const refused: PendingEdit[] = [];
    const queue = new EditQueue<PendingEdit, XvaWorkspaceView>(5, async (revision) => {
      if (revision === 5) throw new ApiError(409, { code: 'RevisionMoved', message: 'moved', issues: [] });
      return view(revision + 1);
    }, () => {}, (edit) => refused.push(edit));

    await queue.push({ entity: 'trade:A', field: 'csa', value: 'CSA-B' });
    await queue.push({ entity: 'trade:B', field: 'csa', value: 'CSA-B' });
    queue.rebase(7);
    await queue.push({ entity: 'trade:C', field: 'csa', value: 'CSA-B' });

    expect(refused.map((e) => e.entity)).toEqual(['trade:A', 'trade:B']);
    expect(queue.revision).toBe(8);
  });
});
