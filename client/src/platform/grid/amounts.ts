import type { CellTone } from '@ex-grid/core';

// An amount of money as the workbench shows one (C48, owner 2026-10-28, widened 2026-11-10): every amount grouped in
// threes, in whole units, and a negative one painted as a loss in red's text colour. Percentages, rates, spreads,
// quotes and counts are not amounts.

const whole = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0, signDisplay: 'negative' });

/** An amount in whole units with thousands separators. One that rounds to zero shows no sign. */
export const formatAmount = (value: unknown): string => whole.format(value as number);

/**
 * The tone of an amount written by `format`: a loss exactly when the figure shown carries a minus sign, so a red figure
 * always reads as negative, and one that rounds to zero is never red.
 */
export const toneOf = (format: (value: number) => string) => (value: unknown): CellTone =>
  typeof value === 'number' && format(value).startsWith('-') ? 'negative' : 'none';

/** An amount's tone as the workbench writes amounts, in whole units. */
export const amountTone = toneOf(formatAmount);

/** Whether an amount is shown as a loss, for a figure outside a grid, which marks its cell `data-negative`. */
export const isLoss = (value: unknown): boolean => amountTone(value) === 'negative';
