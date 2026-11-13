import { px } from '@mantine/core';

/**
 * A theme length, such as `theme.spacing.xs`, in pixels: for what takes a number, as a grid's height or a popover's
 * margin does (docs/design-guidelines.md §2: spacing is the theme's).
 */
export function pixels(length: string): number {
  const n = px(length);
  // Mantine answers a length it cannot read in pixels as it was given, or as NaN.
  if (typeof n !== 'number' || !Number.isFinite(n)) throw new Error(`The theme length ${length} is not one in pixels.`);
  return n;
}
