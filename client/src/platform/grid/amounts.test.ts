import { amountTone, formatAmount, isLoss, toneOf } from './amounts';

// C48: an amount of money is grouped in threes, in whole units (DEFAULT), and a negative one is painted as a loss.
describe('amounts', () => {
  it('groups an amount in threes, in whole units, and paints a loss red', () => {
    expect(formatAmount(-1234567.89)).toBe('-1,234,568');
    expect(amountTone(-1234567.89)).toBe('negative');
    expect(formatAmount(98765.4)).toBe('98,765');
    expect(amountTone(98765.4)).toBe('none');
  });

  it('shows an amount that rounds to zero as 0, neither signed nor red', () => {
    expect(formatAmount(-0.4)).toBe('0');
    expect(amountTone(-0.4)).toBe('none');
  });

  it('paints red exactly what a format shows signed: to the cent, -0.01 is red and -0.004, shown as 0.00, is not', () => {
    const cents = toneOf(new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'negative' }).format);
    expect([cents(-0.01), cents(-0.004), cents('pending')]).toEqual(['negative', 'none', 'none']);
    expect([isLoss(-2), isLoss(-0.4), isLoss(undefined)]).toEqual([true, false, false]);
  });
});
