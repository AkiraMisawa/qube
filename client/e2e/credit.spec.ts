import type { Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, test, taken } from './fixtures';

// ADR-0010 Delivery step 5 (docs/specs/pricer-credit-risks-profile): the Credit and Settings input tabs, and the Risks and
// Profile result tabs, as only a browser shows them. Every name and figure is synthetic.

const result = (page: Page) => page.getByTestId('xva-result');

/** Prices the workspace and waits for the Run to finish. */
async function price(page: Page): Promise<void> {
  const [shown] = await result(page).getByTestId('xva-result-revision').allInnerTexts();
  await command(page, 'price');
  await expect(result(page).getByTestId('xva-result-status')).toHaveText('succeeded');
  // A second Price shows a Run of a later revision: the figures read are that Run's.
  if (shown !== undefined) await expect(result(page).getByTestId('xva-result-revision')).not.toHaveText(shown);
}

/** The Base's figure in the Metrics column of this label (`CVA After`), once the Run has written it. */
async function figure(page: Page, label: string): Promise<string> {
  const table = result(page).getByRole('table', { name: 'Metrics' });
  await expect(table.getByRole('columnheader', { name: label })).toBeVisible();
  const headers = await table.locator('thead tr').nth(1).locator('th').evaluateAll((ths) => ths.map((t) => t.getAttribute('aria-label')));
  const cell = table.getByRole('row', { name: /^Base / }).locator('td').nth(headers.indexOf(label));
  await expect(cell).toHaveText(/^(-?[\d,]+|refused)$/);
  return cell.innerText();
}

test('ADR-0010 §8\'s note of 2026-11-09: the Credit tab chooses a CDS and edits its spreads, marked, and the Metrics move; none refuses CVA', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva');
  await price(page);
  const priced = await figure(page, 'CVA After');

  // `open credit` puts the keyboard on the CDS type: the counterparty's own single name, found by its reference.
  await command(page, 'open credit');
  await expect(page.getByRole('tab', { name: 'Credit' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('combobox', { name: 'CDS' })).toBeFocused();
  await expect(page.getByRole('combobox', { name: 'CDS' })).toHaveValue('singleName');
  await expect(page.getByRole('combobox', { name: 'Reference' })).toHaveValue('ABCH | USD | SNRFOR | XR14 | ABC Holdings (synthetic)');

  // A spread typed in bp, the grid's unit, is sent and marked, and the tab counts it (ADR-0010 §2).
  await settled(page, 'CDS curve');
  const curve = page.getByLabel('CDS curve').getByRole('row', { name: /^Single name ABCH / });
  const fiveYears = curve.locator('[aria-colindex="11"]');
  // By keyboard, from the CDS type past the reference into the grid. Its first key only places its Focus (ex-grid
  // ADR-0037); End then takes it to the 10Y spread, and two to the left is the 5Y.
  await page.keyboard.press('Tab');
  await expect(page.getByRole('combobox', { name: 'Reference' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('CDS curve').locator(':focus')).toHaveCount(1);
  await page.keyboard.press('End');
  await page.keyboard.press('End');
  await page.keyboard.press('ArrowLeft');
  await page.keyboard.press('ArrowLeft');
  // The Focus brings the 5Y into view: the columns are as wide as their headers read whole, so it may start off screen.
  await expect(fiveYears).toHaveText('52');
  // A spread the server refuses keeps the editor open with its reason at the cell (ExGrid ADR-0380), and the catalogue's
  // spread is still what the cell shows once Escape closes it. The 422 is the one console message this test expects.
  await page.keyboard.type('0');
  await page.keyboard.press('Enter');
  await expect(page.getByLabel('CDS curve').getByRole('status')).toHaveText('A spread is above 0bp and below 10000bp.');
  await expect(page.getByTestId('notice')).toHaveText('CDS 5y was not changed: A spread is above 0bp and below 10000bp.');
  await page.keyboard.press('Escape');
  await expect(fiveYears).toHaveText('52');
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
  await page.keyboard.type('120');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'Credit ●1' })).toBeVisible();
  await expect(fiveYears).toHaveText('120');
  await expect(fiveYears).toHaveClass(/ex-state-modified/);
  // A wider spread is a larger expected loss: the CVA moves once priced again.
  await price(page);
  await expect.poll(() => figure(page, 'CVA After')).not.toBe(priced);

  // `cds none` leaves no curve: the tab says why CVA is refused, and so do the Metrics.
  await taken(page, 'cds', 'none');
  const missing = 'No CDS for ABC/LE-01: choose a single name or a proxy on the Credit tab, or with cds.';
  await expect(page.getByTestId('notice')).toHaveText(missing);
  await expect(page.getByTestId('cds-missing')).toHaveText(missing);
  await expect(page.getByLabel('CDS curve')).toHaveCount(0);
  await price(page);
  expect(await figure(page, 'CVA After')).toBe('refused');

  // A proxy by its keys, from the command line, and the tab shows it.
  await taken(page, 'cds', 'proxy financials asia A');
  await expect(page.getByTestId('notice')).toContainText('The CDS of ABC/LE-01 is the proxy');
  await expect(page.getByRole('combobox', { name: 'CDS' })).toHaveValue('proxy');
  await expect(page.getByRole('combobox', { name: 'Sector' })).toHaveValue('financials');
  await expect(page.getByLabel('CDS curve').getByRole('row', { name: /^Proxy / })).toBeVisible();

  // The tab's own controls choose the same: another sector of the proxy, then a single name found by its words.
  await page.getByRole('combobox', { name: 'Sector' }).selectOption('utilities');
  await expect(page.getByLabel('CDS curve').getByRole('row', { name: /^Proxy PROXY\.UTILITIES\.ASIA\.A / })).toBeVisible();
  // Back to a single name, the reference is the one the counterparty had, until another is chosen.
  await page.getByRole('combobox', { name: 'CDS' }).selectOption('singleName');
  const reference = page.getByRole('combobox', { name: 'Reference' });
  await expect(reference).toHaveValue('ABCH | USD | SNRFOR | XR14 | ABC Holdings (synthetic)');
  await reference.fill('ABCS');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(reference).toHaveValue('ABCS | USD | SNRFOR | XR14 | ABC Securities (synthetic)');
  await expect(page.getByLabel('CDS curve').getByRole('row', { name: /^Single name ABCS / })).toBeVisible();
  await expect(page.getByTestId('cds-missing')).toHaveCount(0);
  expect(problems).toEqual([]);
});

test('ADR-0010 §8: a setting typed in the Settings tab is marked and moves KVA after Price; `set xva` sets one and a bad value is refused', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva kva');
  await price(page);
  const kva = await figure(page, 'KVA After');

  // `open settings` puts the keyboard on the first setting; each says its default and the measures it is for.
  await command(page, 'open settings');
  await expect(page.getByRole('textbox', { name: 'MPOR override' })).toBeFocused();
  const settings = page.getByRole('table', { name: 'XVA settings' });
  await expect(settings.getByRole('row', { name: /^Cost of capital / })).toContainText('10%KVA');
  const coc = page.getByRole('textbox', { name: 'Cost of capital' });
  await coc.fill('15%');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'Settings ●1' })).toBeVisible();
  await expect(coc).toHaveAttribute('data-modified', 'true');
  await expect(coc).toHaveValue('15%');
  // A higher cost of capital charges more for the same capital profile.
  await price(page);
  await expect.poll(async () => Number((await figure(page, 'KVA After')).replaceAll(',', ''))).toBeLessThan(Number(kva.replaceAll(',', '')));

  // `set` names a setting by its row, the module, and its words; the field shows what it set.
  await command(page, 'set', 'xva charge ratio 1.5');
  await expect(page.getByRole('textbox', { name: 'Charge ratio' })).toHaveValue('1.5');
  await expect(page.getByRole('tab', { name: 'Settings ●2' })).toBeVisible();

  // A value out of bounds is the server's to refuse, by name; the field stays as typed and marked until it is put back.
  // Enter sends it, and leaving the field after does not send it again (spine 6): the one 422 is the one console
  // message this test expects.
  const ratio = page.getByRole('textbox', { name: 'Charge ratio' });
  await ratio.fill('20');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Tab');
  await expect(page.getByTestId('notice')).toHaveText('Charge ratio was not changed: A charge ratio is a number above 0 and up to 10.');
  await expect(ratio).toHaveAttribute('aria-invalid', 'true');
  await ratio.focus();
  await price(page);
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
  await ratio.focus();
  await page.keyboard.press('Escape');
  await expect(ratio).toHaveValue('1.5');
  await expect(ratio).not.toHaveAttribute('aria-invalid', 'true');
});

test('the owner\'s Risks of 2026-11-08: one Risks tab for CVA and FVA says why it has no deltas, then shows them once risks are included', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva fva');
  await price(page);
  await command(page, 'open result', 'risks');
  // CVA and FVA both register Risks; the dock draws it once.
  await expect(result(page).getByRole('tablist', { name: 'Results' }).getByRole('tab')).toHaveText(['Metrics', 'Risks', 'Profile']);
  const why = 'Risks were not asked: turn Risks included on in Settings, or set xva risks on, and Price again.';
  await expect(result(page).getByTestId('xva-risks-reason')).toHaveText([why, why]);

  // Risks included, by keyboard: the switch is a setting like any other, frozen by the next Price.
  await command(page, 'open settings');
  const risks = page.getByRole('switch', { name: 'Risks included' });
  await risks.focus();
  await page.keyboard.press('Space');
  await expect(risks).toBeChecked();
  await expect(page.getByRole('tab', { name: 'Settings ●1' })).toBeVisible();
  await price(page);
  await expect(result(page).getByRole('tab', { name: 'Risks' })).toHaveAttribute('aria-selected', 'true');

  // CVA's credit delta a CDS tenor, then its IR and FX deltas, each Before, After and Diff; FVA's beside it.
  const cva = result(page).getByRole('table', { name: 'CVA deltas' });
  await expect(cva.getByRole('row', { name: /^Credit cds 5y \+1bp / })).toBeVisible();
  await expect(cva.getByRole('rowheader')).toHaveText(['cds 6m', 'cds 1y', 'cds 2y', 'cds 3y', 'cds 5y', 'cds 7y', 'cds 10y', 'jpy_ois', 'usd_sofr', 'eur_estr', 'gbpjpy', 'usdjpy', 'eurjpy']);
  // Risk class and Bump are cells and the factor the row's header, so the Diff is the fifth cell.
  await expect(cva.getByRole('row', { name: /^Credit cds 5y / }).locator('td').nth(4)).toHaveText(/^-?[\d,]+$/);
  await expect(result(page).getByRole('table', { name: 'FVA deltas' }).getByRole('rowheader').first()).toHaveText('jpy_ois');
});

test('the owner\'s Profile of 2026-11-08: the Profile tab draws the exposure and the capital profile as a table and a chart', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'measures', 'cva kva');
  await price(page);
  await command(page, 'open result', 'profile');

  const profile = result(page).getByTestId('xva-profile');
  await expect(profile.getByRole('combobox', { name: 'Profile' }).locator('option')).toHaveText(['Exposure', 'Capital']);
  await expect(profile.getByTestId('xva-profile-returned')).toHaveText('Returned: Exposure, Capital · Exposure in JPY');
  // EE at the value date, Before and After, and a chart of the same series drawn by ExChart.
  const exposure = profile.getByRole('table', { name: 'Exposure profile' });
  await expect(exposure.getByRole('rowheader').first()).toHaveText('2026-10-26');
  await expect(exposure.getByRole('columnheader', { name: 'EE After' })).toBeVisible();
  const chart = profile.getByTestId('xva-profile-chart');
  await expect(chart.locator('svg').first()).toBeVisible();
  await expect(chart).toContainText('EE After');
  await expect(chart).toContainText('ENE Before');
  // A series is hidden and shown again from the legend, by keyboard; the table keeps every series.
  const entry = chart.locator('.ex-chart-legend-entry', { hasText: 'EE After' });
  await entry.focus();
  await page.keyboard.press('Enter');
  await expect(entry).toHaveAttribute('aria-pressed', 'false');
  await expect(exposure.getByRole('columnheader', { name: 'EE After' })).toBeVisible();
  // The entry keeps the keyboard across the redraw a toggle makes (ExChart ADR-0271), so a second Enter shows it again.
  await expect(entry).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(entry).toHaveAttribute('aria-pressed', 'true');

  await profile.getByRole('combobox', { name: 'Profile' }).selectOption('Capital');
  const capital = profile.getByRole('table', { name: 'Capital profile' });
  await expect(capital.getByRole('columnheader', { name: 'EAD After' })).toBeVisible();
  await expect(capital.getByRole('columnheader', { name: 'Capital After' })).toBeVisible();
  await expect(chart).toContainText('Capital After');
});

test('ADR-0010 §1, UX03: at 390px the Credit and Settings tabs, the Risks and the Profile chart fit the phone, and the page never scrolls sideways', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'cva fva kva');
  const sideways = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

  await command(page, 'open credit');
  await settled(page, 'CDS curve');
  // Seven tabs wrap onto two rows, and Hide inputs keeps its words beside them.
  await expect(page.getByRole('button', { name: 'Hide inputs' })).toBeInViewport({ ratio: 1 });
  expect(await sideways()).toBe(0);
  await command(page, 'open settings');
  await expect(page.getByRole('table', { name: 'XVA settings' })).toBeVisible();
  expect(await sideways()).toBe(0);

  await command(page, 'set', 'xva risks on');
  await price(page);
  // The deltas stack a measure under the other, each table scrolling inside the dock.
  await command(page, 'open result', 'risks');
  await expect(result(page).getByRole('table', { name: 'CVA deltas' })).toBeVisible();
  await expect(result(page).getByRole('table', { name: 'FVA deltas' })).toBeVisible();
  expect(await sideways()).toBe(0);
  await command(page, 'open result', 'profile');
  await expect(result(page).getByTestId('xva-profile-chart').locator('svg').first()).toBeVisible();
  await expect(result(page).getByRole('table', { name: 'Exposure profile' })).toBeVisible();
  // The table is wider than a phone: it scrolls inside the dock, never the page.
  expect(await sideways()).toBe(0);
});
