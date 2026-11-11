import { readFile } from 'node:fs/promises';
import type { Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, test, textBelowContrast, taken } from './fixtures';

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
/** What the open line says of its chain (ADR-0016 §9, note 24): the last it draws, a line that ran as `› <line>` and a notice as its sentence. */
const saidIn = (page: Page) => page.getByTestId('command-said').locator('[data-kind]');

test('docs/03, ADR-0003: a new trade takes the main CSA, a CSA typed in moves its curve, and Price lands in the Pricing DB', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  // A new workspace starts on its owner's last selection, which another test may have left.
  await taken(page, 'measures', 'cva fva');
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
  // B and A are shown by their boxes and never marked. Before is right of the view once every header reads whole, so
  // the Focus brings it in and its column names it.
  await focusCell(page, CSA + 2);
  await expect(cell(page, 'DRAFT-1', CSA + 3)).not.toHaveClass(/ex-state-modified/);
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
  // Trades After is right of the view once every header reads whole: the Focus brings it in, and its column names it.
  await focusCell(page, 8);
  await expect(row.locator('[aria-colindex="9"]')).toHaveText('3');
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

  await taken(page, 'measures', 'fva');
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
  await taken(page, 'measures', 'cva fva');
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
  await taken(page, 'measures', 'cva fva');
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
  await expect(result.getByTestId('xva-result-lines')).toHaveText(/^Base · CVA Diff -?[\d,]+ · FVA Diff -?[\d,]+$/);
  await expect(dock).toHaveCount(0);
  await page.keyboard.press('Enter');
  await expect(result.getByRole('table', { name: 'Metrics' })).toBeVisible();
});

// ADR-0010 §1, UX03: below `sm` the page is three tabs under the context bar, worked by pointer as on a phone.
test('ADR-0010 §1, UX03: at 390px the three tabs reach every part, and the page never scrolls sideways', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'cva fva');
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
  await taken(page, 'measures', 'cva fva');
  await command(page, 'price');
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');
  // A result older than the workspace, in yellow (ADR-0010 §7).
  await taken(page, 'measures', 'cva');
  await expect(page.getByTestId('xva-result-revision')).toHaveText(/ · workspace now /);

  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    // On a desktop screen, then on a phone's, where the page's parts are tabs.
    await page.setViewportSize({ width: 1440, height: 900 });
    expect(await page.locator('.qube-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 1440px: text below 4.5:1`).toEqual([]);
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByText('Results', { exact: true })).toBeVisible();
    expect(await page.locator('.qube-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 390px: text below 4.5:1`).toEqual([]);
  }
});

// C48: a negative amount is painted as a loss, and docs/design-guidelines.md §7 still holds: its figure reads at 4.5:1
// in both themes. The Run's view declares Before, After and Diff `currency`; the simulated CVA is negative.
test('C48, docs/design-guidelines.md §7: a negative amount in a view is painted as a loss and reads at 4.5:1 in both themes', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'cva fva');
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await result.getByRole('button', { name: 'Open run' }).click();
  const view = page.getByLabel('CVA and FVA (simulated)');
  const cva = view.getByRole('row', { name: /^Base CVA -/ });
  await expect(cva).toBeVisible();
  await expect(cva.locator('.ex-tone-negative').first()).toHaveText(/^-\d{1,3}(,\d{3})*$/);

  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    expect(await view.evaluate(textBelowContrast, 4.5), `${scheme}: text below 4.5:1`).toEqual([]);
  }
});

test('ADR-0010 §3, §6, ADR-0016 note 29: `before none` stands for a line of `set`, which the line shows, and clears Before on every existing trade at once; Price then prices After alone, and the caption\'s button runs the same line', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  // A new workspace starts on its owner's last selection, which another test may have left.
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  const line = page.getByRole('combobox', { name: 'Command' });

  // A short form says the line it stands for before Enter: that line is what runs, and what `undo` names.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('before none existing');
  await expect(page.getByTestId('command-expands')).toHaveText('Runs as set existing before off');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  // ADR-0006 note 2 (2026-11-11): a command that edits says what it did. Two of the five synthetic trades are alive.
  await expect(page.getByTestId('notice')).toHaveText('Set B to off on ABC-LE-01-E00003 and ABC-LE-01-E00004.');
  await expect(page.getByTestId('price')).toHaveText('Price After only · CVA FVA · 1 case');
  // One edit for the whole grid, whatever its size.
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');

  await command(page, 'price');
  await expect(page.getByTestId('notice')).toHaveText(/^Price was accepted as Run \S+\.$/);
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-note')).toHaveText('After only: Before was not priced');
  await expect(result.getByRole('columnheader', { name: 'CVA Diff' })).toHaveCount(0);

  // The caption's own control runs the line `before all existing` stands for, back again, and `undo` names that line.
  await page.getByRole('button', { name: 'Tick Before on every existing trade' }).click();
  await expect(page.getByTestId('notice')).toHaveText('Set B to on on ABC-LE-01-E00003 and ABC-LE-01-E00004.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await taken(page, 'undo');
  await expect(page.getByTestId('notice')).toHaveText(/^Took back: set existing before on\b/);
  await expect(page.getByTestId('price')).toHaveText('Price After only · CVA FVA · 1 case');

  // With no grid typed the grid is the one the keyboard is in, and `currency` stands for a line of `set` too.
  await taken(page, 'go', 'existing');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('before all');
  await expect(page.getByTestId('command-expands')).toHaveText('Runs as set existing before on');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('currency usd');
  await expect(page.getByTestId('command-expands')).toHaveText('Runs as set workspace currency USD');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByRole('combobox', { name: 'Currency', exact: true })).toHaveValue('USD');
});

test('ADR-0016 note 28: `measures` offers the measures not yet typed and says what Price would price, and a word that names none keeps the line with the server\'s reason', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  const line = page.getByRole('combobox', { name: 'Command' });
  const offered = page.getByRole('listbox', { name: 'What could follow' }).getByRole('option');

  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('measures kva ');
  // What could follow is the pricer's to say: the measures the line does not hold yet.
  await expect(offered.filter({ hasText: /^cva/ })).toHaveCount(1);
  await expect(offered.filter({ hasText: /^kva/ })).toHaveCount(0);
  await expect(page.getByTestId('command-preview')).toHaveText('Price would price KVA.');
  // The server reads the line before anything runs: the reason is the line's, and the selection stands.
  await page.keyboard.type('nope');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('command-problems')).toHaveText(/^No measure nope is registered; the measures are cva, fva, kva/);
  await expect(line).toHaveValue('measures kva nope');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');

  // A measure's button invokes the same command: it says nothing of its own, and `undo` names it as the line's.
  await page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'KVA', exact: true }).click();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA KVA · 1 case');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('undo');
  await expect(page.getByTestId('command-preview')).toHaveText(/^Takes back: measures cva fva kva\b/);
  await expect(page.getByTestId('notice').filter({ hasText: 'KVA' })).toHaveCount(0);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
});

test('ADR-0010 §3, §9, ADR-0016 note 28: `vary` says the cases it would make, makes a case per value beside the Base, and one Price gives Metrics a row per case', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');

  // The reading is a trial (ADR-0016 note 28): a value the cell refuses is the line's reason, and what would be made
  // is said before Enter, with nothing made.
  const line = page.getByRole('combobox', { name: 'Command' });
  await command(page, 'vary', 'CSA-A threshold 200mm, lots');
  await expect(page.getByTestId('command-problems')).toHaveText('CSA-A threshold is an amount: 5mm, 250k, 1bn, or a bare number of millions; lots is not.');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('vary CSA-A threshold 200mm, 50mm, 0');
  await expect(page.getByTestId('command-preview')).toHaveText('Makes 2 case(s): CSA-A threshold 200mm, CSA-A threshold 50mm; skips 0 is the workspace\'s value.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await page.keyboard.press('Enter');
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
  await taken(page, 'measures', 'cva fva');
  await taken(page, 'vary', 'CSA-A threshold 5mm, 10mm, 20mm');
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
  await taken(page, 'case remove', 'CSA-A threshold 20mm');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 2 cases');
  await expect(changes.getByRole('row', { name: /^CSA-A threshold 20mm/ })).toHaveCount(0);
  await taken(page, 'cases clear');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
});

test('ADR-0006 note 1 (2026-11-11): a line that cannot be read stays open with its text and the reason, and runs once corrected', async ({ page }) => {
  await open(page);
  // A chord has no line to say why in: its reason is a refused notice, and the key never does nothing in silence.
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await expect(page.getByTestId('notice')).toHaveText('focus grid: No panel is open.');
  await expect(page.getByTestId('notice')).toHaveAttribute('data-kind', 'refused');

  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const said = page.getByTestId('command-problems');

  // `set` is read by the server (ADR-0016 §5): the reason is its own, and nothing ran.
  await command(page, 'set', 'DRAFT-1 a');
  await expect(said).toHaveText('A has no value: one value is written after its field (<field> <value>), and several as name=value.');
  await expect(line).toBeFocused();
  await expect(line).toHaveValue('set DRAFT-1 a');
  // Corrected where it stands, the same line runs: the word at fault ends the line, so typing adds to it.
  await page.keyboard.type(' off');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set DRAFT-1 A to off.');

  for (const [words, args, reason] of [
    // A domain verb's line is the server's to read too (ADR-0016 note 28), and the reason is the verb's own.
    ['vary', 'CSA-A threshold', 'vary takes a cell, its row and its column, then the values separated by commas, such as vary CSA-A threshold 10mm, 5mm, 0.'],
    ['scenario duplicate', 's1', 'scenario duplicate takes the scenario to copy, then the copy\'s name in one word.'],
  ] as const) {
    await command(page, words, args);
    await expect(said).toHaveText(reason);
    await expect(line).toHaveValue(`${words} ${args}`);
    await page.keyboard.press('Escape');
    await expect(line).toBeHidden();
  }
});

test('ADR-0006 note 2 (2026-11-11): a result is teal, takes no room and leaves by itself unless it is held; a refusal is red and stays until closed', async ({ page, problems }) => {
  // The page's clock is the test's. It runs while the page works, and is stopped once a notice is in view, so that
  // "six seconds after" and "a minute later" are read off the clock and never waited for.
  await page.clock.install();
  const stopClock = async () => page.clock.pauseAt(await page.evaluate(() => Date.now()) + 1000);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const notice = page.getByTestId('notice');
  const close = notice.getByRole('button', { name: 'Close the notice' });
  // The colour is the theme's token and the icon is Tabler's: read as set, since a painted teal differs by scheme.
  const colour = () => notice.evaluate((e) => (e as HTMLElement).style.getPropertyValue('--notification-color'));
  const toolbar = page.getByRole('toolbar', { name: 'Workspace' });
  const at = await toolbar.boundingBox();

  await command(page, 'set', 'CSA-A threshold 5mm');
  await expect(notice).toHaveText('Set CSA-A threshold to 5mm.');
  // Stopped a second on: the notice has at most five seconds left, and no time passes but what the test runs.
  await stopClock();
  await expect(notice).toHaveAttribute('data-kind', 'done');
  await expect(notice).toHaveRole('status');
  expect(await colour()).toBe('var(--mantine-color-teal-filled)');
  await expect(notice.locator('.tabler-icon-check')).toBeVisible();
  // Drawn over the page: nothing under it moved.
  expect(await toolbar.boundingBox()).toEqual(at);
  expect(await notice.evaluate(textBelowContrast, 4.5), 'done: text below 4.5:1').toEqual([]);

  // Held under the pointer, however long; the six seconds start again when the pointer leaves.
  await notice.hover();
  await page.clock.runFor(60_000);
  await expect(notice).toBeVisible();
  await toolbar.hover();
  await page.clock.runFor(5000);
  await expect(notice).toBeVisible();
  // Held by the keyboard too, by a hold of its own.
  await close.focus();
  await page.clock.runFor(60_000);
  await expect(notice).toBeVisible();
  await close.blur();
  await page.clock.runFor(5000);
  await expect(notice).toBeVisible();
  await page.clock.runFor(1000);
  await expect(notice).toBeHidden();
  await page.clock.resume();

  // A refused edit is named in words, never by its entity's key (it read `selection:measures measures`): the last
  // measure's own button, pressed to leave it out, asks for none.
  await taken(page, 'measures', 'cva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case');
  await page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'CVA', exact: true }).click();
  await expect(notice).toHaveText('The measures were not changed: Select at least one measure to price.');
  await stopClock();
  await expect(notice).toHaveAttribute('data-kind', 'refused');
  await expect(notice).toHaveRole('alert');
  expect(await colour()).toBe('var(--mantine-color-red-filled)');
  await expect(notice.locator('.tabler-icon-x')).toBeVisible();
  expect(await toolbar.boundingBox()).toEqual(at);
  expect(await notice.evaluate(textBelowContrast, 4.5), 'refused: text below 4.5:1').toEqual([]);
  await page.clock.runFor(60_000);
  await expect(notice).toBeVisible();
  await close.click();
  await expect(notice).toBeHidden();
  await page.clock.resume();
  const refused = 'error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)';
  expect(problems.splice(0)).toEqual([refused]);

  // And a cell by its row and its column's header, as its grid names them (it read `csa:CSA-A currency`). Ccy is the
  // CSA grid's fifth column; the grid's first key only places its Focus (ex-grid ADR-0037).
  await command(page, 'open csa');
  await settled(page, 'CSA terms');
  await page.keyboard.press('Home');
  await page.keyboard.press('Home');
  for (let i = 0; i < 4; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.type('XXX');
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText(/^CSA-A Ccy was not changed: A currency is /);
  await page.keyboard.press('Escape');
  expect(problems.splice(0)).toEqual([refused]);
});

// docs/design-guidelines.md §11: what only a browser shows, at the four widths of §9. A notice is drawn over the page,
// so one wider than the main area, or one under the header, would pass every other test.
for (const width of [1440, 992, 768, 390]) {
  test(`ADR-0006 note 2 (2026-11-11), docs/design-guidelines.md §9: at ${width}px a notice lies under the header and inside the page, and moves nothing`, async ({ page, problems }) => {
    await page.setViewportSize({ width, height: 800 });
    await open(page);
    await newWorkspace(page, 'ABC/LE-01', 5);
    const toolbar = page.getByRole('toolbar', { name: 'Workspace' });
    const at = await toolbar.boundingBox();
    // A sentence the page says of a refused edit, which wraps on a phone: the last measure's button asks for none.
    await taken(page, 'measures', 'cva');
    await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case');
    await page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'CVA', exact: true }).click();
    const notice = page.getByTestId('notice');
    await expect(notice).toHaveText('The measures were not changed: Select at least one measure to price.');
    const box = await notice.boundingBox();
    const header = await page.locator('header').boundingBox();
    if (box === null || header === null) throw new Error('The notice and the header are drawn.');
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(width);
    expect(box.y).toBeGreaterThanOrEqual(header.y + header.height);
    expect(await toolbar.boundingBox()).toEqual(at);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBe(0);
    await expect(notice.getByRole('button', { name: 'Close the notice' })).toBeInViewport();
    expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
  });
}

test('ADR-0006 note 3 (2026-11-11): the empty line and `?` list the page\'s commands first under its title, and the placeholder follows the page', async ({ page }) => {
  await open(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const list = page.getByRole('listbox', { name: 'Commands' });
  const headings = page.getByTestId('command-heading');
  // An option is found by its words, which it draws in an element of their own.
  const worded = (words: string | RegExp) => ({ has: page.getByText(words, { exact: true }) });

  // With nothing open the line names what starts something. It named Rates' commands on every page.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(line).toHaveAttribute('placeholder', 'Type what you want to do: new xva, new rates, open pricing db… (? lists them)');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  await newWorkspace(page, 'ABC/LE-01', 5);
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  // The pricer's own commands lead, in the order its module registers them (ADR-0016 note 33): `price` first.
  await expect(line).toHaveAttribute('placeholder', 'Type what you want to do: price, vary, add each… (? lists them)');
  // The page's commands come first, under the panel's title; the sample workflows came first.
  await expect(headings.first()).toHaveText('XVA ABC/LE-01');
  // Nothing is chosen while the line lists: the first command here is Price, and Enter alone runs nothing.
  await expect(list.getByRole('option', { selected: true })).toHaveCount(0);
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('command-problems')).toHaveText('Choose a command with the arrows, or type what you want to do.');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('notice')).toHaveCount(0);
  const ofThePage = page.getByRole('group', { name: 'XVA ABC/LE-01' }).getByRole('option');
  await expect(ofThePage.first()).toContainText('price');
  await expect(ofThePage.filter(worded('set'))).toHaveCount(1);
  await expect(ofThePage.filter(worded(/^new /))).toHaveCount(0);
  // What cannot run here is last, with why: with one panel open there is no other to show. Rates' commands are the
  // server's (ADR-0016 notes 27 and 36): on the pricer's page their words are no commands, and are not listed.
  await expect(headings.last()).toHaveText('Not available now');
  await expect(page.getByRole('group', { name: 'Not available now' }).getByRole('option').filter(worded('next panel')))
    .toContainText('There is no other panel.');
  await expect(list.getByRole('option').filter(worded('capture'))).toHaveCount(0);

  // `?` lists what can run, all of it: it stopped at fifty in silence, before `new rates` and `open pricing db`.
  await page.keyboard.type('?');
  await expect(headings.first()).toHaveText('XVA ABC/LE-01');
  await expect(headings.filter({ hasText: 'Not available now' })).toHaveCount(0);
  await expect(list.getByRole('option').filter(worded('new rates'))).toHaveCount(1);
  await expect(list.getByRole('option').filter(worded('open pricing db'))).toHaveCount(1);
  await expect(list.getByRole('option').filter(worded('capture'))).toHaveCount(0);
  await expect(page.getByTestId('command-more')).toHaveCount(0);

  // The arrows choose, across the headings: from nothing chosen, up is the last command listed and down the first.
  await expect(list.getByRole('option', { selected: true })).toHaveCount(0);
  await page.keyboard.press('ArrowUp');
  await expect(list.getByRole('option', { selected: true })).toContainText('open ai pane');
  await page.keyboard.press('ArrowDown');
  await expect(list.getByRole('option', { selected: true })).toContainText('price');
});

test('ADR-0006 note 4 (2026-11-11), ADR-0016 note 28: `vary` shows this workspace\'s rows in what it says it takes, and an A cell reads a flag as `set` reads it', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  // The measures' columns are drawn before the first key: a key sent to a grid not yet settled is lost.
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  // What `vary` says while nothing follows it shows a CSA of this workspace, never an id no workspace has.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type('vary ');
  await expect(page.getByTestId('command-preview')).toHaveText('vary takes a cell, its row and its column, then the values separated by commas, such as vary CSA-A threshold 10mm, 5mm, 0.');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeHidden();

  // A is the eleventh column of the New grid: ten to the right of the trade's id. The grid writes a flag True or False.
  const after = cell(page, 'DRAFT-1', 11);
  await focusCell(page, 10);
  await expect(after).toHaveText('True');

  await page.keyboard.type('off');
  await page.keyboard.press('Enter');
  await expect(after).toHaveText('False');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');

  await page.keyboard.press('ArrowUp');
  await page.keyboard.type('yes');
  await page.keyboard.press('Enter');
  await expect(after).toHaveText('True');

  await page.keyboard.press('ArrowUp');
  await page.keyboard.type('maybe');
  await page.keyboard.press('Enter');
  await expect(page.getByLabel('New trades', { exact: true }).getByRole('status')).toHaveText('Type on or off.');
  await page.keyboard.press('Escape');
  await expect(after).toHaveText('True');
});

test('ADR-0016 §3, §5: `set` writes one value on each row named through the kernel\'s command, and a value its field refuses keeps the line open with the grid\'s reason', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  const line = page.getByRole('combobox', { name: 'Command' });

  await command(page, 'set', 'CSA-A threshold 5mm');
  await expect(page.getByTestId('notice')).toHaveText('Set CSA-A threshold to 5mm.');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');
  await command(page, 'set', 'DRAFT-1, DRAFT-2 a off');
  await expect(page.getByTestId('notice')).toHaveText('Set A to off on DRAFT-1 and DRAFT-2.');
  await expect(page.getByTestId('workspace-summary')).toContainText('3 edit(s)');
  // An edit of the workspace, not a case.
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');

  // The server reads the value before anything runs: the reason is the line's, and no request was refused.
  await command(page, 'set', 'CSA-A threshold -1mm');
  await expect(page.getByTestId('command-problems')).toHaveText('CSA-A threshold is an amount of 0 or more; -1mm is not.');
  await expect(line).toHaveValue('set CSA-A threshold -1mm');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('workspace-summary')).toContainText('3 edit(s)');
});

test('ADR-0016 §5: `set` offers rows, then fields, then a field\'s values, and Tab takes one; the line says what would change before Enter', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const offered = page.getByRole('listbox', { name: 'What could follow' }).getByRole('option');

  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set ');
  // What could follow `set`: the words that name rows, the tables, and rows by their ids.
  for (const word of ['all', 'new', 'existing', 'where', 'trades', 'csas']) await expect(offered.filter({ hasText: new RegExp(`^${word}$`) })).toHaveCount(1);
  // A list is the server's answer for the text now on the line: it is read before Tab takes its first.
  await page.keyboard.type('DR');
  await expect(offered).toHaveText(['DRAFT-1trades', 'DRAFT-2trades', 'DRAFT-3trades']);
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('set DRAFT-1 ');
  await page.keyboard.type('cs');
  await expect(offered).toHaveText(['csa']);
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('set DRAFT-1 csa ');
  // A line not yet whole says why it cannot be read yet, and offers the field's values: the workspace's CSAs.
  await expect(read).toHaveText('csa has no value: one value is written after its field (<field> <value>), and several as name=value.');
  await expect(offered).toHaveText(['CSA-A', 'CSA-B']);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('set DRAFT-1 csa CSA-B ');

  // Before Enter: the rows the line means and what would change, and each argument as the server read it.
  await expect(read).toContainText('trades, 1 row: DRAFT-1');
  await expect(read).toContainText('csa: CSA-A to CSA-B');
  await expect(page.getByTestId('command-pending')).toContainText('rows DRAFT-1');
  await expect(page.getByTestId('command-pending')).toContainText('set csa CSA-B');
  await expect(page.getByTestId('workspace-summary')).toContainText('0 edit(s)');
  // After Enter: the server's sentence of what it did.
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set DRAFT-1 csa to CSA-B.');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');
});

test('ADR-0016 §5: `set where` says its rows and what would change before Enter and what it did after, and a line the server cannot read stays with its reason', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  // Ccy is the third column of the New grid and A the eleventh, ten to the right of the trade's id: two of the three
  // new trades are in USD, and each has A ticked. The grid draws the columns in view, so A is read once it is in view.
  await expect(cell(page, 'DRAFT-1', 3)).toHaveText('JPY');
  for (const id of ['DRAFT-2', 'DRAFT-3']) await expect(cell(page, id, 3)).toHaveText('USD');
  await focusCell(page, 10);
  for (const id of ['DRAFT-1', 'DRAFT-2', 'DRAFT-3']) await expect(cell(page, id, 11)).toHaveText('True');

  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set where ccy=USD a off');
  await expect(read).toContainText('trades, 2 rows: DRAFT-2, DRAFT-3');
  await expect(read).toContainText('A: 2 on to off');
  // Nothing has run: the reading wrote nothing.
  await expect(page.getByTestId('workspace-summary')).toContainText('0 edit(s)');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set A to off on DRAFT-2 and DRAFT-3.');
  await expect(cell(page, 'DRAFT-1', 11)).toHaveText('True');
  for (const id of ['DRAFT-2', 'DRAFT-3']) await expect(cell(page, id, 11)).toHaveText('False');
  await expect(page.getByTestId('workspace-summary')).toContainText('2 edit(s)');

  // A line the server cannot read stays at Enter, with its text and the reason, and the word at fault selected.
  await command(page, 'set', 'DRAFT-1 colour red');
  await expect(page.getByTestId('command-problems')).toHaveText(/^trades has no field colour; its fields are trade id, /);
  await expect(line).toBeFocused();
  await expect(line).toHaveValue('set DRAFT-1 colour red');
  expect(await line.evaluate((e: HTMLInputElement) => e.value.slice(e.selectionStart ?? 0, e.selectionEnd ?? 0))).toBe('colour');
  // Typing replaces the word, and the same line runs once it can be read.
  await page.keyboard.type('a');
  await expect(line).toHaveValue('set DRAFT-1 a red');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('command-problems')).toHaveText(/red is not/);
  await expect(line).toHaveValue('set DRAFT-1 a red');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('workspace-summary')).toContainText('2 edit(s)');
});

test('ADR-0016 §2, §5: Enter runs the reading the line shows, and a workspace someone else moved since refuses it: the line stays, is read again, and then runs', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');

  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  const asked = page.waitForRequest((r) => r.url().endsWith('/api/commands/preview') && (r.postDataJSON() as { line: string }).line === 'set new a off');
  await page.keyboard.type('set new a off');
  const request = await asked;
  const { workspaceId } = request.postDataJSON() as { workspaceId: string };
  const { revision } = await (await request.response())!.json() as { revision: number };
  await expect(read).toContainText('trades, 3 rows: DRAFT-1, DRAFT-2, DRAFT-3');
  await expect(read).toContainText('A: 3 on to off');

  // The AI, for the same user, takes one of the three rows out of After while the line is open.
  const moved = await page.request.post('/api/agent/commands/entity.set/1.0.0',
    { data: { inputs: { request: { workspaceId, revision, rows: 'DRAFT-1', set: 'a=off' } }, idempotencyKey: `e2e-${Date.now()}` } });
  expect(moved.status()).toBe(200);

  // Enter runs what the line shows, which was read of the workspace before it moved: it is refused, and nothing ran.
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('command-problems')).toHaveText(`The workspace is at revision ${revision + 1}, not ${revision}.`);
  await expect(line).toHaveValue('set new a off');
  await expect(line).toBeFocused();
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 409 (Conflict)']);
  // The line is read again, of the workspace as it now stands, and Enter runs that.
  await expect(read).toContainText('A: 2 on to off, 1 already off');
  await expect(read).not.toHaveAttribute('aria-busy', 'true');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set A to off on DRAFT-2 and DRAFT-3; 1 already held it.');
  await focusCell(page, 10);
  for (const id of ['DRAFT-1', 'DRAFT-2', 'DRAFT-3']) await expect(cell(page, id, 11)).toHaveText('False');
});

test('ADR-0016 §5: while the server reads the line, nothing is taken from its reading of an earlier text; a line closed while Enter is read says its reason as a notice; and a reading the server did not give is said as a failure', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  const line = page.getByRole('combobox', { name: 'Command' });
  const list = page.getByRole('listbox', { name: 'What could follow' });
  const offered = list.getByRole('option');

  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set DR');
  await expect(offered).toHaveText(['DRAFT-1trades', 'DRAFT-2trades', 'DRAFT-3trades']);

  // From here the server is slow: each reading waits until the test lets it go.
  let letGo = (): void => undefined;
  let waits = new Promise<void>((resolve) => { letGo = resolve; });
  // A reading the line no longer waits for was given up by the page, and has nothing to continue.
  await page.route('**/api/commands/preview', async (route) => { await waits; await route.continue().catch(() => undefined); });
  await page.keyboard.type('AFT-2 cs');
  // The list on screen is still the one for `set DR`, whose first would be written over the row now typed: it is
  // said to be an earlier reading, and Tab takes nothing from it.
  await expect(page.getByText('Reading the line…')).toBeVisible();
  await expect(list).toHaveAttribute('aria-busy', 'true');
  await expect(offered).toHaveText(['DRAFT-1trades', 'DRAFT-2trades', 'DRAFT-3trades']);
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('set DRAFT-2 cs');
  letGo();
  await expect(offered).toHaveText(['csa']);
  await expect(list).not.toHaveAttribute('aria-busy', 'true');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('set DRAFT-2 csa ');

  // Enter while the server is slow, and the line closed before it answers: the reason has no line left to stay on.
  waits = new Promise<void>((resolve) => { letGo = resolve; });
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('set DRAFT-2 colour red');
  await page.keyboard.press('Enter');
  await expect(line).toHaveAttribute('aria-busy', 'true');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  letGo();
  await expect(page.getByTestId('notice')).toHaveText(/^set: trades has no field colour; its fields are trade id, /);
  await expect(page.getByTestId('notice')).toHaveAttribute('data-kind', 'refused');
  await expect(page.getByTestId('workspace-summary')).toContainText('0 edit(s)');

  // The server answering with no reading is said as a failure, in the line's red: it is not what the line means.
  await page.unroute('**/api/commands/preview');
  await page.route('**/api/commands/preview', (route) => route.fulfill({ status: 404, json: { code: 'UnknownWorkspace', message: 'This workspace is gone.', issues: [] } }));
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set ');
  await expect(page.getByTestId('command-unread')).toHaveText('The server did not read the line: This workspace is gone.');
  await expect(page.getByTestId('command-preview')).toHaveCount(0);
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 404 (Not Found)']);
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
});

test('ADR-0016 §11: `undo` says what it would take back before Enter and what it took back after, `redo` puts it back, and a grid\'s Ctrl+Z and Ctrl+Y do the same', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });

  // A workspace as it was made has nothing to take back: the line says so before Enter, and Enter keeps it.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('undo');
  await expect(page.getByTestId('command-preview')).toHaveText('There is nothing to take back: the workspace is as it was made.');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('command-problems')).toHaveText('There is nothing to take back: the workspace is as it was made.');
  await expect(line).toHaveValue('undo');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // One command on the line: undo names it as it was typed, before Enter and after, and redo puts it back.
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  await command(page, 'set', 'CSA-A threshold 5mm');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('undo');
  await expect(page.getByTestId('command-preview')).toContainText(/^Takes back: set CSA-A threshold 5mm \(you, 1 edit\) · \d/);
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('notice')).toHaveText('Took back: set CSA-A threshold 5mm. redo puts it back.');
  await expect(page.getByTestId('notice')).toHaveAttribute('data-kind', 'done');
  await expect(page.getByTestId('workspace-summary')).not.toContainText('1 edit(s)');
  await command(page, 'redo');
  await expect(page.getByTestId('notice')).toHaveText('Put back: set CSA-A threshold 5mm.');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');

  // In a grid the keys run the same two commands: A is ten to the right of a new trade's id.
  const after = cell(page, 'DRAFT-1', 11);
  await focusCell(page, 10);
  await expect(after).toHaveText('True');
  await page.keyboard.type('off');
  await page.keyboard.press('Enter');
  await expect(after).toHaveText('False');
  await page.keyboard.press('ControlOrMeta+Z');
  await expect(page.getByTestId('notice')).toHaveText('Took back: set DRAFT-1 A. redo puts it back.');
  await expect(after).toHaveText('True');
  await page.keyboard.press('ControlOrMeta+Y');
  await expect(page.getByTestId('notice')).toHaveText('Put back: set DRAFT-1 A.');
  await expect(after).toHaveText('False');
  // A key has no line to say why in: with nothing left to put back, its reason is a notice.
  await page.keyboard.press('ControlOrMeta+Y');
  await expect(page.getByTestId('notice')).toHaveAttribute('data-kind', 'refused');
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
});

test('ADR-0016 §9, notes 16, 18, 19: the line says what is on screen, a field alone acts on the trades selected, `selected where` narrows them, and `reset` alone is the focused cell', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const head = page.getByTestId('command-pending');
  const screen = page.getByTestId('command-context');

  // No grid has had the keyboard yet: the line names no screen, and a line that needs one says so.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set selected a off');
  await expect(read).toHaveText('selected means the rows selected on a screen, and no screen is behind this command: name the rows by their ids, all, a subset or where.');
  await expect(screen).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // Three new trades selected by keyboard, from the CSA of the first: the line says so before anything is typed.
  await focusCell(page, CSA);
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: trades (new), 3 selected, DRAFT-1 · csa');
  // A field alone is theirs: the rows are read from the screen, and said to be.
  await page.keyboard.type('set csa CSA-B');
  await expect(head).toContainText('rows from the screen: DRAFT-1, DRAFT-2, DRAFT-3');
  await expect(read).toContainText('trades, 3 rows: DRAFT-1, DRAFT-2, DRAFT-3');
  await expect(read).toContainText('csa: 3 CSA-A to CSA-B');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set csa to CSA-B on DRAFT-1, DRAFT-2 and DRAFT-3.');
  for (const id of ['DRAFT-1', 'DRAFT-2', 'DRAFT-3']) await expect(cell(page, id, CSA + 1)).toHaveText('CSA-B');
  await expect(page.getByTestId('workspace-summary')).toContainText('3 edit(s)');

  // The grid keeps its selection: `selected where` takes the USD trades among the three, and the words are the user's.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set selected where ccy=USD a off');
  await expect(head).toContainText('rows selected where ccy=USD');
  await expect(head).not.toContainText('from the screen');
  await expect(read).toContainText('trades, 2 rows: DRAFT-2, DRAFT-3');
  await expect(read).toContainText('A: 2 on to off');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set A to off on DRAFT-2 and DRAFT-3.');
  await expect(page.getByTestId('workspace-summary')).toContainText('5 edit(s)');

  // `reset` with nothing after it is the cell the keyboard is on, DRAFT-1's CSA, whatever else is selected.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('reset ');
  await expect(head).toContainText('rows from the screen: DRAFT-1');
  await expect(head).toContainText('field from the screen: csa');
  await expect(read).toContainText('trades, 1 row: DRAFT-1');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(cell(page, 'DRAFT-1', CSA + 1)).toHaveText('CSA-A');
  for (const id of ['DRAFT-2', 'DRAFT-3']) await expect(cell(page, id, CSA + 1)).toHaveText('CSA-B');
  await expect(page.getByTestId('workspace-summary')).toContainText('4 edit(s)');
});

test('ADR-0016 §2, notes 16, 17: every row of a grid selected with one key is that many rows, and in a sorted grid the rows are those at the positions selected', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 60);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const head = page.getByTestId('command-pending');
  const screen = page.getByTestId('command-context');
  const existing = page.getByLabel('Existing trades', { exact: true });
  /** The trade drawn at a position of the Existing grid, from 0; the header is the grid's first row. */
  const at = (position: number) => existing.locator(`[aria-rowindex="${position + 2}"] [aria-colindex="1"]`);

  // Ctrl+A in the Existing grid: the rows are every one of its 43, most of them never drawn, and one command writes them.
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: trades (existing), 43 selected, ABC-LE-01-E00003 · trade id');
  await page.keyboard.type('set selected a off');
  await expect(head).toContainText('rows selected');
  await expect(read).toContainText('trades, 43 rows: ABC-LE-01-E00003, ');
  await expect(read).toContainText('A: 43 on to off');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set A to off on 43 trades.');
  await expect(page.getByTestId('workspace-summary')).toContainText('43 edit(s)');

  // Sorted by notional, largest first, from the column's menu: a position is a row of that order.
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Alt+ArrowDown');
  await page.getByRole('menuitem', { name: 'Sort descending' }).click();
  await expect(existing.getByRole('columnheader', { name: 'Notional' })).toHaveAttribute('aria-sort', 'descending');
  await settled(page, 'Existing trades');
  await expect(at(0)).not.toHaveText('ABC-LE-01-E00003');
  const [first, second] = [await at(0).innerText(), await at(1).innerText()];
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText(`On screen: trades (existing), 2 selected, ${first} · trade id`);
  await page.keyboard.type('set curve usd_sofr');
  await expect(head).toContainText(`rows from the screen: ${first}, ${second}`);
  await expect(read).toContainText(`trades, 2 rows: ${first}, ${second}`);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText(`Set curve to usd_sofr on ${first} and ${second}.`);
  await expect(page.getByTestId('workspace-summary')).toContainText('45 edit(s)');
});

test('ADR-0016 §2, §9, notes 16, 17, 19: the CSA grid is a screen of the CSAs, named by their ids, `shown` is the rows its filter leaves, and a grid of no table leaves the line none', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const screen = page.getByTestId('command-context');

  // The keyboard in the CSA grid, on CSA-A's currency, the first term after Main, the id, the type and the agreement.
  await command(page, 'open', 'csa');
  await expect(page.getByRole('tab', { name: /^CSA/ })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'CSA terms');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < 4; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: csas, 1 selected, CSA-A · currency');
  await page.keyboard.type('set threshold 5mm');
  await expect(page.getByTestId('command-pending')).toContainText('rows from the screen: CSA-A');
  await expect(read).toContainText('threshold: 0 to 5mm');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set CSA-A threshold to 5mm.');

  // Filtered from its column's menu, the grid shows CSA-B alone: `shown` is the rows the user sees (ADR-0016 §2), and
  // the line says that the grid is filtered.
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Alt+ArrowDown');
  await page.getByRole('dialog', { name: 'CSA', exact: true }).getByRole('textbox').first().focus();
  await page.keyboard.type('CSA-B');
  await page.keyboard.press('Enter');
  await expect(page.getByLabel('CSA terms').getByRole('row')).toHaveCount(2);
  await settled(page, 'CSA terms');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: csas, filtered to 1, nothing selected');
  await page.keyboard.type('set shown threshold 7mm');
  await expect(read).toContainText('csas, 1 row: CSA-B');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // The Scenarios grid is a grid of no table: once the keyboard has been in it, the line has no screen to read.
  await command(page, 'open scenarios');
  await expect(page.getByRole('tab', { name: 'Scenarios' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Shifts');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set threshold 5mm');
  // With no rows on screen `threshold` is no field of theirs: the first word is read as a row, as it always was.
  await expect(read).toHaveText('No row threshold in trades, csas, netting, credit, cds, workspace or xva.');
  await expect(screen).toHaveCount(0);
});

/** What has the keyboard inside the part of the page with this label: one element while the keyboard is there. */
const keyboardIn = (page: Page, label: string) => page.getByLabel(label, { exact: true }).locator(':focus');

test('ADR-0016 §9, notes 20 and 30: `go` puts the keyboard in a place of the page and `open` in a tab, each says where, `focus grid` is `go` to its first grid, and a place that cannot take it says why on the line', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  const line = page.getByRole('combobox', { name: 'Command' });
  const said = page.getByTestId('command-problems');
  const notice = page.getByTestId('notice');

  for (const [words, place, title, label] of [['go', 'existing', 'Existing trades', 'Existing trades'], ['go', 'new', 'New trades', 'New trades'], ['open csa', undefined, 'the CSA tab', 'CSA terms']] as const) {
    await command(page, words, place);
    await expect(line).toBeHidden();
    await expect(notice).toHaveText(`The keyboard is in ${title}.`);
    await expect(keyboardIn(page, label)).toHaveCount(1);
  }
  // The chord and its words are `go` to the page's first grid, from wherever the keyboard is.
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await expect(notice).toHaveText('The keyboard is in New trades.');
  await expect(keyboardIn(page, 'New trades')).toHaveCount(1);
  await command(page, 'go', 'existing');
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);
  await command(page, 'focus grid');
  await expect(notice).toHaveText('The keyboard is in New trades.');
  await expect(keyboardIn(page, 'New trades')).toHaveCount(1);

  // `go` offers the page's places that are no tab, and a word that names none is refused with the places there are.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('go ');
  await expect(page.getByRole('option')).toHaveText([/^new/, /^existing/, /^results/]);
  await page.keyboard.type('csa');
  await page.keyboard.press('Enter');
  await expect(said).toHaveText('place: one of new, existing, results');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // A place that cannot take the keyboard now says why, and the line stays: nothing is priced, so the dock has no results.
  await command(page, 'go', 'results');
  await expect(said).toHaveText('Nothing is priced yet: Price shows its results here.');
  await expect(line).toHaveValue('go results');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await command(page, 'go', 'results');
  await expect(notice).toHaveText('The keyboard is in the results.');
  await expect(result.locator(':focus')).toHaveCount(1);

  // Where the page is one part at a time, the part a place is in is shown first.
  await page.setViewportSize({ width: 390, height: 844 });
  await command(page, 'open csa');
  await expect(page.getByRole('radio', { name: 'Inputs' })).toBeChecked();
  await expect(keyboardIn(page, 'CSA terms')).toHaveCount(1);
  await command(page, 'go', 'existing');
  await expect(page.getByRole('radio', { name: 'Trades' })).toBeChecked();
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);
  await command(page, 'go', 'results');
  await expect(page.getByRole('radio', { name: 'Results' })).toBeChecked();
  await expect(result.locator(':focus')).toHaveCount(1);
});

test('ADR-0016 §9, notes 20 and 30: `open <tab>` puts the keyboard in the tab and says where, on the tab itself where it has nothing to work in, and a tab no selected measure needs refuses on the line', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  // FVA alone reads no CVA recovery, so Netting draws no field; and no measure selected has a Credit or an IM tab.
  await taken(page, 'measures', 'fva');
  const line = page.getByRole('combobox', { name: 'Command' });
  const said = page.getByTestId('command-problems');
  const notice = page.getByTestId('notice');
  const inputs = page.getByRole('tablist', { name: 'Inputs' });

  await command(page, 'open netting');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText('The keyboard is in the Netting tab.');
  await expect(inputs.getByRole('tab', { name: /^Netting/ })).toBeFocused();

  await command(page, 'open csa');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText('The keyboard is in the CSA tab.');
  await expect(keyboardIn(page, 'CSA terms')).toHaveCount(1);

  for (const [words, reason] of [['open im', 'The IM tab shows only while a selected measure needs it, and none does now.'], ['open credit', 'The Credit tab shows only while a selected measure needs it, and none does now.']] as const) {
    await page.keyboard.press('ControlOrMeta+K');
    await expect(line).toBeFocused();
    await page.keyboard.type(words);
    await page.keyboard.press('Enter');
    await expect(said).toHaveText(reason);
    await expect(line).toHaveValue(words);
    await page.keyboard.press('Escape');
    await expect(line).toBeHidden();
    // Nothing moved: the keyboard is back in the grid it was in, and the notice still says where it last went.
    await expect(keyboardIn(page, 'CSA terms')).toHaveCount(1);
    await expect(notice).toHaveText('The keyboard is in the CSA tab.');
  }
});

test('ADR-0016 §9, note 22: after `set`, `reset`, `undo` and Price from the line the keyboard is back where it was before the line opened', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  const line = page.getByRole('combobox', { name: 'Command' });
  const screen = page.getByTestId('command-context');
  const notice = page.getByTestId('notice');

  // From a cell of a grid: DRAFT-2's CSA, which each command leaves the keyboard on.
  await focusCell(page, CSA);
  await page.keyboard.press('ArrowDown');
  // `reset` takes its cell from the screen: its words and a space are a whole line.
  for (const [typed, did] of [
    ['set csa CSA-B', 'Set DRAFT-2 csa to CSA-B.'],
    ['reset ', 'Reset DRAFT-2 csa.'],
    ['undo', 'Took back: reset trades DRAFT-2 csa. redo puts it back.'],
  ] as const) {
    await page.keyboard.press('ControlOrMeta+K');
    await expect(line).toBeFocused();
    await page.keyboard.type(typed);
    await expect(page.getByTestId('command-preview')).not.toHaveAttribute('aria-busy', 'true');
    await expect(page.getByTestId('command-preview')).not.toBeEmpty();
    await page.keyboard.press('Enter');
    await expect(line).toBeHidden();
    await expect(notice).toHaveText(did);
    await expect(keyboardIn(page, 'New trades')).toHaveCount(1);
  }
  await command(page, 'price');
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(keyboardIn(page, 'New trades')).toHaveCount(1);
  // The same cell, as the line opened next reads it.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: trades (new), 1 selected, DRAFT-2 · csa');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // From a field that is no grid: the CVA recovery of the Netting tab.
  await command(page, 'open netting');
  const recovery = page.getByRole('textbox', { name: 'CVA recovery' });
  await expect(recovery).toBeFocused();
  await command(page, 'set', 'DRAFT-1 a off');
  await expect(notice).toHaveText('Set DRAFT-1 A to off.');
  await expect(recovery).toBeFocused();
});

test('ADR-0016 §9, note 21: `find` puts the Focus on a row the sorted grid has not loaded, or on its cell by a field, with the keyboard in the grid; a row it cannot place keeps the line with the reason', async ({ page }) => {
  // Every trade the server has answered the Existing grid with: a row not among them was never loaded.
  const loaded = new Set<string>();
  page.on('response', (response) => {
    if (!response.url().endsWith('/rows') || response.request().postDataJSON().grid !== 'existing') return;
    // An answer the grid stopped waiting for has no body to read, and gave the grid no row.
    void response.json().then((answer: { readonly rows: readonly { readonly tradeId: string }[] }) => { for (const row of answer.rows) loaded.add(row.tradeId); }, () => undefined);
  });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 600);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const said = page.getByTestId('command-problems');
  const screen = page.getByTestId('command-context');
  const notice = page.getByTestId('notice');
  const existing = page.getByLabel('Existing trades', { exact: true });
  const far = 'ABC-LE-01-E00300';

  // No grid has had the keyboard yet: there is none to look in, and the line says so.
  await command(page, 'find', '0300');
  await expect(said).toHaveText('find looks for a row in the grid on screen, and no grid of a table is behind this line: go to one first.');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // The Existing grid sorted by notional, largest first: the row is far down that order, in a page never asked for.
  await command(page, 'go', 'existing');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Alt+ArrowDown');
  await page.getByRole('menuitem', { name: 'Sort descending' }).click();
  await expect(existing.getByRole('columnheader', { name: 'Notional' })).toHaveAttribute('aria-sort', 'descending');
  await settled(page, 'Existing trades');
  expect(loaded.has(far)).toBe(false);

  // The end of an id that names one row is that row, and the field is one of the table's by its words.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('find 0300 csa');
  await expect(read).toContainText(`trades, 1 row: ${far}`);
  await expect(read).toContainText(`The keyboard goes to ${far} · csa.`);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText(`The keyboard is on ${far} · csa in trades (existing).`);
  await expect(trade(page, far)).toBeInViewport();
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);
  // The Focus is that cell, as the line opened next reads it, and a row found with no field keeps its column.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText(`On screen: trades (existing), 1 selected, ${far} · csa`);
  await page.keyboard.type('find E00003');
  await expect(read).toContainText('The keyboard goes to ABC-LE-01-E00003.');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText('The keyboard is on ABC-LE-01-E00003 in trades (existing).');
  await expect(trade(page, 'ABC-LE-01-E00003')).toBeInViewport();
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: trades (existing), 1 selected, ABC-LE-01-E00003 · csa');

  // A row of the other grid is said to be there, and an id that names none is no row: the line stays, and nothing moves.
  await page.keyboard.type('find DRAFT-1');
  await page.keyboard.press('Enter');
  await expect(said).toHaveText('DRAFT-1 is no row of the grid on screen, trades (existing): it is one of trades (new).');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await command(page, 'find', 'E99999');
  await expect(said).toHaveText('No row E99999 in trades (existing).');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);

  // A grid whose page holds every row: the CSA grid, where the row is found by its key.
  await command(page, 'open csa');
  await settled(page, 'CSA terms');
  await command(page, 'find', 'CSA-B threshold');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText('The keyboard is on CSA-B · threshold in csas.');
  await expect(keyboardIn(page, 'CSA terms')).toHaveCount(1);
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: csas, 1 selected, CSA-B · threshold');
});

test('ADR-0016 §9, note 23: on the CSA grid rows said by `where` alone are CSAs, a subset\'s word is still the trades, and `undo` names the table the line left out', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');

  await command(page, 'open csa');
  await settled(page, 'CSA terms');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('command-context')).toContainText('On screen: csas');
  // `existing` is a subset of the trades alone: with the CSA grid behind the line it is still theirs.
  await page.keyboard.type('set existing a off');
  await expect(read).toContainText('trades, 2 rows: ABC-LE-01-E00003, ABC-LE-01-E00004');
  await line.fill('set where ccy=USD threshold 0');
  await expect(read).toContainText('csas, 1 row: CSA-B');
  await expect(read).toContainText('threshold: 1mm to 0');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Set CSA-B threshold to 0.');
  await expect(page.getByLabel('CSA terms').getByRole('row', { name: /^No CSA-B / }).locator('[aria-colindex="11"]')).toHaveText('0');

  // The log reads as a line that would do the same from anywhere: it names the table the screen gave.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('undo');
  await expect(read).toContainText(/^Takes back: set csas where ccy=USD threshold 0 \(you, 1 edit\) · \d/);
});

test('ADR-0016 §9, note 24: Ctrl+Enter runs the line and keeps it open, saying each line that ran and each notice since in their order; each chained command is a new opening, a refused one stays with its reason, and the line closes on the place a chained `go` put the keyboard in', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const said = saidIn(page);
  const screen = page.getByTestId('command-context');
  const summary = page.getByTestId('workspace-summary');

  // Three new trades selected by keyboard, and two commands with no key between them but their own.
  await focusCell(page, CSA);
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('command-said')).toHaveCount(0);
  await page.keyboard.type('set selected a off');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› set selected a off', 'Set A to off on DRAFT-1, DRAFT-2 and DRAFT-3.']);
  await expect(said.nth(0)).toHaveAttribute('data-kind', 'ran');
  await expect(said.nth(1)).toHaveAttribute('data-kind', 'done');
  await expect(line).toBeFocused();
  await expect(line).toHaveValue('');
  await expect(summary).toContainText('3 edit(s)');
  // The line is as one just opened: it lists the commands, and the grid's selection is still what `selected` means.
  await expect(page.getByTestId('command-heading').first()).toHaveText('XVA ABC/LE-01');
  await expect(screen).toHaveText('On screen: trades (new), 3 selected, DRAFT-1 · csa');
  await page.keyboard.type('undo');
  await page.keyboard.press('ControlOrMeta+Enter');
  const undone = ['› undo', 'Took back: set trades DRAFT-1, DRAFT-2, DRAFT-3 a off. redo puts it back.'];
  await expect(said).toHaveText(['› set selected a off', 'Set A to off on DRAFT-1, DRAFT-2 and DRAFT-3.', ...undone]);
  await expect(summary).toContainText('0 edit(s)');

  // A line the server cannot read ran nothing: it stays with its text and the reason, its word at fault selected, and
  // the line says no more than it did.
  await page.keyboard.type('set selected nothing 1');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(page.getByTestId('command-problems')).toContainText('trades has no field nothing; its fields are trade id, ');
  await expect(line).toHaveValue('set selected nothing 1');
  await expect(line).toBeFocused();
  expect(await line.evaluate((input: HTMLInputElement) => input.value.slice(input.selectionStart!, input.selectionEnd!))).toBe('nothing');
  await expect(said).toHaveText(['› set selected a off', 'Set A to off on DRAFT-1, DRAFT-2 and DRAFT-3.', ...undone]);
  await expect(summary).toContainText('0 edit(s)');

  // A chained `go` puts the keyboard in its place and the line takes it back: the next line's screen is that grid.
  // The line draws the last of what it said, and counts the rest.
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('go existing');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText([...undone, '› go existing', 'The keyboard is in Existing trades.']);
  await expect(page.getByTestId('command-said-earlier')).toHaveText('2 earlier not shown.');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText('On screen: trades (existing), nothing selected');
  // A chained `find` puts the grid's Focus on its cell, and a field alone is then that cell's row.
  await page.keyboard.type('find E00004 csa');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› go existing', 'The keyboard is in Existing trades.', '› find E00004 csa', 'The keyboard is on ABC-LE-01-E00004 · csa in trades (existing).']);
  await expect(screen).toHaveText('On screen: trades (existing), 1 selected, ABC-LE-01-E00004 · csa');
  await expect(line).toBeFocused();
  await page.keyboard.type('set a off');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› find E00004 csa', 'The keyboard is on ABC-LE-01-E00004 · csa in trades (existing).', '› set a off', 'Set ABC-LE-01-E00004 A to off.']);
  await expect(summary).toContainText('1 edit(s)');

  // Closed, the keyboard is where the chained commands put it, not in the New grid the line was opened from.
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('command-said')).toHaveCount(0);
  await expect(screen).toHaveText('On screen: trades (existing), 1 selected, ABC-LE-01-E00004 · csa');

  // A chained `open <tab>` shows the tab and its grid takes the keyboard: the line has it back, on that grid's screen.
  await page.keyboard.type('open csa');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› open csa', 'The keyboard is in the CSA tab.']);
  await expect(page.getByRole('tab', { name: /^CSA/ })).toHaveAttribute('aria-selected', 'true');
  await expect(line).toBeFocused();
  await expect(screen).toContainText('On screen: csas');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await expect(keyboardIn(page, 'CSA terms')).toHaveCount(1);
});

test('ADR-0016 §9, note 24: while a chained line runs the next is typed and kept, Enter and the chain key run nothing and say why until it has ended, and a chained line refused comes back as typed', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const said = saidIn(page);
  const problems = page.getByTestId('command-problems');
  const summary = page.getByTestId('workspace-summary');

  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set DRAFT-1 a off');
  await expect(page.getByTestId('command-preview')).toContainText('A: on to off');
  // From here the server is slow: what the line asks of it waits until the test lets it go.
  let letGo = (): void => undefined;
  let waits = new Promise<void>((resolve) => { letGo = resolve; });
  await page.route('**/api/commands/**', async (route) => { await waits; await route.continue().catch(() => undefined); });
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› set DRAFT-1 a off is running…']);
  await expect(said.nth(0)).toHaveAttribute('data-kind', 'running');
  await expect(line).toHaveAttribute('aria-busy', 'true');

  // The next line is typed at once and kept whole. Neither key runs it, or anything, before the first has ended.
  await page.keyboard.type('undo');
  await expect(line).toHaveValue('undo');
  await page.keyboard.press('Enter');
  await expect(problems).toHaveText('set DRAFT-1 a off has not ended yet. Press Enter again once it has.');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(problems).toHaveText('set DRAFT-1 a off has not ended yet. Press Enter again once it has.');
  await expect(line).toHaveValue('undo');
  await expect(summary).toContainText('0 edit(s)');
  letGo();
  await expect(said).toHaveText(['› set DRAFT-1 a off', 'Set DRAFT-1 A to off.']);
  await expect(problems).toHaveCount(0);
  await expect(line).not.toHaveAttribute('aria-busy', 'true');
  await expect(line).toHaveValue('undo');
  await expect(line).toBeFocused();
  await expect(summary).toContainText('1 edit(s)');
  // Now it runs, once: the first command is what it takes back.
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› set DRAFT-1 a off', 'Set DRAFT-1 A to off.', '› undo', 'Took back: set DRAFT-1 a off. redo puts it back.']);
  await expect(summary).toContainText('0 edit(s)');

  // A chained line the server refuses ran nothing: it is back on the line as typed with why, its word at fault
  // selected, and what was typed while it ran is gone with it. The line says no more than before.
  waits = new Promise<void>((resolve) => { letGo = resolve; });
  await page.keyboard.type('set DRAFT-1 colour red');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said.last()).toHaveText('› set DRAFT-1 colour red is running…');
  await page.keyboard.type('price');
  await expect(line).toHaveValue('price');
  letGo();
  await expect(problems).toContainText('trades has no field colour; its fields are trade id, ');
  await expect(line).toHaveValue('set DRAFT-1 colour red');
  await expect(line).toBeFocused();
  expect(await line.evaluate((input: HTMLInputElement) => input.value.slice(input.selectionStart!, input.selectionEnd!))).toBe('colour');
  await expect(said).toHaveText(['› set DRAFT-1 a off', 'Set DRAFT-1 A to off.', '› undo', 'Took back: set DRAFT-1 a off. redo puts it back.']);
  await expect(summary).toContainText('0 edit(s)');
});

test('ADR-0016 §9, note 24: a result that arrives later is said in the open line, while an IME composes Enter and the chain key are the IME\'s, and a command that opens a dialog ends the chain', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  const line = page.getByRole('combobox', { name: 'Command' });
  const said = saidIn(page);
  const summary = page.getByTestId('workspace-summary');

  // Price answers after its command has ended: the line, still open, says the result when it arrives, after the line.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('price');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› price', /^Price was accepted as Run \S+\.$/]);
  await expect(said.nth(1)).toHaveAttribute('data-kind', 'done');
  await expect(line).toBeFocused();
  await expect(line).toHaveValue('');

  await page.keyboard.type('set DRAFT-1 a off');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› price', /^Price was accepted as Run \S+\.$/, '› set DRAFT-1 a off', 'Set DRAFT-1 A to off.']);
  await expect(summary).toContainText('1 edit(s)');
  // `re` held in an IME's composition lists the commands it begins: the arrow and Tab are the IME's, so the list's
  // choice and the line's text stand.
  const ime = await page.context().newCDPSession(page);
  await ime.send('Input.imeSetComposition', { text: 're', selectionStart: 2, selectionEnd: 2 });
  await expect(line).toHaveValue('re');
  await expect(line).toHaveAttribute('aria-activedescendant', 'qube-line-option-0');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Tab');
  await expect(line).toHaveAttribute('aria-activedescendant', 'qube-line-option-0');
  await expect(line).toHaveValue('re');
  await expect(line).toBeFocused();
  // `undo` held in the composition: the Enter that would commit it, and the chain key, run nothing. Committed, the
  // chain key runs it once: had either key run it before, this would be refused, or the line closed.
  await ime.send('Input.imeSetComposition', { text: 'undo', selectionStart: 4, selectionEnd: 4 });
  await expect(line).toHaveValue('undo');
  await page.keyboard.press('Enter');
  await page.keyboard.press('ControlOrMeta+Enter');
  await ime.send('Input.insertText', { text: 'undo' });
  await expect(line).toHaveValue('undo');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› set DRAFT-1 a off', 'Set DRAFT-1 A to off.', '› undo', 'Took back: set DRAFT-1 a off. redo puts it back.']);
  await expect(line).toBeFocused();
  await expect(summary).toContainText('0 edit(s)');

  // A command that opens a dialog ends the chain: the line closes, and the dialog has the keyboard.
  await page.keyboard.type('new xva');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(line).toBeHidden();
  await expect(page.getByRole('dialog', { name: 'New XVA workspace' }).getByRole('combobox', { name: 'Counterparty' })).toBeFocused();
});

test('ADR-0016 §9, notes 24, 26: a sorted grid keeps its selection across two chained commands that move no row', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 60);
  const line = page.getByRole('combobox', { name: 'Command' });
  const said = saidIn(page);
  const screen = page.getByTestId('command-context');
  const existing = page.getByLabel('Existing trades', { exact: true });
  const at = (position: number) => existing.locator(`[aria-rowindex="${position + 2}"] [aria-colindex="1"]`);

  // Sorted by notional, largest first, from the column's menu, and its first two rows selected.
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Alt+ArrowDown');
  await page.getByRole('menuitem', { name: 'Sort descending' }).click();
  await expect(existing.getByRole('columnheader', { name: 'Notional' })).toHaveAttribute('aria-sort', 'descending');
  await settled(page, 'Existing trades');
  await expect(at(0)).not.toHaveText('ABC-LE-01-E00003');
  const [first, second] = [await at(0).innerText(), await at(1).innerText()];
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('Shift+ArrowDown');

  // Neither command moves a row of that order, so the order's token stands and the grid keeps what is selected: the
  // second command says the same rows with nothing selected again.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText(`On screen: trades (existing), 2 selected, ${first} · trade id`);
  await page.keyboard.type('set selected a off');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› set selected a off', `Set A to off on ${first} and ${second}.`]);
  await expect(screen).toHaveText(`On screen: trades (existing), 2 selected, ${first} · trade id`);
  await page.keyboard.type('set selected a on');
  await page.keyboard.press('ControlOrMeta+Enter');
  await expect(said).toHaveText(['› set selected a off', `Set A to off on ${first} and ${second}.`, '› set selected a on', `Set A to on on ${first} and ${second}.`]);
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // Once the grid has drawn what the commands left, its order and its selection are as they were.
  await settled(page, 'Existing trades');
  await expect(at(0)).toHaveText(first);
  await expect(at(1)).toHaveText(second);
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText(`On screen: trades (existing), 2 selected, ${first} · trade id`);
});

test('ADR-0016 §4, note 25: a grid\'s Context Menu offers its table\'s actions with their lines, after the grid\'s own commands and before the page\'s, and an item runs its line on the menu\'s Selection and cell and says what it did as a notice', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  // A new workspace starts on its owner's last selection of measures, which decides the grids' columns: the grids are
  // drawn again for these, and take keys once they have settled.
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  await settled(page, 'Existing trades');
  const notice = page.getByTestId('notice');
  const summary = page.getByTestId('workspace-summary');
  const existing = page.getByLabel('Existing trades', { exact: true });
  const actions = ['Reset this cell · reset', 'Take out of After · set selected after off', 'Put into After · set selected after on', 'Take out of Before · set selected before off', 'Put into Before · set selected before on'];

  // Both existing trades selected by keyboard, and the menu by its key: the kernel's action first, then the table's.
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  await expect(page.getByRole('menuitem')).toHaveText(['Copy', 'Copy with headers', ...actions, 'Make case from the selection…']);
  for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('menuitem', { name: 'Take out of After · set selected after off' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText('Set A to off on ABC-LE-01-E00003 and ABC-LE-01-E00004.');
  await expect(notice).toHaveAttribute('data-kind', 'done');
  await expect(summary).toContainText('2 edit(s)');
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);
  // The item ran its line: `undo` names it by its rows, as it names a typed line that says `selected`.
  await command(page, 'undo');
  await expect(notice).toHaveText('Took back: set trades ABC-LE-01-E00003, ABC-LE-01-E00004 after off. redo puts it back.');
  await expect(summary).toContainText('0 edit(s)');

  // A secondary press outside the Selection is on that cell alone (ExGrid): the action is its row's. One that changes
  // nothing there says so.
  await page.keyboard.press('ControlOrMeta+Home');
  // The grid draws its rows again for what `undo` left: the cell is measured once it has.
  await settled(page, 'Existing trades');
  await expect(trade(page, 'ABC-LE-01-E00004').locator('[aria-colindex="1"]')).toBeVisible();
  const second = await trade(page, 'ABC-LE-01-E00004').locator('[aria-colindex="1"]').boundingBox();
  await page.mouse.click(second!.x + 20, second!.y + second!.height / 2, { button: 'right' });
  await page.getByRole('menuitem', { name: 'Take out of Before · set selected before off' }).click();
  await expect(notice).toHaveText('Set ABC-LE-01-E00004 B to off.');
  await page.mouse.click(second!.x + 20, second!.y + second!.height / 2, { button: 'right' });
  await page.getByRole('menuitem', { name: 'Take out of Before · set selected before off' }).click();
  await expect(notice).toHaveText('Nothing changed: ABC-LE-01-E00004 already holds B off.');
  await expect(summary).toContainText('1 edit(s)');

  // `Reset this cell` is the cell the menu was opened on: a new trade's CSA, edited, goes back to its source. The
  // page's own items still end the menu: Vary values… on a cell that is typed, and Make case from the selection…
  // (ADR-0016 note 37) on every row of a trade grid.
  await focusCell(page, CSA);
  await page.keyboard.type('CSA-B');
  await page.keyboard.press('Enter');
  await expect(cell(page, 'DRAFT-1', CSA + 1)).toHaveText('CSA-B');
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  await expect(page.getByRole('menuitem')).toHaveText(['Copy', 'Copy with headers', ...actions, 'Vary values…', 'Make case from the selection…']);
  for (let i = 0; i < 2; i++) await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('menuitem', { name: 'Reset this cell · reset' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText('Reset DRAFT-1 csa.');
  await expect(cell(page, 'DRAFT-1', CSA + 1)).toHaveText('CSA-A');
  await expect(summary).toContainText('1 edit(s)');
  await expect(keyboardIn(page, 'New trades')).toHaveCount(1);

  // A cell of a column that is no field of the table, PV01: the server cannot read the line there, and its reason is
  // a refused notice, with the line.
  for (let i = CSA + 1; i < XVA.pv01; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  for (let i = 0; i < 2; i++) await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('menuitem', { name: 'Reset this cell · reset' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText('reset: reset alone resets the cell the keyboard is on, and that cell is no field of trades: name the rows, or a field.');
  await expect(notice).toHaveAttribute('data-kind', 'refused');
  await expect(summary).toContainText('1 edit(s)');

  // The CSA grid is bound to the CSAs, which declare no action: its menu holds the kernel's own alone before the
  // page's, and that resets the term the menu was opened on.
  const threshold = page.getByLabel('CSA terms').getByRole('row', { name: /^Yes CSA-A / }).locator('[aria-colindex="11"]');
  const toThreshold = async () => {
    await page.keyboard.press('ControlOrMeta+Home');
    await page.keyboard.press('ControlOrMeta+Home');
    for (let i = 1; i < 11; i++) await page.keyboard.press('ArrowRight');
  };
  await command(page, 'open', 'csa');
  await settled(page, 'CSA terms');
  await toThreshold();
  await command(page, 'set', 'threshold 5mm');
  await expect(notice).toHaveText('Set CSA-A threshold to 5mm.');
  await expect(threshold).toHaveText('5,000,000');
  await expect(keyboardIn(page, 'CSA terms')).toHaveCount(1);
  await toThreshold();
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  await expect(page.getByRole('menuitem')).toHaveText(['Copy', 'Copy with headers', 'Reset this cell · reset', 'Vary values…']);
  for (let i = 0; i < 2; i++) await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('menuitem', { name: 'Reset this cell · reset' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText('Reset CSA-A threshold.');
  await expect(threshold).toHaveText('0');
  await expect(summary).toContainText('1 edit(s)');
});

test('docs/03 §5, ADR-0010 §3, ADR-0016 note 28: `add each` says the cases it would make and makes a case per new trade alone, refuses one in Before by name on the line, and Price refuses a set the rows left', async ({ page, problems }) => {
  const refused = 'error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)';
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  const line = page.getByRole('combobox', { name: 'Command' });

  // The reading is a trial: the line says what Enter would make, and nothing is made until then.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('add each');
  await expect(page.getByTestId('command-preview')).toHaveText('Makes 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('notice')).toHaveText('Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 4 cases');
  // B on every new trade after the set was made: Price says which trades left it, and prices nothing. The line reads
  // `price` before it submits anything (ADR-0016 note 33), so the reason is the line's and no request was refused.
  const left = "add each's cases add DRAFT-1, DRAFT-2, DRAFT-3, and DRAFT-1, DRAFT-2, DRAFT-3 are in Before. "
    + 'Run add each again to make the set anew, with B clear on each.';
  await taken(page, 'before', 'all new');
  await command(page, 'price');
  await expect(page.getByTestId('command-problems')).toHaveText(left);
  await expect(line).toHaveValue('price');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  expect(problems).toEqual([]);
  // The button invokes the same command with no line to keep: its refusal is a notice, of a request the server refused.
  await page.getByTestId('price').click();
  await expect(page.getByTestId('notice')).toHaveText(`Price was refused: ${left}`);
  expect(problems.splice(0)).toEqual([refused]);
  // Made again, a candidate in Before is refused by name, never unticked: the server read the line before anything
  // ran, so the reason is the line's, and no request was refused.
  await command(page, 'add each');
  await expect(page.getByTestId('command-problems')).toHaveText('DRAFT-1, DRAFT-2, DRAFT-3 are in Before, and a new trade is priced alone only out of Before. '
    + 'Clear B on them, or A to leave them out, then add each again.');
  await expect(line).toHaveValue('add each');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  await taken(page, 'before', 'none new');
  await taken(page, 'add each');
  await expect(page.getByTestId('notice')).toHaveText('Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3; replaced add DRAFT-1, add DRAFT-2, add DRAFT-3.');
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByRole('table', { name: 'Metrics' }).getByRole('rowheader')).toHaveText(['Base', 'add DRAFT-1', 'add DRAFT-2', 'add DRAFT-3']);
});

test('ADR-0016 note 37, ADR-0010 §3: Add each makes a case per ticked new trade, Make case… makes one case of what the two grids\' selections name beside those made, with a pressed trade left out, a case made already is refused in the dialog, and the Changes tab lists a case\'s trades, keeps one in After and says why one is not left out', async ({ page, problems }) => {
  const refused = 'error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)';
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  await settled(page, 'New trades');
  await settled(page, 'Existing trades');
  const notice = page.getByTestId('notice');
  const price = page.getByTestId('price');
  const fresh = page.getByLabel('New trades', { exact: true });
  const existing = page.getByLabel('Existing trades', { exact: true });
  const dialog = page.getByRole('dialog', { name: 'Make case from the selection' });
  const adds = dialog.getByRole('group', { name: 'Adds together' }).getByRole('button');
  const takesOut = dialog.getByRole('group', { name: 'Takes out of After' }).getByRole('button');
  const alone = 'add DRAFT-1, add DRAFT-2, add DRAFT-3';
  const together = 'add DRAFT-1 + DRAFT-2 without ABC-LE-01-E00003';

  // The button is `add each` as the line types it: a case per new trade with A ticked.
  await page.getByRole('button', { name: 'Add each' }).click();
  await expect(notice).toHaveText(`Made 3 case(s): ${alone}.`);
  await expect(price).toHaveText('Price CVA FVA · 4 cases');

  // Two new trades and one existing trade selected by keyboard, each in its own grid: the dialog lists what the two
  // selections name, as the server read them, and makes nothing until Make case.
  await fresh.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('Shift+ArrowDown');
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.getByRole('button', { name: 'Make case…' }).click();
  await expect(adds).toHaveText(['DRAFT-1', 'DRAFT-2']);
  await expect(takesOut).toHaveText(['ABC-LE-01-E00003']);
  await expect(price).toHaveText('Price CVA FVA · 4 cases');
  await expect(dialog.getByRole('button', { name: 'Make case' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
  // One case more beside those made, which stay the cases they were: the sentence names the one made.
  await expect(notice).toHaveText(`Made 1 case(s): ${together}.`);
  await expect(price).toHaveText('Price CVA FVA · 5 cases');

  // From the Existing grid's Context Menu, with trades pressed to leave them out: what is left is a case already made,
  // which the server refuses by name. The dialog says so and stays, and no case was made.
  await existing.getByRole('grid').focus();
  await page.keyboard.press('Shift+F10');
  await page.getByRole('menuitem', { name: 'Make case from the selection…' }).click();
  await expect(adds).toHaveText(['DRAFT-1', 'DRAFT-2']);
  await adds.filter({ hasText: 'DRAFT-2' }).click();
  await expect(adds.filter({ hasText: 'DRAFT-2' })).toHaveAttribute('aria-pressed', 'false');
  await takesOut.click();
  await dialog.getByRole('button', { name: 'Make case' }).click();
  await expect(dialog.getByTestId('case-refused')).toHaveText('No case was made: The case add DRAFT-1 is made already.');
  expect(problems.splice(0)).toEqual([refused]);
  await expect(price).toHaveText('Price CVA FVA · 5 cases');
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();

  // The Changes tab lists what a case adds and takes out, each trade a button that says what its press does. The one
  // trade a case adds has no button: the case is removed whole. A trade kept in After there is `add each` given its
  // set again with that one change.
  await taken(page, 'open', 'changes');
  const changes = page.getByRole('table', { name: 'Changes' });
  const trades = () => changes.getByRole('button', { name: /^(Leave|Keep) /u }).evaluateAll((buttons) => buttons.map((b) => b.getAttribute('aria-label')));
  await expect.poll(trades).toEqual([`Leave DRAFT-1 out of ${together}`, `Leave DRAFT-2 out of ${together}`, `Keep ABC-LE-01-E00003 in After in ${together}`]);
  await changes.getByRole('button', { name: `Keep ABC-LE-01-E00003 in After in ${together}` }).click();
  await expect(notice).toHaveText(`Made 4 case(s): ${alone}, add DRAFT-1 + DRAFT-2; replaced ${alone}, ${together}.`);
  await expect.poll(trades).toEqual(['Leave DRAFT-1 out of add DRAFT-1 + DRAFT-2', 'Leave DRAFT-2 out of add DRAFT-1 + DRAFT-2']);
  // A trade left out of what a case adds is the same command: what is left here is a case already in the set, which
  // the server refuses by name. The page says so, and the cases are as they were.
  await changes.getByRole('button', { name: 'Leave DRAFT-2 out of add DRAFT-1 + DRAFT-2' }).click();
  await expect(notice).toHaveText('DRAFT-2 was not left out of add DRAFT-1 + DRAFT-2: The iteration DRAFT-1 is given twice.');
  expect(problems.splice(0)).toEqual([refused]);
  await expect.poll(trades).toEqual(['Leave DRAFT-1 out of add DRAFT-1 + DRAFT-2', 'Leave DRAFT-2 out of add DRAFT-1 + DRAFT-2']);
  await expect(price).toHaveText('Price CVA FVA · 5 cases');

  // The case of two trades is priced as one case, beside each trade alone.
  await taken(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByRole('table', { name: 'Metrics' }).getByRole('rowheader')).toHaveText(['Base', 'add DRAFT-1', 'add DRAFT-2', 'add DRAFT-3', 'add DRAFT-1 + DRAFT-2']);
  expect(problems).toEqual([]);
});

test('ADR-0016 note 33: `price` says what it would price before Enter, `refresh` and the Refresh button say what carried and `undo` names it, and `pv01 retry` says that nothing failed', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  const line = page.getByRole('combobox', { name: 'Command' });
  const preview = page.getByTestId('command-preview');
  const notice = page.getByTestId('notice');
  const typing = async (text: string) => {
    await expect(line).toBeHidden();
    await page.keyboard.press('ControlOrMeta+K');
    await expect(line).toBeFocused();
    await page.keyboard.type(text);
  };

  // Price is not tried: the line says the measures, the cases and the markets it would price, and Enter submits one Run.
  await typing('price');
  await expect(preview).toHaveText('Prices CVA and FVA for 1 case, as one Run.');
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText(/^Price was accepted as Run run-/);
  await expect(page.getByTestId('xva-result').getByTestId('xva-result-status')).toHaveText('succeeded');

  // Refresh reads the source again and carries the edit: the line says what it would read, and the answer what carried.
  await taken(page, 'set', 'CSA-A threshold 5mm');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');
  await typing('refresh');
  await expect(preview).toHaveText(/^Reads ABC\/LE-01 again as of \d{4}-\d{2}-\d{2}, and carries your edits onto what it reads\.$/);
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText('Refreshed: 1 edit(s) carried, 0 in conflict, 0 lost.');
  await expect(page.getByTestId('workspace-summary')).toContainText('1 edit(s)');
  await settled(page, 'Existing trades');
  // The log names it by its word, and `undo` takes it back.
  await typing('undo');
  await expect(preview).toContainText(/^Takes back: refresh \(you, /);
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText('Took back: refresh. redo puts it back.');
  // The button is the same command.
  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(notice).toHaveText('Refreshed: 1 edit(s) carried, 0 in conflict, 0 lost.');
  await settled(page, 'New trades');

  // Nothing has failed, so `pv01 retry` says that nothing would be asked, and asks nothing.
  await typing('pv01 retry');
  await expect(preview).toHaveText('Nothing would be asked again: no result has failed.');
  await page.keyboard.press('Enter');
  await expect(notice).toHaveText('No result had failed, so nothing was asked again.');
});

test('ADR-0010 §3: Vary values… on a CSA threshold\'s Context Menu makes three cases, by keyboard; a read-only cell offers none', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
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
  // An amount in whole units, grouped (C48), and red as a loss.
  await expect(cell(page, 'DRAFT-1', XVA.pv01)).toHaveText(/^-\d{1,3}(,\d{3})*$/);
  await expect(cell(page, 'DRAFT-1', XVA.pv01)).toHaveClass(/ex-tone-negative/);

  await focusCell(page, XVA.margin - 1);
  await page.keyboard.type('2');
  await page.keyboard.press('Enter');
  // Margin is typed and differs from Original, which is empty (ADR-0010 §2); PV Adj comes from it. Moving onto PV Adj
  // brings its column into view.
  await expect(cell(page, 'DRAFT-1', XVA.margin)).toHaveClass(/ex-state-modified/);
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('ArrowRight');
  // The note gives PV01 to the cent, which the cell shows in whole units: PV Adj is 2 bp of it, shown as an amount.
  const note = page.getByText(/^From Margin: 2 bp × PV01 -/);
  await expect(note).toBeVisible();
  const pv01 = Number(/PV01 (-[\d,.]+)$/.exec((await note.textContent()) ?? '')?.[1]?.replaceAll(',', ''));
  expect(Number.isFinite(pv01)).toBe(true);
  const pvAdj = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 }).format(Math.round(pv01 * 2 * 100) / 100);
  await expect(cell(page, 'DRAFT-1', XVA.pvAdj)).toHaveText(pvAdj);
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
  await taken(page, 'measures', 'cva fva');
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


test('ADR-0010 §4, ADR-0016 note 32: `scenario` says the scenario it would make and defines it whole, Price prices it beside the base market, and the dock, its line and Copy quote follow the selector', async ({ page }) => {
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');

  // A shift that does not resolve is refused on the line with what it could be, and nothing is made.
  const typed = page.getByRole('combobox', { name: 'Command' });
  await command(page, 'scenario', 'up jpy_ois +10%');
  await expect(page.getByTestId('command-problems')).toContainText('an IR shift is absolute, in bp: write jpy_ois +10bp');
  await expect(typed).toHaveValue('scenario up jpy_ois +10%');
  await page.keyboard.press('Escape');
  await expect(typed).toBeHidden();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
  // The server reads the line as it is typed: what Enter would make is said before it.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(typed).toBeFocused();
  await page.keyboard.type('scenario up jpy_ois +100bp; usd_sofr +100bp; usdjpy -5%');
  await expect(page.getByTestId('command-preview')).toHaveText('Makes the scenario up with 3 shifts: jpy_ois +100bp; usd_sofr +100bp; usdjpy -5%.');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('notice')).toHaveText('Made the scenario up.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case × 2 markets');
  // A scenario is no edit of the portfolio.
  await expect(page.getByTestId('workspace-summary')).toContainText('0 edit(s)');

  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  const selector = result.getByRole('combobox', { name: 'Scenario' });
  await expect(selector.locator('option')).toHaveText(['Base market', 'up']);
  await expect(selector.locator('option:checked')).toHaveText('Base market');
  const after = result.getByRole('table', { name: 'Metrics' }).getByRole('row', { name: /^Base / }).locator('td').nth(1);
  await expect(after).toHaveText(/^-?[\d,]+$/);
  const base = await after.innerText();

  await selector.selectOption({ label: 'up' });
  await expect(selector.locator('option:checked')).toHaveText('up');
  await expect(after).toHaveText(/^-?[\d,]+$/);
  await expect(after).not.toHaveText(base);
  // Collapsed, the line a case is of the scenario in view, as the Metrics are.
  await result.getByRole('button', { name: 'Collapse' }).click();
  await expect(result.getByTestId('xva-result-lines').locator('p')).toHaveText([/^Base · CVA Diff /]);
  const line = await result.getByTestId('xva-result-lines').innerText();
  await selector.selectOption({ label: 'Base market' });
  await expect(result.getByTestId('xva-result-lines')).not.toHaveText(line);

  // Copy quote quotes the market in view, and names a scenario.
  await selector.selectOption({ label: 'up' });
  await result.getByRole('button', { name: 'Copy quote' }).click();
  await expect(page.getByTestId('notice')).toContainText('under scenario up is on the clipboard');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toMatch(/ · Revision \d+ · Scenario up · JPY\n/);
});

test('ADR-0010 §4: a block pasted onto the Scenarios grid makes its shifts, and the include box and Duplicate act on a scenario, by keyboard', async ({ page }) => {
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'cva');
  await command(page, 'open scenarios');
  await expect(page.getByRole('tab', { name: 'Scenarios' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Shifts');
  await expect(page.getByTestId('scenarios-summary')).toHaveText(/^No scenario: Price prices the base market alone/);

  // Copied from a spreadsheet: Scenario, Category, Index, Risk factor, Shift type and Size, the derived two left blank.
  await page.evaluate(() => navigator.clipboard.writeText('up\t\tjpy_ois\tall\t\t+10bp\nup\t\tusdjpy\tspot\t\t-5%\ndown\t\tjpy_ois\t5y\t\t-3bp\n'));
  // `open scenarios` put the keyboard in the grid; its first key only places the Focus (ex-grid ADR-0037).
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+V');
  const shifts = page.getByLabel('Shifts', { exact: true });
  await expect(shifts.getByRole('row', { name: /^up IR jpy_ois all abs \+10bp/ })).toHaveCount(1);
  await expect(shifts.getByRole('row', { name: /^up FX usdjpy spot rel -5%/ })).toHaveCount(1);
  await expect(shifts.getByRole('row', { name: /^down IR jpy_ois 5y abs -3bp/ })).toHaveCount(1);
  await expect(page.getByRole('tab', { name: 'Scenarios (2)' })).toBeVisible();
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case × 3 markets');

  // The include box leaves a scenario out of Price, and keeps its shifts.
  const list = page.getByRole('table', { name: 'Scenarios' });
  await list.getByRole('checkbox', { name: 'Include down' }).focus();
  await page.keyboard.press('Space');
  await expect(page.getByTestId('notice')).toHaveText('Price leaves the scenario down out; its shifts stay.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case × 2 markets');
  await expect(shifts.getByRole('row', { name: /^down IR jpy_ois 5y abs -3bp/ })).toHaveCount(1);

  // Duplicate copies a scenario's shifts under the name typed.
  await list.getByRole('button', { name: 'Duplicate up' }).focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Duplicate up' });
  await expect(dialog.getByLabel('New name')).toBeFocused();
  await expect(dialog.getByLabel('New name')).toHaveValue('up2');
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('up-big');
  await page.keyboard.press('Enter');
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId('notice')).toHaveText('Duplicated up as up-big.');
  await expect(list.getByRole('row').filter({ has: page.getByRole('checkbox', { name: 'Include up-big' }) }).getByRole('cell').nth(2)).toHaveText('2');
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case × 3 markets');

  // Remove all, beside the summary, removes every scenario, as scenarios clear does.
  await page.getByRole('button', { name: 'Remove all' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('notice')).toHaveText('Removed every scenario; Price prices the base market alone.');
  await expect(page.getByTestId('scenarios-summary')).toHaveText(/^No scenario: Price prices the base market alone\./);
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case');
});

test('ADR-0010 §4, UX03: at 390px the Scenarios tab is among the Inputs, the scenario selector in Results, and the page never scrolls sideways', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await command(page, 'scenario', 'up jpy_ois +10bp; usdjpy -5%');
  await expect(page.getByTestId('notice')).toHaveText('Made the scenario up.');
  const sideways = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

  await page.getByText('Inputs', { exact: true }).click();
  await page.getByRole('tab', { name: 'Scenarios (1)' }).click();
  await settled(page, 'Shifts');
  await expect(page.getByRole('table', { name: 'Scenarios' }).getByRole('checkbox', { name: 'Include up' })).toBeVisible();
  // The grid is wider than a phone: it scrolls inside its frame, never the page.
  expect(await sideways()).toBe(0);

  await taken(page, 'measures', 'cva');
  await command(page, 'price');
  await page.getByText('Results', { exact: true }).click();
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  const selector = result.getByRole('combobox', { name: 'Scenario' });
  await expect(selector).toBeVisible();
  await selector.selectOption({ label: 'up' });
  await expect(selector.locator('option:checked')).toHaveText('up');
  expect(await sideways()).toBe(0);
});

test('ADR-0010 §4, ADR-0016 note 32: `scenario cross` says what it makes against the cap as it is typed, then makes the set in one command, and `scenarios clear` with none says so', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'cva');
  const line = page.getByRole('combobox', { name: 'Command' });
  // A workspace with no scenario has none to clear: the line says so and keeps its text (ADR-0016 note 32).
  await command(page, 'scenarios clear');
  await expect(page.getByTestId('command-problems')).toHaveText('The workspace has no scenario to remove.');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // The line drops a key typed before it has the keyboard, so the line is typed once it has it.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('scenario cross jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%');
  await expect(page.getByTestId('command-preview')).toHaveText('Makes 8 scenario(s): jpy_ois -50bp · usdjpy -10%, jpy_ois -50bp, jpy_ois -50bp · usdjpy +10% and 5 more. '
    + 'Price would then price 9 pairs of a case and a market, of 50 at most.');
  await page.keyboard.press('Enter');

  await expect(page.getByTestId('notice')).toHaveText(/^Made 8 scenario\(s\): jpy_ois -50bp · usdjpy -10%, jpy_ois -50bp, /);
  await expect(page.getByRole('tab', { name: 'Scenarios (8)' })).toBeVisible();
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case × 9 markets');

  // A generated name has spaces, and the command line takes it whole.
  await command(page, 'scenario exclude', 'jpy_ois +50bp · usdjpy +10%');
  await expect(page.getByTestId('notice')).toHaveText('Price leaves the scenario jpy_ois +50bp · usdjpy +10% out; its shifts stay.');
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case × 8 markets');
});

test('ADR-0010 §4, C32: `scenario save` keeps every scenario as a set, and `scenario load` offers it and adds it to another workspace', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'cva');
  await command(page, 'scenario', 'up jpy_ois +10bp; usdjpy -5%');
  await command(page, 'scenario', 'down jpy_ois -10bp');
  await command(page, 'scenario exclude', 'down');
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case × 2 markets');

  // Saved again by its owner, a set replaces their own: a repeated run says so.
  await command(page, 'scenario save', 'e2e-rates all');
  await expect(page.getByTestId('notice')).toHaveText(/^Saved 2 scenario\(s\) as the set e2e-rates, for every user(, replacing your e2e-rates)?\.$/);

  await newWorkspace(page, 'XYZ/LE-03', 3);
  await taken(page, 'measures', 'cva');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type('scenario load e2e');
  await expect(page.getByRole('listbox').getByRole('option', { name: /^e2e-rates / })).toContainText('2 scenario(s), for all, saved by');
  await page.keyboard.type('-rates');
  await page.keyboard.press('Enter');

  await expect(page.getByTestId('notice')).toHaveText('Loaded 2 scenario(s) from e2e-rates: up, down.');
  await expect(page.getByRole('tab', { name: 'Scenarios (2)' })).toBeVisible();
  // A scenario is included when it is made, as scenario makes it, whatever the box said where it was saved.
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case × 3 markets');
});

test('ADR-0016 notes 39 to 41: `sort` and `filter` give a trade grid its own sort and the rows its reader answers under the conditions; `shown` is then those rows, a trade edited out of them leaves, and `filter off` or the caption\'s button shows every row again', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 60);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const notice = page.getByTestId('notice');
  const existing = page.getByLabel('Existing trades', { exact: true });
  const filtered = page.getByTestId('grid-filter');
  /** The trade drawn at a position of the Existing grid, from 0; the header is the grid's first row. */
  const at = (position: number) => existing.locator(`[aria-rowindex="${position + 2}"] [aria-colindex="1"]`);

  // With the keyboard in the Existing grid, `sort` is that grid's own sort: its header shows it, and the keyboard is
  // back in the grid once the line has closed.
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('sort by notional desc');
  await expect(read).toHaveText('Sorts trades (existing) by notional, descending.');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText('Sorted trades (existing) by notional, descending.');
  await expect(existing.getByRole('columnheader', { name: 'Notional' })).toHaveAttribute('aria-sort', 'descending');
  await settled(page, 'Existing trades');
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);
  // Its rows are in that order too: the first of them hold notionals that only fall, and not one notional all through.
  const notionalColumn = await existing.getByRole('columnheader', { name: 'Notional' }).getAttribute('aria-colindex');
  const notionals = await Promise.all([0, 1, 2, 3, 4, 5].map(async (position) =>
    Number((await existing.locator(`[aria-rowindex="${position + 2}"] [aria-colindex="${notionalColumn}"]`).innerText()).replaceAll(',', ''))));
  expect(notionals.every((n) => Number.isFinite(n))).toBe(true);
  expect(notionals).toEqual([...notionals].sort((a, b) => b - a));
  expect(notionals[0]).toBeGreaterThan(notionals[5]!);

  // `filter where` says how many rows it leaves before Enter; the grid then holds those rows alone, still sorted, and
  // its caption says so. The New grid is another grid, and keeps its rows.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('filter where ccy=USD');
  await expect(read).toContainText(/trades \(existing\), \d+ of 43 rows: /);
  const usd = Number(/, (\d+) of 43 rows/.exec(await read.innerText())?.[1]);
  expect(usd).toBeGreaterThan(1);
  expect(usd).toBeLessThan(43);
  await expect(read).toContainText(`Shows ${usd} of 43 rows of trades (existing).`);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  // The sentence says the conditions as the server read them; the caption, as the grid holds them, which is as typed.
  await expect(notice).toHaveText(`Showing ${usd} of 43 rows of trades (existing) where ccy = USD.`);
  await expect(filtered).toHaveCount(1);
  await expect(filtered.getByRole('status')).toHaveText(`Filter: ccy=USD · ${usd} of 43`);
  await settled(page, 'Existing trades');
  await expect(existing.getByRole('grid')).toHaveAttribute('aria-rowcount', String(usd + 1));
  await expect(existing.getByRole('columnheader', { name: 'Notional' })).toHaveAttribute('aria-sort', 'descending');
  expect(new Set(await existing.locator('[role=row] [role=gridcell][aria-colindex="3"]').allInnerTexts())).toEqual(new Set(['USD']));

  // `shown` is the rows the filter leaves (ADR-0016 §2), and the line says that the grid is filtered.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('command-context')).toContainText(`On screen: trades (existing), filtered to ${usd}, `);
  await page.keyboard.type('set shown a off');
  await expect(read).toContainText(`trades, ${usd} rows: `);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText(`Set A to off on ${usd} trades.`);

  // New conditions replace the ones before, and are read again as the rows change: a trade edited out of them leaves.
  await taken(page, 'filter', 'where a=on');
  await expect(filtered.getByRole('status')).toHaveText(`Filter: a=on · ${43 - usd} of 43`);
  await settled(page, 'Existing trades');
  const leaving = await at(0).innerText();
  await taken(page, 'set', `${leaving} a off`);
  await expect(filtered.getByRole('status')).toHaveText(`Filter: a=on · ${42 - usd} of 43`);
  await expect(trade(page, leaving)).toHaveCount(0);

  // `filter off` shows every row again, and so does the caption's button, which is the pointer's `filter off`.
  await taken(page, 'filter', 'off');
  await expect(notice).toHaveText('Took the filter off trades (existing).');
  await expect(filtered).toHaveCount(0);
  await expect(existing.getByRole('grid')).toHaveAttribute('aria-rowcount', '44');
  await taken(page, 'filter', 'where ccy=USD');
  await expect(filtered.getByRole('status')).toHaveText(`Filter: ccy=USD · ${usd} of 43`);
  await page.getByRole('button', { name: 'Take the filter off the existing trades' }).click();
  await expect(filtered).toHaveCount(0);
  await expect(existing.getByRole('grid')).toHaveAttribute('aria-rowcount', '44');

  // Conditions the server cannot read keep the line with its reason, and the grid as it was.
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('filter where colour=red');
  await expect(read).toContainText('trades has no field colour; its fields are trade id, ');
  await page.keyboard.press('Enter');
  await expect(line).toBeFocused();
  await expect(filtered).toHaveCount(0);
});

test('ADR-0016 notes 39 to 41: the CSA grid, whose page holds every CSA, shows the CSAs the server says a filter leaves; a line chained on `filter` is read under it at once, a CSA edited out of it leaves, a filter that cannot be read shows no row, and `sort` is the grid\'s own', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const notice = page.getByTestId('notice');
  const csas = page.getByLabel('CSA terms', { exact: true });
  const filtered = page.getByTestId('grid-filter');
  const ids = () => csas.locator('[role=row] [role=gridcell][aria-colindex="2"]');

  await command(page, 'open', 'csa');
  await expect(page.getByRole('tab', { name: /^CSA/ })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'CSA terms');
  await expect(ids()).toHaveText(['CSA-A', 'CSA-B']);

  // The filter, then with the chain key a line of `shown`: it is read under the filter whether or not the page has
  // heard yet which CSAs it leaves.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('filter where ccy=USD');
  await expect(read).toContainText('csas, 1 of 2 rows: CSA-B');
  await page.keyboard.press('ControlOrMeta+Enter');
  await page.keyboard.type('set shown threshold 5mm');
  await expect(read).toContainText('csas, 1 row: CSA-B');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText('Set CSA-B threshold to 5mm.');
  await expect(filtered.getByRole('status')).toHaveText('Filter: ccy=USD · 1 of 2');
  await expect(ids()).toHaveText(['CSA-B']);

  // A CSA edited so that it no longer meets the conditions leaves the grid, and `undo` brings it back.
  await taken(page, 'set', 'CSA-B ccy JPY');
  await expect(filtered.getByRole('status')).toHaveText('Filter: ccy=USD · 0 of 2');
  await expect(ids()).toHaveCount(0);
  await taken(page, 'undo');
  await expect(filtered.getByRole('status')).toHaveText('Filter: ccy=USD · 1 of 2');
  await expect(ids()).toHaveText(['CSA-B']);

  // A CSA an edit brings into the conditions is one `shown` names at once: the server reads the conditions at the
  // revision the line is read at, whatever the grid has drawn. The page's own question is held back here to show it:
  // the grid still draws the one CSA it last heard of, and the line reads two.
  let answer = (): void => { throw new Error('The page asked for the CSAs shown before its question was held back.'); };
  const heldBack = new Promise<void>((resolve) => { answer = resolve; });
  await page.route('**/api/workspaces/*/shown', async (route) => { await heldBack; await route.continue(); });
  await taken(page, 'set', 'CSA-A ccy USD');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set shown threshold 6mm');
  await expect(read).toContainText('csas, 2 rows: CSA-A, CSA-B');
  await expect(ids()).toHaveText(['CSA-B']);
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  answer();
  await expect(filtered.getByRole('status')).toHaveText('Filter: ccy=USD · 2 of 2');
  await expect(ids()).toHaveText(['CSA-A', 'CSA-B']);
  await page.unroute('**/api/workspaces/*/shown');
  await taken(page, 'undo');
  await expect(filtered.getByRole('status')).toHaveText('Filter: ccy=USD · 1 of 2');
  await expect(ids()).toHaveText(['CSA-B']);

  // The button takes the filter off; `sort` is the grid's own sort, which its header shows, and `sort off` takes it off.
  await page.getByRole('button', { name: 'Take the filter off the CSA terms' }).click();
  await expect(filtered).toHaveCount(0);
  await expect(ids()).toHaveText(['CSA-A', 'CSA-B']);
  await csas.getByRole('grid').focus();
  await taken(page, 'sort', 'by threshold desc');
  await expect(notice).toHaveText('Sorted csas by threshold, descending.');
  await expect(ids()).toHaveText(['CSA-B', 'CSA-A']);
  await taken(page, 'sort', 'off');
  await expect(notice).toHaveText('Took the sort off csas.');
  await expect(ids()).toHaveText(['CSA-A', 'CSA-B']);

  // Conditions the server can no longer read, as where a field of theirs has gone: the grid shows no row and says
  // why beside the filter, never every row, and the button still takes the filter off.
  const unread = 'The filter of csas can no longer be read (csas has no field ccy): take it off with filter off.';
  await page.route('**/api/workspaces/*/shown', (route) => route.fulfill({ status: 422, contentType: 'application/json', body: JSON.stringify({ code: 'FilterUnread', message: unread, issues: [] }) }));
  await taken(page, 'filter', 'where ccy=JPY');
  await expect(filtered.getByRole('status')).toHaveText(`Filter: ccy=JPY · No row is shown: ${unread}`);
  await expect(ids()).toHaveCount(0);
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
  await page.unroute('**/api/workspaces/*/shown');
  await page.getByRole('button', { name: 'Take the filter off the CSA terms' }).click();
  await expect(ids()).toHaveText(['CSA-A', 'CSA-B']);
});

test('ADR-0016 notes 43 and 44: `select` places a trade grid\'s Selection on one run of rows and refuses rows that are apart, a field alone then acts on the rows selected, `copy` puts their cells on the clipboard, and `export` saves the grid\'s rows as a CSV file in the grid\'s order', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 60);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const notice = page.getByTestId('notice');
  const screen = page.getByTestId('command-context');
  const existing = page.getByLabel('Existing trades', { exact: true });

  // `where` alone means rows of the whole table, as it does for `set`: a new trade in USD is no row of this grid, and
  // the line says so with the way on. Said of the grid's rows, the USD trades are apart as sent: it says how many runs
  // they are in, and selects none of them.
  await existing.getByRole('grid').focus();
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('select where ccy=USD');
  await expect(read).toHaveText(/^DRAFT-\d+(, DRAFT-\d+)* (is a row|are rows) of trades that the grid on screen, trades \(existing\), does not show: select places the Selection of that grid, and shown says its rows alone\.$/);
  await line.fill('select shown where ccy=USD');
  await expect(read).toHaveText(/^The \d+ rows are in \d+ runs in trades \(existing\), and the line selects rows that are next to one another: filter or sort the grid so that they are, then select shown\.$/);
  const usd = Number(/^The (\d+) rows/.exec(await read.innerText())?.[1]);
  await page.keyboard.press('Enter');
  await expect(line).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // Sorted by currency they are one run: the Selection is placed on it, and the keyboard is back in the grid.
  await taken(page, 'sort', 'by ccy');
  await settled(page, 'Existing trades');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('select shown where ccy=USD');
  await expect(read).toContainText(`Selects ${usd} rows of trades (existing).`);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText(`Selected ${usd} rows of trades (existing).`);
  await expect(keyboardIn(page, 'Existing trades')).toHaveCount(1);

  // The line then says the rows selected, with the Focus on the first of them, and a field alone acts on them (note 18).
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(screen).toHaveText(new RegExp(`^On screen: trades \\(existing\\), ${usd} selected, ABC-LE-01-E\\d+ · trade id$`));
  const first = / selected, (\S+) · trade id$/.exec(await screen.innerText())?.[1];
  await page.keyboard.type('set a off');
  await expect(read).toContainText(`trades, ${usd} rows: ${first}`);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText(`Set A to off on ${usd} trades.`);

  // `copy` with no rows is the Selection, in the grid's order: one line a trade, cells apart by tabs, no header.
  await taken(page, 'copy', 'trade id, ccy');
  await expect(notice).toHaveText(`Copied ${usd} rows of trades, 2 fields: trade id, ccy.`);
  const copied = (await page.evaluate(() => navigator.clipboard.readText())).split('\r\n');
  expect(copied).toHaveLength(usd);
  expect(copied[0]).toBe(`${first}\tUSD`);
  expect(copied.every((cells) => /^ABC-LE-01-E\d+\tUSD$/.test(cells))).toBe(true);

  // `export` with no rows is every row the grid shows, in its order, under a header line, as a file named by the table.
  const saving = page.waitForEvent('download');
  await taken(page, 'export');
  const saved = await saving;
  await expect(notice).toContainText('Exported 43 rows of trades to trades.csv, every field (');
  expect(saved.suggestedFilename()).toBe('trades.csv');
  const bytes = await readFile(await saved.path());
  // The byte order mark by which Excel reads the file as UTF-8, then the lines, each ended by CRLF.
  expect([...bytes.subarray(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
  const written = bytes.subarray(3).toString('utf8').split('\r\n');
  expect(written).toHaveLength(45);
  expect(written[0]).toMatch(/^trade id,/);
  expect(written[44]).toBe('');
  // In the grid's order, which is by currency: the USD trades are one run of the file, as the clipboard had them.
  const order = written.slice(1, 44).map((cells) => cells.split(',')[0]);
  const from = order.indexOf(first);
  expect(from).toBeGreaterThanOrEqual(0);
  expect(order.slice(from, from + usd)).toEqual(copied.map((cells) => cells.split('\t')[0]));
});

test('ADR-0016 notes 43 and 44: in the CSA grid, whose page holds every CSA, `select` places the rows the server names by their ids, and `copy … with headers` puts them on the clipboard under the words that name their fields', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const line = page.getByRole('combobox', { name: 'Command' });
  const notice = page.getByTestId('notice');

  await command(page, 'open', 'csa');
  await expect(page.getByRole('tab', { name: /^CSA/ })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'CSA terms');

  await taken(page, 'select', 'CSA-B, CSA-A');
  await expect(notice).toHaveText('Selected 2 rows of csas.');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  // The grid had no Focus to keep, so it is on the first row in the grid's first column, which is no field's.
  await expect(page.getByTestId('command-context')).toHaveText('On screen: csas, 2 selected, CSA-A');
  await page.keyboard.type('copy csa id, ccy with headers');
  await expect(page.getByTestId('command-preview')).toContainText('Copies 2 rows of csas, 2 fields: csa id, currency, with headers.');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice).toHaveText('Copied 2 rows of csas, 2 fields: csa id, currency, with headers.');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toMatch(/^csa id\tcurrency\r\nCSA-A\t[A-Z]{3}\r\nCSA-B\tUSD$/);
});
