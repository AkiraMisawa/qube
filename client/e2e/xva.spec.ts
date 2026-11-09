import { readFile } from 'node:fs/promises';
import type { Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, test, textBelowContrast } from './fixtures';

// Slice 02 (docs/plans/slice-02-xva-workspace.md): the XVA workspace and the Pricing DB, driven by keyboard.

// New trades and existing trades are in grids of their own (ADR-0010 §6); a trade's id names its row in either.
const trade = (page: Page, id: string) => page.getByRole('row', { name: new RegExp(`^${id} `) });

/** Puts the grid's Focus on a cell of the first row, `right` columns from the first. */
async function focusCell(page: Page, right: number): Promise<void> {
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next ones move it.
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < right; i++) await page.keyboard.press('ArrowRight');
}

// The CSA's column in the New grid, which has no last fixing (ADR-0010 §6).
const CSA = 7;
// XVA's columns in the New grid, after B and A (ADR-0010 §6), by the grid's own 1-based column index: the grid draws
// the columns in view, so a cell is found by its column, not by how many cells are drawn before it.
const XVA = { pv01: 12, margin: 13, pvAdj: 14 } as const;
const cell = (page: Page, id: string, column: number) => trade(page, id).locator(`[aria-colindex="${column}"]`);

test('docs/03, ADR-0003: a new trade takes the main CSA, a CSA typed in moves its curve, and Price lands in the Pricing DB', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  // A new workspace starts on its owner's last selection, which another test may have left.
  await command(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price After only · CVA FVA · 1 case');
  // XV15: an empty CVA recovery stays empty on screen (it once read "undefined"); the CDS recovery is its placeholder.
  await expect(page.getByLabel('CVA recovery', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('CVA recovery', { exact: true })).toHaveAttribute('placeholder', /^empty: CDS /);
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveText('CSA-A');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA + 1)).toHaveText('jpy_ois');

  await focusCell(page, CSA);
  await page.keyboard.type('CSA-B');
  await page.keyboard.press('Enter');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveText('CSA-B');
  // The curve follows the CSA on the server; nothing here computed it.
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA + 1)).toHaveText('usd_sofr');
  // Both differ from Original now, the curve moved by the rule rather than typed, and are marked (ADR-0010 §2).
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveClass(/ex-state-modified/);
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA + 1)).toHaveClass(/ex-state-modified/);
  // B and A are shown by their boxes and never marked.
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA + 2)).not.toHaveClass(/ex-state-modified/);
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');

  await command(page, 'price');
  // The figures land on the page itself; the Run opens in its own panel only when asked.
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  // No existing trade: Before has nothing to price, so the dock draws After alone and says so once (ADR-0010 §7).
  await expect(result.getByTestId('xva-result-note')).toHaveText('After only: Before was not priced');
  await expect(result.getByRole('columnheader', { name: 'CVA Charge After' })).toBeVisible();
  await expect(result.getByRole('columnheader', { name: 'CVA Before' })).toHaveCount(0);
  await expect(result.getByTestId('xva-result-revision')).toHaveText(/^revision \d+$/);
  await result.getByRole('button', { name: 'Open run' }).click();
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByLabel('CVA and FVA (simulated)').getByRole('row', { name: /^Base CVA -/ })).toBeVisible();
  const runId = await page.locator('[data-testid=run-status] + p').textContent();
  expect(runId).toMatch(/^run-/);

  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await expect(page.getByLabel('Pricing DB').getByRole('row', { name: new RegExp(`${runId}`) })).toContainText('pricer');
  await page.getByRole('radiogroup', { name: 'Domain' }).getByText('XVA', { exact: true }).click();
  await expect(page.getByRole('radio', { name: 'XVA' })).toBeChecked();
  await settled(page, 'Pricing DB');
  const row = page.getByLabel('Pricing DB').getByRole('row', { name: new RegExp(`${runId}`) });
  // The domain's figures follow the Run's own columns: value date, registration, trades Before and After.
  await expect(row.getByRole('gridcell').nth(5)).toHaveText('2026-10-26');
  await expect(row.getByRole('gridcell').nth(6)).toHaveText('ABC/LE-01');
  await expect(row.getByRole('gridcell').nth(7)).toHaveText('0');
  await expect(row.getByRole('gridcell').nth(8)).toHaveText('3');
});

test('ADR-0003 §6: a counterparty change asks before dropping its edits, and keeps a new trade\'s own', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  await focusCell(page, 3);
  await page.keyboard.type('7000000');
  await page.keyboard.press('Enter');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(3)).toHaveText('7,000,000');
  await focusCell(page, CSA);
  await page.keyboard.type('CSA-B');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('workspace-summary')).toContainText('2 edit(s)');

  await page.getByRole('combobox', { name: 'Counterparty' }).click();
  await page.getByRole('option', { name: /^ABC \| LE-02 \|/ }).click();
  const confirm = page.getByRole('dialog', { name: 'Change counterparty' });
  await expect(confirm).toContainText('drops 1 edit(s)');
  await expect(confirm).toContainText('xva.assignment');
  await confirm.getByRole('button', { name: 'Drop 1 and change' }).click();

  await expect(page.getByTestId('notice')).toContainText('1 edit(s) carried');
  await settled(page, 'New trades');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(3)).toHaveText('7,000,000');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveText('CSA-C');
});

test('ADR-0010 §10: `measures fva` leaves CVA out of the Price, the Netting tab and the quote, and a new workspace starts on that choice', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  const measures = page.getByRole('group', { name: 'Measures' });

  await command(page, 'measures', 'fva');
  await expect(measures.getByRole('button', { name: 'CVA' })).toHaveAttribute('aria-pressed', 'false');
  await expect(measures.getByRole('button', { name: 'FVA' })).toHaveAttribute('aria-pressed', 'true');
  // Price says what it will run before it runs it (ADR-0010 §1).
  await expect(page.getByTestId('price')).toHaveText('Price FVA · 1 case');

  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByRole('columnheader', { name: 'FVA Diff' })).toBeVisible();
  await expect(result.getByRole('columnheader', { name: /^CVA/ })).toHaveCount(0);
  // The dock names the currency the Run priced in; the quote is CVA's, so this Run has none (§7, §11).
  await expect(result.getByTestId('xva-result-currency')).toHaveText('JPY');
  await expect(result.getByRole('button', { name: 'Copy quote' })).toBeDisabled();
  // CVA's recovery is a field CVA needs, so the Netting tab leaves it out while CVA is not selected (§10).
  const netting = page.getByRole('table', { name: 'Netting' });
  await expect(netting.getByRole('columnheader', { name: 'Enforceable' })).toBeVisible();
  await expect(netting.getByLabel('CVA recovery')).toHaveCount(0);

  // The page's own toggle runs the same command: CVA back in, with its recovery.
  await measures.getByRole('button', { name: 'CVA' }).click();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await expect(netting.getByLabel('CVA recovery')).toBeVisible();

  // No existing trade there: Before has nothing to price, and Price says so.
  await newWorkspace(page, 'ABC/LE-02', 0);
  await expect(page.getByTestId('price')).toHaveText('Price After only · CVA FVA · 1 case');
});

// Ticket 08's flow, by keyboard alone (ADR-0010 §1, §9): every step is a command or a key, as a user at speed or a
// script would drive it. It holds §5 and §8 on the way: raising a CSA threshold past 1mm GBP makes the CSA imperfect,
// which moves its trades into FVA.
test('ADR-0010 §1, §5, §8, §9: by keyboard, a CSA edit moves a trade into FVA, marked, and the Run is priced, quoted and downloaded', async ({ page }) => {
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  await command(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price After only · CVA FVA · 1 case');

  // `open csa` opens the tab and puts the keyboard in its grid.
  await command(page, 'open csa');
  await expect(page.getByRole('tab', { name: 'CSA' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'CSA terms');
  const terms = page.getByLabel('CSA terms');
  await expect(terms.locator('.ex-grid')).toContainText('CSA-A');
  // CSA-A is the main CSA, which a new trade takes (ADR-0010 §8): Main is the grid's first column.
  const csaA = terms.getByRole('row', { name: /^Yes CSA-A / });
  // The grid's first key only places its Focus (ex-grid ADR-0037); End then takes it, and the view, to FVA's column.
  await page.keyboard.press('End');
  await page.keyboard.press('End');
  await expect(csaA).toContainText('Out · CSA-A perfect: daily, MTA+threshold');
  await page.keyboard.press('Home');
  for (let i = 0; i < 10; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.type('200000000');
  await page.keyboard.press('Enter');
  // The tab counts the value that differs from Original, and the cell is marked (ADR-0010 §2).
  await expect(page.getByRole('tab', { name: 'CSA ●1' })).toBeVisible();
  await expect(csaA.locator('[aria-colindex="11"]')).toHaveClass(/ex-state-modified/);
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('End');
  await expect(csaA).toContainText('In · CSA-A imperfect: counterparty MTA+threshold');

  // DRAFT-1 takes the main CSA, CSA-A, so it is in FVA now, with the reason.
  await focusCell(page, 0);
  await page.keyboard.press('End');
  await expect(trade(page, 'DRAFT-1')).toContainText('In · CSA-A imperfect: counterparty MTA+threshold');

  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-trades')).toHaveText('Trades: CVA 0 → 3 · FVA 0 → 3');
  await expect(result.getByRole('columnheader', { name: 'CVA After' })).toBeVisible();
  await expect(result.getByTestId('xva-result-note')).toHaveText('After only: Before was not priced');

  // The quote names the side a one-sided case quotes (ADR-0010 §7).
  await command(page, 'copy quote');
  await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toMatch(/^XVA quote \(simulated\) · run-/);
  const quote = await page.evaluate(() => navigator.clipboard.readText());
  expect(quote).toMatch(/\nBase \(After only\): CVA Charge -?[\d,]+\.\d\d · PV -?[\d,]+\.\d\d$/);
  const runId = /· (run-\S+)/.exec(quote)?.[1];
  if (runId === undefined) throw new Error(`The quote names no Run: ${quote}`);

  const [download] = await Promise.all([page.waitForEvent('download'), command(page, 'download run')]);
  expect(download.suggestedFilename()).toBe(`${runId}.zip`);
  expect((await readFile(await download.path())).subarray(0, 2).toString()).toBe('PK');
});

// ADR-0010 §1: what only a browser shows, the parts' heights. The handles are separators that take keys (WAI-ARIA), so
// the keyboard sets each height as a drag does, and the part is as high as the handle says.
test('ADR-0010 §1, §7: the input tabs and the dock collapse to their row and take the height their handle sets, by keyboard', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await command(page, 'measures', 'cva fva');
  const height = async (testId: string) => (await page.getByTestId(testId).boundingBox())?.height;

  // Collapsed, the inputs are their tab row; `open netting` opens them again with the keyboard on the CVA recovery.
  const toggle = page.getByRole('button', { name: 'Hide inputs' });
  await toggle.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('button', { name: 'Show inputs' })).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId('xva-inputs-body')).toHaveCount(0);
  await command(page, 'open netting');
  await expect(page.getByRole('button', { name: 'Hide inputs' })).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByRole('textbox', { name: 'CVA recovery' })).toBeFocused();
  // A tab is as high as its content until the handle moves: Netting's row and the CSA grid show whole, with no scroll of
  // the tab's own.
  const body = page.getByTestId('xva-inputs-body');
  const overflow = () => body.evaluate((e) => e.scrollHeight - e.clientHeight);
  await expect.poll(overflow).toBe(0);
  await command(page, 'open csa');
  await settled(page, 'CSA terms');
  await expect.poll(overflow).toBe(0);
  await command(page, 'open netting');
  await expect(page.getByRole('textbox', { name: 'CVA recovery' })).toBeFocused();

  // The handle under them: a step down, then their bounds.
  const inputs = page.getByRole('separator', { name: 'Height of the input tabs' });
  const fit = Number(await inputs.getAttribute('aria-valuenow'));
  await inputs.focus();
  await page.keyboard.press('ArrowDown');
  await expect(inputs).toHaveAttribute('aria-valuenow', String(fit + 24));
  await expect.poll(() => height('xva-inputs-body')).toBe(fit + 24);
  await page.keyboard.press('End');
  await expect(inputs).toHaveAttribute('aria-valuenow', '640');
  await page.keyboard.press('Home');
  await expect(inputs).toHaveAttribute('aria-valuenow', '48');
  await expect.poll(() => height('xva-inputs-body')).toBe(48);

  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  // The dock's handle is above it, so Up raises it.
  const dock = page.getByRole('separator', { name: 'Height of the result dock' });
  const before = Number(await dock.getAttribute('aria-valuenow'));
  await dock.focus();
  await page.keyboard.press('ArrowUp');
  await expect(dock).toHaveAttribute('aria-valuenow', String(before + 24));
  // Collapsed, the dock is a line a case with each measure's headline Diff (ADR-0010 §7).
  await result.getByRole('button', { name: 'Collapse' }).focus();
  await page.keyboard.press('Enter');
  await expect(result.getByRole('table', { name: 'Metrics' })).toHaveCount(0);
  await expect(result.getByTestId('xva-result-lines')).toHaveText(/^Base · CVA Diff -?[\d,]+\.\d\d · FVA Diff -?[\d,]+\.\d\d$/);
  await expect(dock).toHaveCount(0);
  await page.keyboard.press('Enter');
  await expect(result.getByRole('table', { name: 'Metrics' })).toBeVisible();
});

// ADR-0010 §1, UX03: below `sm` the page is three tabs under the context bar, worked by pointer as on a phone.
test('ADR-0010 §1, UX03: at 390px the three tabs reach every part, and the page never scrolls sideways', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await command(page, 'measures', 'cva fva');
  const sideways = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  const netting = page.getByRole('table', { name: 'Netting' });
  const newTrades = page.getByLabel('New trades', { exact: true });

  // Trades first: the grids, and neither the inputs nor the dock.
  await expect(page.getByRole('radio', { name: 'Trades' })).toBeChecked();
  await expect(newTrades).toBeVisible();
  await expect(netting).toBeHidden();
  await expect(page.getByTestId('xva-result-none')).toBeHidden();
  expect(await sideways()).toBe(0);

  await page.getByText('Inputs', { exact: true }).click();
  await expect(netting).toBeVisible();
  await expect(newTrades).toBeHidden();
  expect(await sideways()).toBe(0);
  await page.getByRole('tab', { name: 'CSA' }).click();
  await settled(page, 'CSA terms');
  expect(await sideways()).toBe(0);

  await page.getByText('Results', { exact: true }).click();
  await expect(page.getByTestId('xva-result-none')).toBeVisible();
  await page.getByTestId('price').click();
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByRole('table', { name: 'Metrics' })).toBeVisible();
  // The Metrics are wider than a phone: they scroll inside the dock, never the page.
  expect(await sideways()).toBe(0);
});

// docs/design-guidelines.md §7 (WCAG 1.4.3): every text the page draws, figures, words and badges, reads at 4.5:1 or
// more against what is behind it, in both themes. Measured in the browser, which alone knows the colours it painted.
test('ADR-0010 §1, docs/design-guidelines.md §7: in the dark and the light theme every text on the XVA page reads at 4.5:1', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await command(page, 'measures', 'cva fva');
  await command(page, 'price');
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');
  // A result older than the workspace, in yellow (ADR-0010 §7).
  await command(page, 'measures', 'cva');
  await expect(page.getByTestId('xva-result-revision')).toHaveText(/ · workspace now /);

  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    // On a desktop screen, then on a phone's, where the page's parts are tabs.
    await page.setViewportSize({ width: 1440, height: 900 });
    expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 1440px: text below 4.5:1`).toEqual([]);
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByText('Results', { exact: true })).toBeVisible();
    expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 390px: text below 4.5:1`).toEqual([]);
  }
});

test('ADR-0010 §3, §6: `before none` clears Before on every existing trade at once, and Price then prices After alone', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  // A new workspace starts on its owner's last selection, which another test may have left.
  await command(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');

  await command(page, 'before', 'none grid=existing');
  await expect(page.getByTestId('price')).toHaveText('Price After only · CVA FVA · 1 case');
  // One edit for the whole grid, whatever its size.
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');

  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-note')).toHaveText('After only: Before was not priced');
  await expect(result.getByRole('columnheader', { name: 'CVA Diff' })).toHaveCount(0);

  // The caption's own control does the same, back again.
  await page.getByRole('button', { name: 'Tick Before on every existing trade' }).click();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
});

test('ADR-0010 §3, §9: `vary` makes a case per value beside the Base, and one Price gives Metrics a row per case', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await command(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');

  await command(page, 'vary', 'CSA-A threshold 200mm, 50mm, 0');
  await expect(page.getByTestId('notice')).toHaveText('Made 2 case(s): CSA-A threshold 200mm, CSA-A threshold 50mm; skipped 0 is the workspace\'s value.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 3 cases');
  // The workspace is the Base: a case's change is not an edit of it.
  await expect(page.getByTestId('workspace-summary')).toContainText('0 edit(s)');

  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  const metrics = result.getByRole('table', { name: 'Metrics' });
  await expect(metrics.getByRole('rowheader')).toHaveText(['Base', 'CSA-A threshold 200mm', 'CSA-A threshold 50mm']);
  // Collapsed, the dock keeps a line per case with each measure's headline Diff (ADR-0010 §7).
  await result.getByRole('button', { name: 'Collapse' }).click();
  await expect(result.getByTestId('xva-result-lines').locator('p')).toHaveText([/^Base · CVA Diff /, /^CSA-A threshold 200mm · CVA Diff /, /^CSA-A threshold 50mm · CVA Diff /]);
});

test('ADR-0010 §3: the Changes tab counts every change, sets a case\'s value, and removes a case, as `case remove` does', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await command(page, 'measures', 'cva fva');
  await command(page, 'vary', 'CSA-A threshold 5mm, 10mm, 20mm');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 4 cases');

  await command(page, 'open changes');
  await expect(page.getByRole('tab', { name: 'Changes ●3' })).toHaveAttribute('aria-selected', 'true');
  const changes = page.getByRole('table', { name: 'Changes' });
  await expect(changes.getByRole('row', { name: /^Common/ })).toContainText('No Common change');
  const five = changes.getByRole('textbox', { name: 'CSA-A threshold in CSA-A threshold 5mm' });
  await expect(five).toHaveValue('5mm');
  // The server reads the value as the cell reads it, and answers what it holds now: a revision of its own.
  const summary = page.getByTestId('workspace-summary');
  const before = await summary.textContent();
  await five.fill('7.5mm');
  await page.keyboard.press('Enter');
  await expect(summary).not.toHaveText(before ?? '');
  await expect(five).toHaveValue('7.5mm');

  await changes.getByRole('button', { name: 'Remove case CSA-A threshold 10mm' }).click();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 3 cases');
  await expect(page.getByRole('tab', { name: 'Changes ●2' })).toBeVisible();
  await command(page, 'case remove', 'CSA-A threshold 20mm');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 2 cases');
  await expect(changes.getByRole('row', { name: /^CSA-A threshold 20mm/ })).toHaveCount(0);
  await command(page, 'cases clear');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
});

test('docs/03 §5, ADR-0010 §3: `add trades` makes a case per new trade alone, refuses one in Before by name, and Price refuses a set the rows left', async ({ page, problems }) => {
  const refused = 'error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)';
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await command(page, 'measures', 'cva fva');

  await command(page, 'add trades');
  await expect(page.getByTestId('notice')).toHaveText('Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 4 cases');
  // B on every new trade after the set was made: Price says which trades left it, and prices nothing.
  await command(page, 'before', 'all grid=new');
  await command(page, 'price');
  await expect(page.getByTestId('notice')).toHaveText('Price was refused: add trades prices DRAFT-1, DRAFT-2, DRAFT-3 alone, and DRAFT-1, DRAFT-2, DRAFT-3 are in Before. '
    + 'Run add trades again to make the set anew, with B clear on each.');
  // Made again, a candidate in Before is refused by name, never unticked.
  await command(page, 'add trades');
  await expect(page.getByTestId('notice')).toHaveText('add trades made no case: DRAFT-1, DRAFT-2, DRAFT-3 are in Before, and a new trade is priced alone only out of Before. '
    + 'Clear B on them, or A to leave them out, then add trades again.');
  expect(problems.splice(0)).toEqual([refused, refused]);

  await command(page, 'before', 'none grid=new');
  await command(page, 'add trades');
  await expect(page.getByTestId('notice')).toHaveText('Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3; replaced add DRAFT-1, add DRAFT-2, add DRAFT-3.');
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByRole('table', { name: 'Metrics' }).getByRole('rowheader')).toHaveText(['Base', 'add DRAFT-1', 'add DRAFT-2', 'add DRAFT-3']);
});

test('ADR-0010 §3: Vary values… on a CSA threshold\'s Context Menu makes three cases, by keyboard; a read-only cell offers none', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await command(page, 'measures', 'cva fva');
  await command(page, 'open csa');
  await settled(page, 'CSA terms');
  const csaA = page.getByLabel('CSA terms').getByRole('row', { name: /^Yes CSA-A / });
  await expect(csaA).toBeVisible();
  // The grid's first key only places its Focus (ex-grid ADR-0037); Main is the source's, so its menu has no Vary values….
  await page.keyboard.press('Home');
  await page.keyboard.press('Home');
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  await expect(page.getByRole('menuitem', { name: 'Vary values…' })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('menu')).toHaveCount(0);

  // The counterparty's threshold, the eleventh column: the Consumer's commands follow the grid's own, so End reaches it.
  for (let i = 0; i < 10; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  await page.keyboard.press('End');
  await expect(page.getByRole('menuitem', { name: 'Vary values…' })).toBeFocused();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Vary CSA-A Cpty threshold' });
  await expect(dialog.getByRole('textbox', { name: 'Values' })).toBeFocused();
  // A value the cell refuses makes no case, and the dialog says why.
  await page.keyboard.type('5mm, lots');
  await page.keyboard.press('Enter');
  await expect(dialog.getByTestId('vary-said')).toHaveText('vary made no case: CSA-A threshold is an amount: 5mm, 250k, 1bn, or a bare number of millions; lots is not.');
  // The refusal is the server's 422, which the browser reports; it is the one console message this test expects.
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('5mm, 10mm, 20mm, 0');
  await page.keyboard.press('Enter');
  await expect(dialog.getByTestId('vary-said')).toHaveText('Made 3 case(s): CSA-A threshold 5mm, CSA-A threshold 10mm, CSA-A threshold 20mm; skipped 0 is the workspace\'s value.');
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 4 cases');
});

test('ADR-0010 §6: a new trade\'s PV01 arrives with no reload, and Margin gives its PV Adj, read-only and saying so', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  await focusCell(page, 0);
  await page.keyboard.press('End');
  // The simulated engine answers after a moment; the page reads the grid again once it has, and never shows 0.
  await expect(cell(page, 'DRAFT-1', XVA.pv01)).toHaveText(/^-\d{1,3}(,\d{3})*(\.\d{1,2})?$/);
  const pv01 = Number((await cell(page, 'DRAFT-1', XVA.pv01).textContent())?.replaceAll(',', ''));

  await focusCell(page, XVA.margin - 1);
  await page.keyboard.type('2');
  await page.keyboard.press('Enter');
  // Margin is typed and differs from Original, which is empty (ADR-0010 §2); PV Adj comes from it. Moving onto PV Adj
  // brings its column into view.
  await expect(cell(page, 'DRAFT-1', XVA.margin)).toHaveClass(/ex-state-modified/);
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('ArrowRight');
  const pvAdj = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(Math.round(pv01 * 2 * 100) / 100);
  await expect(cell(page, 'DRAFT-1', XVA.pvAdj)).toHaveText(pvAdj);
  await expect(page.getByText(/^From Margin: 2 bp × PV01 -/)).toBeVisible();
  // While Margin is entered, PV Adj is read-only, and typing into it is refused with the reason.
  await page.keyboard.type('5');
  await page.keyboard.press('Enter');
  await expect(page.getByLabel('New trades').getByRole('status')).toHaveText(/^PV Adj is read-only: From Margin: 2 bp × PV01 -/);
  await page.keyboard.press('Escape');
  await expect(cell(page, 'DRAFT-1', XVA.pvAdj)).toHaveText(pvAdj);

  await command(page, 'price');
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');
});

test('ADR-0010 §7: Download saves the Run as a zip, from the dock and the Pricing DB, and `copy quote` copies it as text and HTML', async ({ page }) => {
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  // An earlier test left FVA alone selected, and a new workspace starts on its owner's last selection.
  await command(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');

  const [fromDock] = await Promise.all([page.waitForEvent('download'), result.getByRole('link', { name: 'Download' }).click()]);
  const runId = fromDock.suggestedFilename().replace(/\.zip$/, '');
  expect(runId).toMatch(/^run-/);
  expect((await readFile(await fromDock.path())).subarray(0, 2).toString()).toBe('PK');

  await command(page, 'copy quote');
  await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toMatch(new RegExp(`^XVA quote \\(simulated\\) · ${runId}`));
  const text = await page.evaluate(() => navigator.clipboard.readText());
  expect(text).toMatch(/\nCounterparty ABC\/LE-01 · Netting set NS-001 · Value date 2026-\d\d-\d\d · Revision \d+ · JPY\n/);
  expect(text).toMatch(/\nBase: CVA Charge -?[\d,]+\.\d\d · PV -?[\d,]+\.\d\d$/);
  const html = await page.evaluate(async () => {
    const [item] = await navigator.clipboard.read();
    if (item === undefined) throw new Error('The clipboard holds nothing.');
    return (await item.getType('text/html')).text();
  });
  expect(html).toContain('<th>CVA Charge</th><th>PV</th>');

  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  const row = page.getByLabel('Pricing DB').getByRole('row', { name: new RegExp(runId) });
  const [fromHistory] = await Promise.all([page.waitForEvent('download'), row.getByRole('button', { name: 'Download' }).click({ force: true })]);
  expect(fromHistory.suggestedFilename()).toBe(`${runId}.zip`);
});

