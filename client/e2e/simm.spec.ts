import type { Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, test, textBelowContrast } from './fixtures';

// Ticket 04 (docs/specs/spot-simm-page/issues/04-the-page.md): spot IM on the pricer page (ADR-0012 §3, §7 with their
// notes of 2026-11-08). Its IM-R and IM-P on both trade grids and the IM tab while it is selected, an IM-R edit, the
// Metrics with each side's governing regime and the DEMO basis, and a cell that opens the trace at its direction.
// Every name and figure is synthetic.

const trade = (page: Page, id: string) => page.getByRole('row', { name: new RegExp(`^${id} `) });
/** A cell of the New grid by the grid's own 1-based column index: the grid draws the columns in view. */
const cell = (page: Page, id: string, column: number) => trade(page, id).locator(`[aria-colindex="${column}"]`);
/** A trade grid's column header by its title, which ExGrid names with its menu (`IM-R Column menu`). */
const header = (page: Page, grid: string, title: string) =>
  page.getByLabel(grid, { exact: true }).getByRole('columnheader', { name: `${title} Column menu`, exact: true });
const sideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

// The New grid's columns while CVA and spot IM are selected: the assignments, IM-R and IM-P beside the CSA, B and A,
// XVA's PV01, Margin and PV Adj, then spot IM's scopes (ADR-0010 §6, ADR-0012 §3).
const COLUMN = { imR: 10, imP: 11, scopes: 17 } as const;

/** Puts the New grid's Focus on a cell of its first row, `right` columns from the first. */
async function focusCell(page: Page, right: number): Promise<void> {
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next ones move it.
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < right; i++) await page.keyboard.press('ArrowRight');
}

/** Types into the New grid's first row's IM-R, and waits for the server's answer on the row. */
async function typeImR(page: Page, value: string): Promise<void> {
  await focusCell(page, COLUMN.imR - 1);
  await page.keyboard.type(value);
  await page.keyboard.press('Enter');
  await expect(cell(page, 'DRAFT-1', COLUMN.imR)).toHaveText(value);
}

async function selectSpotIm(page: Page): Promise<void> {
  // A new workspace starts on its owner's last selection, which another test may have left.
  await command(page, 'measures', 'simm.spotim');
  await expect(page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot IM' })).toHaveAttribute('aria-pressed', 'true');
}

test('ADR-0012 §3, §7: spot IM shows its columns and IM tab, refuses a wrong IM-R by name, and opens its trace at each direction', async ({ page }) => {
  // A wide desktop, so that the grids draw the columns up to IM-P without scrolling: ExGrid draws only those in view.
  await page.setViewportSize({ width: 1920, height: 1000 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await command(page, 'measures', 'cva');
  // Unselected, spot IM shows nothing of its own.
  await expect(header(page, 'New trades', 'IM-R')).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveCount(0);
  // The toolbar's own toggle adds spot IM to what Price prices.
  const toggle = page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot IM' });
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('price')).toHaveText('Price CVA Spot IM · 1 case');

  // IM-R and IM-P on both grids, a new trade's the counterparty's IM CSAs, and the trade's scopes in the compact form.
  for (const grid of ['New trades', 'Existing trades']) {
    await settled(page, grid);
    for (const title of ['IM-R', 'IM-P']) await expect(header(page, grid, title)).toBeVisible();
  }
  await expect(cell(page, 'DRAFT-1', COLUMN.imR)).toHaveText('IMR-ABC-01');
  await expect(cell(page, 'DRAFT-1', COLUMN.imP)).toHaveText('IMP-ABC-01');

  // The IM tab, by its command: the IM CSAs with their regimes from their dates, and the keyboard in it.
  await command(page, 'open', 'im');
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveAttribute('aria-selected', 'true');
  const imCsas = page.getByRole('table', { name: 'IM CSAs' });
  await expect(imCsas.getByRole('row', { name: /^IMR-ABC-01 / })).toContainText('JFSA 2022-09-01 · CFTC 2025-09-01');
  await expect(imCsas.getByRole('row', { name: /^IMP-ABC-01 / })).toContainText('DEMO-2.x');
  await expect(page.getByLabel('IM CSAs', { exact: true }).first()).toBeFocused();

  // An IM-R naming the counterparty's IM-P CSA: the trade is out of IM-R, and Price refuses spot IM alone, by name.
  await typeImR(page, 'IMP-ABC-01');
  await focusCell(page, COLUMN.scopes - 1);
  await expect(cell(page, 'DRAFT-1', COLUMN.scopes)).toHaveText(/^In · R: none · P: /);
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  // IM-R and IM-P, Before and After, say refused; CVA is priced, a slip in an IM column never blocking it.
  await expect(result.locator('td[data-status="refused"]')).toHaveCount(4);
  await expect(result.getByTestId('xva-result-reason').filter({ hasText: /^Spot IM refused: / })).toContainText("DRAFT-1's IM-R names IMP-ABC-01");
  await expect(result.locator('td[data-status="priced"]')).toHaveCount(9);

  // Back to the counterparty's IM-R CSA: priced, each side beside its governing regime, with the DEMO basis said.
  await typeImR(page, 'IMR-ABC-01');
  await command(page, 'price');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-revision')).toHaveText(/^revision \d+$/);
  await expect(result.getByTestId('xva-result-basis')).toHaveText('Spot IM · SIMM PARAMETERS DEMO');
  const imRAfter = result.getByRole('button', { name: /^IM-R After (JFSA|CFTC) [\d,]+\.\d{2}: open its trace$/ });
  await expect(imRAfter).toBeVisible();
  await expect(result.getByRole('button', { name: /^IM-P Before (JFSA|EMIR) [\d,]+\.\d{2}: open its trace$/ })).toBeVisible();
  await expect(result.getByTestId('xva-result-cell-note').first()).toHaveText(/^(JFSA|CFTC|EMIR)$/);

  // An IM-R cell opens the trace at collect, an IM-P cell at post.
  await imRAfter.click();
  await expect(page.getByTestId('trace-view')).toBeVisible();
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM collect');
  await page.getByRole('tab', { name: 'XVA ABC/LE-01' }).click();
  await result.getByRole('button', { name: /^IM-P Before .+: open its trace$/ }).click();
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM post');

  // In both themes, at each width ADR-0009 names, every text of the page reads at 4.5:1, the IM tab and the Metrics
  // included, and the page never scrolls sideways.
  await page.getByRole('tab', { name: 'XVA ABC/LE-01' }).click();
  await command(page, 'open', 'im');
  await expect(imCsas).toBeVisible();
  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    for (const width of [1440, 992, 768]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(imCsas).toBeVisible();
      await expect(result.getByTestId('xva-result-basis')).toBeVisible();
      expect(await sideways(page), `${scheme} at ${width}px: the page scrolls sideways`).toBe(0);
      expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at ${width}px: text below 4.5:1`).toEqual([]);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByText('Inputs', { exact: true }).click();
    await expect(imCsas).toBeVisible();
    expect(await sideways(page)).toBe(0);
    expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 390px, Inputs: text below 4.5:1`).toEqual([]);
    await page.getByText('Results', { exact: true }).click();
    await expect(result.getByTestId('xva-result-basis')).toBeVisible();
    expect(await sideways(page)).toBe(0);
    expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 390px, Results: text below 4.5:1`).toEqual([]);
    await page.getByText('Trades', { exact: true }).click();
  }
});

// Akira, 2026-11-07: computing without Before gives After alone, and its drill-down works as both sides' does.
test('ADR-0012 §7, ADR-0011 §4: with Before not priced, spot IM is After alone, and its IM-R opens the trace at collect on After', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await selectSpotIm(page);
  await command(page, 'before', 'none grid=existing');
  await expect(page.getByTestId('price')).toHaveText(/^Price After only · /);
  await command(page, 'price');

  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-note')).toHaveText('After only: Before was not priced');
  await expect(result.getByRole('columnheader', { name: 'IM-R Before' })).toHaveCount(0);
  await result.getByRole('button', { name: /^IM-R After (JFSA|CFTC) [\d,]+\.\d{2}: open its trace$/ }).click();

  await expect(page.getByTestId('trace-one-side')).toHaveText('After only: Before was not priced.');
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM collect');
  await settled(page, 'Terms');
  await expect(page.getByTestId('trace-tree').getByRole('columnheader', { name: 'Before', exact: true })).toHaveCount(0);
});

test('ADR-0012 §2, §4, ADR-0010 §10: a counterparty with no IM CSA says so, its IM is not priced and says why, and spot IM deselected takes its columns and tab away', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1000 });
  await open(page);
  await newWorkspace(page, 'XYZ/LE-03', 3);
  await command(page, 'measures', 'cva simm.spotim');
  const toggle = page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot IM' });
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');

  // The IM tab says the counterparty exchanges no initial margin, in words, where a table of IM CSAs would be empty.
  await command(page, 'open', 'im');
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('XYZ/LE-03 has no IM CSA: it exchanges no initial margin, so spot IM has no scope.')).toBeVisible();
  await expect(page.getByRole('table', { name: 'IM CSAs' })).toHaveCount(0);

  // Priced, spot IM has no figure on either side, never 0, and says why without saying it failed; CVA is priced.
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.locator('td[data-status="notPriced"]').filter({ hasText: /^not priced$/ })).toHaveCount(4);
  await expect(result.getByTestId('xva-result-reason')).toHaveText('Spot IM not priced: XYZ/LE-03 has no IM CSA: it exchanges no initial margin.');
  // A side with no figure opens no trace.
  await expect(result.getByRole('button', { name: /^IM-[RP] / })).toHaveCount(0);
  await expect(result.locator('td[data-status="priced"]').first()).toBeVisible();

  // Deselected, spot IM takes its IM-R and IM-P columns and its IM tab with it, and Inputs fall back to Netting.
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Netting' })).toHaveAttribute('aria-selected', 'true');
  for (const grid of ['New trades', 'Existing trades']) {
    await settled(page, grid);
    for (const title of ['IM-R', 'IM-P']) await expect(header(page, grid, title)).toHaveCount(0);
  }
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case');
});
