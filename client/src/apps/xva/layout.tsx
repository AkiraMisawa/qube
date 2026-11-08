import { useRef, type CSSProperties, type KeyboardEvent, type PointerEvent } from 'react';
import { px } from '@mantine/core';

// The page's own layout controls (ADR-0010 §1): a handle that sets a part's height by drag or by keys. The page is told
// where the pointer is by the browser's events; nothing here reads a layout.

const STEP = 24;

/** What a `WorkbenchGrid` adds outside its viewport: its paper's 1px border above and below. */
export const GRID_FRAME = 2;

/** A theme length, such as `theme.spacing.xs`, in pixels: what a grid's height in pixels leaves for it. */
export function pixels(length: string): number {
  const n = px(length);
  if (typeof n !== 'number') throw new Error(`The theme length ${length} is not one in pixels.`);
  return n;
}

// A compact grid's height is the platform's, from ExGrid's Grid Metrics.
export { compactGridHeight } from '../../platform/grid/metrics';

/**
 * A part's height as the stylesheet reads it (`--qwb-part-height`), so that below `sm`, where a part is a tab as high
 * as its content, the stylesheet sets it aside.
 */
export const partHeight = (height: number): CSSProperties => ({ ['--qwb-part-height' as string]: `${height}px` });

/**
 * A horizontal separator that sets a height in pixels, between `min` and `max`. Dragging it moves the height with the
 * pointer, down for a part above it (`grows="down"`) or up for one below (`grows="up"`); the arrow keys move it a step,
 * Home and End to its bounds. It is one tab stop and says its value, as a separator that moves does (WAI-ARIA).
 */
export function Resizer({ label, value, min, max, grows, onChange }: {
  readonly label: string;
  readonly value: number;
  readonly min: number;
  readonly max: number;
  readonly grows: 'down' | 'up';
  readonly onChange: (height: number) => void;
}) {
  const drag = useRef<{ readonly y: number; readonly from: number } | undefined>(undefined);
  const clamp = (h: number) => Math.round(Math.min(max, Math.max(min, h)));
  const sign = grows === 'down' ? 1 : -1;
  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    drag.current = { y: e.clientY, from: value };
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    if (drag.current !== undefined) onChange(clamp(drag.current.from + sign * (e.clientY - drag.current.y)));
  };
  const onPointerUp = () => { drag.current = undefined; };
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const next = e.key === 'ArrowDown' ? value + sign * STEP : e.key === 'ArrowUp' ? value - sign * STEP
      : e.key === 'Home' ? min : e.key === 'End' ? max : undefined;
    if (next === undefined) return;
    e.preventDefault();
    onChange(clamp(next));
  };
  return (
    <div role="separator" aria-orientation="horizontal" aria-label={label} aria-valuenow={value} aria-valuemin={min} aria-valuemax={max}
      tabIndex={0} className="qwb-resizer" onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp} onKeyDown={onKeyDown} />
  );
}
