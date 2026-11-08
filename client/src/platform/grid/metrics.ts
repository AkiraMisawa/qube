import { GridMetrics } from '@ex-grid/core';

// A compact grid's rows and header come from ExGrid's own Grid Metrics (ExGrid ADR-0028), so a grid as high as its rows
// follows them if the preset moves.
const compact = GridMetrics.resolve({ density: 'compact' });
/** ExGrid's own horizontal scrollbar, a strip the viewport holds under the rows (12px by default, ExGrid ADR-0029). */
const SCROLLBAR = 12;

/**
 * The viewport height in pixels of a compact grid that shows `rows` rows whole, its header and scrollbar included, as
 * ExGrid's `viewportHeight` counts it. A grid that shows its Selection Summary has the status line under that.
 */
export const compactGridHeight = (rows: number): number => compact.headerHeightPx + rows * compact.rowHeightPx + SCROLLBAR;
