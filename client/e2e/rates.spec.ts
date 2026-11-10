import type { Page } from '@playwright/test';
import { command, expect, open, settled, test } from './fixtures';

// Slice 03 (docs/plans/slice-03-rates-line-pricer.md): a line typed by keyboard, priced live, captured and
// priced once. The host runs with the development settings: the unconfirmed convention proposal and a synthetic
// market, every figure simulated.

async function newWorkspace(page: Page): Promise<void> {
  await command(page, 'new rates');
  const dialog = page.getByRole('dialog', { name: 'New Rates workspace' });
  await expect(dialog.getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
}

const lines = (page: Page) => page.getByLabel('Lines', { exact: true });
const firstLine = (page: Page) => lines(page).getByRole('row', { name: /^L\d+ / }).first();

/** A cell of a row by its column's position, 0 from the left: off-screen columns are not in the DOM, so never by count. */
const cell = (page: Page, column: number) => firstLine(page).locator(`[role=gridcell][aria-colindex="${column + 1}"]`);

// The inputs first and the result after them (owner, 2026-11-04): line, Flows, trade, two legs, result. Folded, each
// leg is one column (ADR-0015); open, its basic terms and its overrides.
const END = 4;
const LEG1_SUMMARY = 6;
const FOLDED_ISSUES = 12;
const OPEN = { LEG1_NOTIONAL: 8, LEG2_QUOTE: 19, PV: 24, STATUS: 27 };

/** Moves the Focus to a column of the first line, from the keyboard, so the grid scrolls it into view. */
async function goTo(page: Page, column: number): Promise<void> {
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < column; i++) await page.keyboard.press('ArrowRight');
}

test('C40, docs/07: a line typed by keyboard ticks live, is captured and priced once', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');
  await settled(page, 'Lines');

  // Both legs opened to their basic terms (ADR-0015 §2), then the end and each leg's terms typed cell after cell; Tab
  // commits and moves right, over each leg's overrides too.
  await lines(page).getByRole('button', { name: 'Expand Leg 1' }).click();
  await lines(page).getByRole('button', { name: 'Expand Leg 2' }).click();
  await settled(page, 'Lines');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < END; i++) await page.keyboard.press('ArrowRight');
  const typed: (string | null)[] = ['5y', null, 'JPY', 'TONA', '1000', null, null, '12m', null, null, null,
    'JPY', 'TONA', '1000', 'spread', '0', '12m'];
  for (const text of typed) {
    if (text !== null) await page.keyboard.type(text);
    await page.keyboard.press('Tab');
  }
  await expect(page.getByTestId('workspace-summary')).toContainText('revision');
  await expect(cell(page, OPEN.LEG2_QUOTE)).toHaveText('0');
  await page.keyboard.press('ControlOrMeta+Home');
  await expect(cell(page, END)).toHaveText('5y');
  // A notional is typed and shown in millions (owner, 2026-11-04): 1000 is a billion, and the server priced it so.
  await expect(cell(page, OPEN.LEG1_NOTIONAL)).toHaveText('1,000');

  await page.getByTestId('live-toggle').click();
  // The line in hand shows its result above the grid, though the result columns are off screen (owner, 2026-11-04).
  await expect(page.getByTestId('line-result-status')).toHaveText('priced');
  await expect(page.getByTestId('line-result-pv')).toHaveText(/^PV -?[\d,]+ JPY$/);
  // With the legs open, the result columns are scrolled to.
  await goTo(page, OPEN.STATUS);
  await expect(cell(page, OPEN.STATUS)).toHaveText('priced');
  await expect(cell(page, OPEN.PV)).toHaveText(/^-?[\d,]+$/);
  await expect(page.getByTestId('live-frame')).toContainText('simulated');

  await page.getByRole('button', { name: 'Capture' }).click();
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByRole('tab', { name: /^Capture / })).toHaveAttribute('aria-selected', 'true');

  // Leaving the page only detached it: back on it, the page attaches to the same live subscription by itself.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).click();
  await settled(page, 'Lines');
  await expect(page.getByTestId('live-toggle')).toHaveText('Stop live');
  await expect(page.getByTestId('live-frame')).toContainText('live');
  await command(page, 'price');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  // The Run's view is a grid titled Lines too; only the Run's tab panel is mounted now.
  await expect(page.getByRole('tabpanel').getByLabel('Lines', { exact: true }).getByRole('row', { name: /^L\d+ priced / })).toBeVisible();
});

test('ADR-0015 §2, C43, C39: a leg opens to its terms, then to its conventions, and folds again; a line without terms shows no schedule', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await settled(page, 'Lines');
  // Folded, each leg is one column; its Header Group draws ExGrid's toggle (ADR-0400), and the page answers it.
  await expect(lines(page).getByRole('columnheader', { name: 'Summary' })).toHaveCount(2);
  await expect(lines(page).getByRole('button', { name: 'Expand Leg 1' })).toHaveAttribute('aria-expanded', 'false');
  await lines(page).getByRole('button', { name: 'Expand Leg 1' }).click();
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('columnheader', { name: 'Notional (mm)' })).toBeVisible();

  // From the keyboard: on the leg's overrides, Alt+Shift+↓ opens its conventions, and ↑ folds them, then the leg.
  await goTo(page, 14);
  await expect(cell(page, 14)).toHaveText('Profile');
  await page.keyboard.press('Alt+Shift+ArrowDown');
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('columnheader', { name: 'Day count' })).toBeVisible();
  await page.keyboard.press('Alt+Shift+ArrowUp');
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('columnheader', { name: 'Day count' })).toHaveCount(0);
  await page.keyboard.press('Alt+Shift+ArrowUp');
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('button', { name: 'Expand Leg 1' })).toHaveAttribute('aria-expanded', 'false');

  // A double click on a folded leg opens it (ADR-0015 §1).
  const leg2 = (await cell(page, 7).boundingBox())!;
  await page.mouse.dblclick(leg2.x + leg2.width / 2, leg2.y + leg2.height / 2);
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('button', { name: 'Collapse Leg 2' })).toHaveAttribute('aria-expanded', 'true');

  await firstLine(page).getByRole('button', { name: 'Flows' }).click();
  await expect(page.getByTestId('cashflows')).toContainText('generates no schedule');
});

test('ADR-0015: at 1280px every column up to the par figure is in view without scrolling, the legs folded', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await open(page);
  await newWorkspace(page);
  // The widest summaries a line makes: an off-market IRS, and a CCS whose legs differ in currency and notional.
  const made: readonly (readonly [string, string])[] = [['new irs', 'usd 500mm 5y pay 4.1%'], ['new ccs', 'usd jpy 100mm 15bn 7y pay freq1=3m freq2=3m index1=sofr index2=tona spread2=0bp']];
  for (const [i, [words, args]] of made.entries()) {
    await command(page, words, args);
    await expect(page.getByTestId('workspace-summary')).toContainText(`${i + 1} line(s)`);
  }
  await settled(page, 'Lines');
  await expect(lines(page).getByRole('row', { name: /JPY 15bn TONA \+0% 3m/ })).toBeVisible();

  const fit = await lines(page).locator('.ex-grid').evaluate((root) => {
    const scroller = root.querySelector('.ex-scroller')!;
    const par = [...root.querySelectorAll('[role=columnheader]')].find((h) => h.textContent?.trim() === 'Par %')!;
    return {
      parRight: par.getBoundingClientRect().right,
      viewRight: scroller.getBoundingClientRect().left + scroller.clientWidth,
      scrolled: scroller.scrollLeft,
      pageOverflow: document.documentElement.scrollWidth - window.innerWidth,
    };
  });
  expect(fit.scrolled).toBe(0);
  expect(fit.parRight).toBeLessThanOrEqual(fit.viewRight);
  expect(fit.pageOverflow).toBeLessThanOrEqual(0);
});

test('ADR-0008: a line is made from short words in any order, read against the product the server describes', async ({ page, problems }) => {
  await open(page);
  await newWorkspace(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const options = page.getByRole('listbox');

  // `/` opens the line from outside a text field or grid; the command is found by the start of its words.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).focus();
  await page.keyboard.press('/');
  await expect(line).toBeFocused();
  await page.keyboard.type('ne ir');
  await expect(options.getByRole('option').first()).toContainText('new irs');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('new irs ');

  // Each word is completed from the description: a currency the server's profiles name, taken with Tab.
  await page.keyboard.type('jp');
  await expect(options.getByRole('option').first()).toContainText('jpy');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('new irs jpy ');

  // The rest in any order; the line shows how each word was read. The index and the frequencies come from the
  // JPY profile's template and the quotes from C47 (at the money), so nothing more is typed.
  await page.keyboard.type('3bn 10y rec');
  const pending = page.getByTestId('command-pending');
  await expect(pending).toContainText('notional 3bn');
  await expect(pending).toContainText('end 10y');
  await expect(pending).toContainText('direction rec');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');
  await expect(cell(page, END)).toHaveText('10y');
  // Folded, leg 1 reads its notional in the scale it was typed in (ADR-0015 §1).
  await expect(cell(page, LEG1_SUMMARY)).toHaveText('JPY 3bn Fixed par 12m');
  await settled(page, 'Lines');
  // A line that can be priced has no issues.
  await goTo(page, FOLDED_ISSUES);
  await expect(cell(page, FOLDED_ISSUES)).toHaveText('');

  // What no default supplies is refused by name, and no line is made.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).focus();
  await page.keyboard.press('/');
  await expect(line).toBeFocused();
  await page.keyboard.type('new irs 5y');
  await page.keyboard.press('Enter');
  await expect(page.getByText(/The line was not created: currency is missing/)).toBeVisible();
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');
  // The refusal is the server's 422, which the browser reports; it is the one console message this test expects.
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
});
