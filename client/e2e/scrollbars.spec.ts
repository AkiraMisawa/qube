import type { Page } from '@playwright/test';
import { expect, newWorkspace, open, test } from './fixtures';

// ADR-0018 §5 with scrollbars that take room. Headless Chromium draws its scrollbars over the page, as macOS and a phone
// do; shown, they take 15px of the window, as on Windows and Linux. There a page beside one read less than its panel,
// and a window just past 768px stayed in whichever of the strip and the areas it was drawn in first: the strip's
// document scrolls, the areas do not. A browser's launch is the file's, so this file holds what needs the scrollbars.
test.use({
  launchOptions: {
    ignoreDefaultArgs: ['--hide-scrollbars'],
    ...(process.env.PLAYWRIGHT_CHROMIUM === undefined ? {} : { executablePath: process.env.PLAYWRIGHT_CHROMIUM }),
  },
});

const panels = (page: Page) => page.locator('.qube-panels');
const sideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

test('ADR-0018 §5: a width past 768px is areas whichever it came from, and a page reads its whole panel beside a scrollbar', async ({ page }) => {
  await page.setViewportSize({ width: 992, height: 900 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 40);
  const pageOf = page.locator('.qube-panel-page');
  // The pricer is taller than the window: in the strip the document scrolls, beside its scrollbar; in an area the page
  // scrolls inside its panel, whose width it reads whole.
  for (const [width, mode] of [[992, 'areas'], [760, 'strip'], [776, 'areas'], [768, 'areas'], [760, 'strip'], [768, 'areas']] as const) {
    await page.setViewportSize({ width, height: 900 });
    await expect(panels(page), `at ${width}px`).toHaveAttribute('data-mode', mode);
    if (mode === 'areas') await expect(pageOf, `at ${width}px`).toHaveAttribute('data-panel-width', String(width));
    expect(await sideways(page), `at ${width}px: the page scrolls sideways`).toBe(0);
  }
  // In the strip the document's scrollbar stands beside the page, which reads the width it has.
  await page.setViewportSize({ width: 760, height: 900 });
  await expect(panels(page)).toHaveAttribute('data-mode', 'strip');
  // The page is drawn anew at the crossing; the scrollbar comes once it is taller than the window again.
  const scrollbar = () => page.evaluate(() => window.innerWidth - document.documentElement.clientWidth);
  await expect.poll(scrollbar, { message: 'the scrollbar takes room' }).toBeGreaterThan(0);
  const bar = await scrollbar();
  await expect(pageOf).toHaveAttribute('data-panel-width', String(760 - bar));
});
