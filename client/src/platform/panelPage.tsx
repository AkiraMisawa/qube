import { createContext, useContext, type ReactNode } from 'react';
import { useElementSize } from '@mantine/hooks';

// A panel's page lays itself out by the panel's width, not the window's (ADR-0018 §5, amending ADR-0010 §1 and
// docs/design-guidelines.md §9): in an area of 600px a page is a narrow page, as in a window of 600px. The page's box is
// a CSS size container, `qube-panel`, for a stylesheet's container queries; a component that chooses what mounts reads
// the width with `usePanelWidth`, as the browser reports it when it changes, never measured on the way to a paint.
// The box is the panel's whole width, the shell's gutter at each side included, and the page is drawn inside the
// gutter: a page in one area of a 768px window reads 768px, as it read the window's before there were areas.

/** The theme's `sm` (48em) in CSS pixels: below it a page is laid out as on a phone (docs/design-guidelines.md §9). */
export const SM = 768;

const PanelWidth = createContext<number | undefined>(undefined);

/** The page of one panel: drawn once its width is known, so that nothing mounts for a width it does not have. */
export function PanelPage({ children }: { readonly children: ReactNode }) {
  const { ref, width } = useElementSize<HTMLDivElement>();
  return (
    // The width the page was last told, for what reads the page only once it has been laid out for it (the tests).
    <div ref={ref} className="qube-panel-page" data-panel-width={width > 0 ? width : undefined}>
      <div className="qube-panel-page-body">
        {width > 0 && <PanelWidth.Provider value={width}>{children}</PanelWidth.Provider>}
      </div>
    </div>
  );
}

/** The width of the panel this component is drawn in, in CSS pixels. */
export function usePanelWidth(): number {
  const width = useContext(PanelWidth);
  if (width === undefined) throw new Error('usePanelWidth is read outside a panel\'s page.');
  return width;
}
