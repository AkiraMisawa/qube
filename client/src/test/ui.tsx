import type { ReactElement } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderResult } from '@testing-library/react';

// What Mantine asks of a browser and jsdom has none of: stood in here, for the tests that draw a component, as
// Mantine's own guide to testing says. No test reads a layout from them.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: () => undefined, removeListener: () => undefined,
    addEventListener: () => undefined, removeEventListener: () => undefined, dispatchEvent: () => false,
  }),
});
window.ResizeObserver = class { observe() { /* jsdom lays nothing out */ } unobserve() { /* nothing observed */ } disconnect() { /* nothing observed */ } };
window.HTMLElement.prototype.scrollIntoView = () => undefined;

/** Draws a component as the workbench does: in Mantine's theme, without its transitions and portals, and with a query client of its own. */
export function renderUi(ui: ReactElement): RenderResult {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<MantineProvider env="test"><QueryClientProvider client={client}>{ui}</QueryClientProvider></MantineProvider>);
}
