import type { Page } from '@playwright/test';
import { command, expect, open, settled, test } from './fixtures';

// Slice 02 (docs/plans/slice-02-xva-workspace.md): the XVA workspace and the Pricing DB, driven by keyboard.

async function newWorkspace(page: Page, registration: string, trades: number): Promise<void> {
  await command(page, 'new xva');
  const dialog = page.getByRole('dialog', { name: 'New XVA workspace' });
  await expect(dialog.getByRole('combobox', { name: 'Counterparty' })).toBeFocused();
  await page.keyboard.type(registration);
  // The counterparties load as the dialog opens: the option is read once it is listed, never before.
  await expect(page.getByRole('option', { name: new RegExp(`^${registration} `) })).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('combobox', { name: 'Counterparty' })).toHaveValue(new RegExp(`^${registration} `));
  await dialog.getByLabel('Existing trades (synthetic)').fill(String(trades));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: `XVA ${registration}` })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Trades');
}

const trade = (page: Page, id: string) => page.getByLabel('Trades').getByRole('row', { name: new RegExp(`^${id} `) });

/** Puts the grid's Focus on a cell of the first row, `right` columns from the first. */
async function focusCell(page: Page, right: number): Promise<void> {
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next ones move it.
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < right; i++) await page.keyboard.press('ArrowRight');
}

const CSA = 8;

test('docs/03, ADR-0003: a new trade takes the main CSA, a CSA typed in moves its curve, and Price lands in the Pricing DB', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
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
  await expect(page.getByLabel('CVA and FVA (simulated)').getByRole('row', { name: /^Workspace CVA -/ })).toBeVisible();
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
  await page.getByRole('option', { name: /^ABC\/LE-02 / }).click();
  const confirm = page.getByRole('dialog', { name: 'Change counterparty' });
  await expect(confirm).toContainText('drops 1 edit(s)');
  await expect(confirm).toContainText('xva.assignment');
  await confirm.getByRole('button', { name: 'Drop 1 and change' }).click();

  await expect(page.getByTestId('notice')).toContainText('1 edit(s) carried');
  await settled(page, 'Trades');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(3)).toHaveText('7,000,000');
  await expect(trade(page, 'DRAFT-1').getByRole('gridcell').nth(CSA)).toHaveText('CSA-C');
});

test('ADR-0010 §10: `measures fva` leaves CVA out of the Price, and a new workspace starts on that choice', async ({ page }) => {
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

  // The page's own toggle runs the same command: CVA back in.
  await measures.getByRole('button', { name: 'CVA' }).click();
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');

  await newWorkspace(page, 'ABC/LE-02', 0);
  await expect(page.getByTestId('price')).toHaveText('Price CVA FVA · 1 case');
});

test('ADR-0010 §5, §8: raising a CSA threshold past 1mm GBP makes it imperfect and moves its trades into FVA', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 0);
  await expect(page.getByRole('tab', { name: 'CSA' })).toBeVisible();
  await page.getByRole('tab', { name: 'CSA' }).click();
  await settled(page, 'CSA terms');
  const csaA = page.getByLabel('CSA terms').getByRole('row', { name: /^CSA-A / });
  // The grid draws the columns in view: End takes the Focus, and the view, to FVA's column at the far right.
  // ExGrid's viewport takes the press over the cell, as it does a user's; the first key only places the Focus
  // (ex-grid ADR-0037), so End is pressed twice, and stays put the second time.
  await csaA.getByRole('gridcell').first().click({ force: true });
  await page.keyboard.press('End');
  await page.keyboard.press('End');
  await expect(csaA).toContainText('Perfect · CSA-A: daily, MTA+threshold');
  await page.keyboard.press('Home');
  for (let i = 0; i < 8; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.type('200000000');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'CSA ●1' })).toBeVisible();
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('End');
  await expect(csaA).toContainText('Imperfect · CSA-A: counterparty MTA+threshold');

  // DRAFT-1 takes the main CSA, CSA-A, so it is in FVA now, with the reason.
  await focusCell(page, 0);
  await page.keyboard.press('End');
  await expect(trade(page, 'DRAFT-1')).toContainText('In · CSA-A: counterparty MTA+threshold');

  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-trades')).toHaveText('Trades: CVA 0 → 3 · FVA 0 → 3');
});
