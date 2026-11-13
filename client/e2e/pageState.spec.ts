import type { Locator, Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, taken, test } from './fixtures';

// ADR-0006 note 10 of 2026-11-12: what a page shows is kept by its panel while the panel is hidden. Only the page of the
// panel shown is mounted (note 7), so what only a browser shows is a page drawn anew taking it back: the inner tab, what
// is open, and a grid's sort, conditions and Selection, placed again by ExGrid. What is kept, and when a Selection is
// placed again, is Vitest's (panelScreens.test.ts, keptGrid.test.ts).

const tab = (page: Page, label: string) =>
  page.locator('.qube-panels').getByRole('tab').filter({ has: page.locator('.qube-panel-tab-title').getByText(label, { exact: true }) });

/** Hides the panel shown by showing another, and shows it again: its page is unmounted, then drawn anew. */
async function hideAndShow(page: Page, label: string, other: string): Promise<void> {
  await tab(page, other).click();
  await expect(tab(page, other)).toHaveAttribute('aria-selected', 'true');
  await tab(page, label).click();
  await expect(tab(page, label)).toHaveAttribute('aria-selected', 'true');
}

/**
 * The cell a grid's Focus is on, by its row and column positions and its text; `none` where nothing is selected. Read in
 * one go from the grid in the page, which a page drawn anew replaces, so that the Focus and its cell are one grid's.
 */
async function focusOf(grid: Locator): Promise<string> {
  return grid.evaluate((root) => {
    const id = root.getAttribute('data-ex-follow-focus');
    if (id === null) return 'none';
    const cell = root.querySelector<HTMLElement>(`[id="${CSS.escape(id)}"]`);
    if (cell === null) return `not painted: ${id}`;
    const row = cell.closest('[aria-rowindex]')?.getAttribute('aria-rowindex');
    return `${String(row)}:${String(cell.getAttribute('aria-colindex'))}:${cell.innerText}`;
  });
}

/** What the command line says is on screen with the keyboard in a place of the page: the rows selected and the Focus's row, by key. */
async function onScreen(page: Page, place: string): Promise<string> {
  await taken(page, 'go', place);
  await page.keyboard.press('ControlOrMeta+K');
  const said = page.getByTestId('command-context');
  await expect(said).toBeVisible();
  const text = await said.innerText();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeHidden();
  return text;
}

test('ADR-0006 note 10: a pricer page hidden and shown again has its inner tab, and its trade grid\'s sort, conditions and Selection; reopened after a close, it is a page opened anew', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 60);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await tab(page, 'XVA ABC/LE-01').click();
  await settled(page, 'Existing trades');
  const existing = page.getByLabel('Existing trades', { exact: true });
  const notional = existing.getByRole('columnheader', { name: 'Notional' });
  const filtered = page.getByTestId('grid-filter').getByRole('status');

  await taken(page, 'go', 'existing');
  await taken(page, 'sort by notional desc');
  await settled(page, 'Existing trades');
  await taken(page, 'filter', 'where ccy=USD');
  await settled(page, 'Existing trades');
  await expect(filtered).toContainText('Filter: ccy=USD');
  // Two rows selected, the second and the third.
  await taken(page, 'go', 'existing');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Shift+ArrowDown');
  const selected = await onScreen(page, 'existing');
  expect(selected).toMatch(/^On screen: trades \(existing\), filtered to \d+, 2 selected, /);
  await command(page, 'open', 'csa');
  await expect(page.getByRole('tab', { name: /^CSA/ })).toHaveAttribute('aria-selected', 'true');

  await hideAndShow(page, 'XVA ABC/LE-01', 'Pricing DB');
  await settled(page, 'Existing trades');
  await expect(page.getByRole('tab', { name: /^CSA/ })).toHaveAttribute('aria-selected', 'true');
  await expect(notional).toHaveAttribute('aria-sort', 'descending');
  await expect(filtered).toContainText('Filter: ccy=USD');
  expect(await onScreen(page, 'existing')).toBe(selected);

  // A panel closed takes what it kept: reopened, its page starts as one opened anew.
  await taken(page, 'close panel');
  await taken(page, 'reopen panel');
  await expect(tab(page, 'XVA ABC/LE-01')).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Existing trades');
  await expect(page.getByRole('tab', { name: /^Netting/ })).toHaveAttribute('aria-selected', 'true');
  await expect(notional).not.toHaveAttribute('aria-sort', 'descending');
  await expect(filtered).toHaveCount(0);
  await expect.poll(() => focusOf(existing.getByRole('grid'))).toBe('none');
});

test('ADR-0006 note 10, ADR-0015 §7: a Rates page hidden and shown again has its leg open, its cashflows open and its Selection back', async ({ page }) => {
  await open(page);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await command(page, 'new rates');
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' }).getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(tab(page, 'Rates 2026-10-27')).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
  const lines = page.getByLabel('Lines', { exact: true });
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await settled(page, 'Lines');
  await page.getByRole('button', { name: 'Add CCS' }).click();
  await settled(page, 'Lines');
  await expect(lines.getByRole('grid')).toHaveAttribute('aria-rowcount', '3');
  await lines.getByRole('button', { name: 'Expand Leg 1' }).click();
  await settled(page, 'Lines');
  await expect(lines.getByRole('button', { name: 'Collapse Leg 1' })).toHaveAttribute('aria-expanded', 'true');
  const first = lines.getByRole('row', { name: /^L\d+ / }).first();
  const id = await first.locator('[role=gridcell][aria-colindex="1"]').innerText();
  await first.getByRole('button', { name: 'Flows' }).click();
  await expect(page.getByTestId('cashflows')).toContainText(`Cashflows of ${id}`);
  // The second line selected, the keyboard on its first cell.
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ArrowDown');
  const grid = lines.getByRole('grid');
  await expect.poll(() => focusOf(grid)).toMatch(/:1:L\d+$/);
  const focus = await focusOf(grid);
  expect(focus).not.toContain(`:${id}`);

  await hideAndShow(page, 'Rates 2026-10-27', 'Pricing DB');
  await settled(page, 'Lines');
  await expect(lines.getByRole('button', { name: 'Collapse Leg 1' })).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByTestId('cashflows')).toContainText(`Cashflows of ${id}`);
  await expect.poll(() => focusOf(grid)).toBe(focus);
});

test('ADR-0006 note 10: the Pricing DB shown again has its domain and its Selection back, and no Selection where a Run was added while it was hidden', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await command(page, 'price');
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  const db = page.getByLabel('Pricing DB', { exact: true });
  const grid = db.getByRole('grid');
  const domain = page.getByLabel('Domain', { exact: true });
  await domain.getByText('XVA', { exact: true }).click();
  await expect(page.getByRole('radio', { name: 'XVA' })).toBeChecked();
  await settled(page, 'Pricing DB');
  const newest = db.locator('[aria-rowindex="2"] [role=gridcell][aria-colindex="2"]');
  const priced = await newest.innerText();
  // The newest Run's third cell selected, from the keyboard.
  await taken(page, 'focus grid');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => focusOf(grid)).toMatch(/^2:3:/);
  const focus = await focusOf(grid);

  await hideAndShow(page, 'Pricing DB', 'XVA ABC/LE-01');
  await settled(page, 'Pricing DB');
  await expect(page.getByRole('radio', { name: 'XVA' })).toBeChecked();
  await expect.poll(() => focusOf(grid)).toBe(focus);
  // The window crossing 768px draws the page anew, in the strip and back in its area (ADR-0018 §5): it is kept there too.
  const size = page.viewportSize();
  if (size === null) throw new Error('The test\'s page has no viewport size.');
  await page.setViewportSize({ width: 700, height: size.height });
  await settled(page, 'Pricing DB');
  await expect(page.getByRole('radio', { name: 'XVA' })).toBeChecked();
  await expect.poll(() => focusOf(grid)).toBe(focus);
  await page.setViewportSize(size);
  await settled(page, 'Pricing DB');
  await expect.poll(() => focusOf(grid)).toBe(focus);

  // A Run priced while the list is hidden comes first: the second row is another Run, so nothing is selected.
  await tab(page, 'XVA ABC/LE-01').click();
  await settled(page, 'Existing trades');
  await command(page, 'price');
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');
  await tab(page, 'Pricing DB').click();
  await settled(page, 'Pricing DB');
  await expect(page.getByRole('radio', { name: 'XVA' })).toBeChecked();
  await expect(newest).not.toHaveText(priced);
  // Read once: a Selection is placed, if at all, as the rows come, in the task that hands them to the grid, so the paint
  // that shows the new Run would show it too.
  expect(await focusOf(grid)).toBe('none');
});

test('ADR-0006 note 10: a measure\'s trace hidden and shown again has its tab, its term selected and the terms above it open', async ({ page }) => {
  await open(page);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'kva ccr.spotcapital');
  await expect(page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot CCR capital' })).toHaveAttribute('aria-pressed', 'true');
  await command(page, 'price');
  await expect(page.getByTestId('xva-result-status')).toHaveText('succeeded');
  await page.getByTestId('xva-result').getByRole('button', { name: /^Capital After .+: open its trace$/ }).click();
  const term = page.getByTestId('trace-detail-label');
  const tree = page.getByTestId('trace-tree');
  await page.getByTestId('trace-operand').filter({ hasText: 'Risk-weighted assets' }).click();
  await expect(term).toHaveText('Risk-weighted assets');
  // Opened from the keyboard, and the term below it selected.
  await settled(page, 'Terms');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('Alt+ArrowRight');
  await expect(tree).toHaveAttribute('data-rows', '4');
  await page.keyboard.press('ArrowDown');
  await expect(term).toHaveText('Risk weight');
  const grid = page.getByLabel('Terms', { exact: true }).getByRole('grid');
  const focus = await focusOf(grid);
  expect(focus).toContain('Risk weight');

  await hideAndShow(page, 'XVA ABC/LE-01', 'Pricing DB');
  await expect(page.getByTestId('trace-title')).toHaveText('Spot CCR capital');
  await expect(term).toHaveText('Risk weight');
  await expect(tree).toHaveAttribute('data-rows', '4');
  await settled(page, 'Terms');
  await expect.poll(() => focusOf(grid)).toBe(focus);
});

test('ADR-0006 note 10: the pricer hidden and shown again has its layout back: the inputs folded and the dock\'s height, and on a phone its part', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'cva fva');
  await command(page, 'price');
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await tab(page, 'XVA ABC/LE-01').click();
  await page.getByRole('button', { name: 'Hide inputs' }).click();
  await expect(page.getByRole('button', { name: 'Show inputs' })).toHaveAttribute('aria-expanded', 'false');
  const dock = page.getByRole('separator', { name: 'Height of the result dock' });
  const height = Number(await dock.getAttribute('aria-valuenow'));
  await dock.focus();
  await page.keyboard.press('ArrowUp');
  await expect(dock).toHaveAttribute('aria-valuenow', String(height + 24));

  await hideAndShow(page, 'XVA ABC/LE-01', 'Pricing DB');
  await expect(page.getByRole('button', { name: 'Show inputs' })).toHaveAttribute('aria-expanded', 'false');
  await expect(dock).toHaveAttribute('aria-valuenow', String(height + 24));

  // On a phone the page is its three parts: the part shown is kept as well.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByText('Results', { exact: true }).click();
  await expect(page.getByRole('radio', { name: 'Results' })).toBeChecked();
  await hideAndShow(page, 'XVA ABC/LE-01', 'Pricing DB');
  await expect(page.getByRole('radio', { name: 'Results' })).toBeChecked();
  await expect(page.getByTestId('xva-result').getByRole('table', { name: 'Metrics' })).toBeVisible();
});
