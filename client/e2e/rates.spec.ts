import { readFile } from 'node:fs/promises';
import type { Page } from '@playwright/test';
import { command, expect, open, settled, taken, test } from './fixtures';

// Slice 03 (docs/plans/slice-03-rates-line-pricer.md): a line typed by keyboard, priced live, captured and
// priced once. The host runs with the development settings: the unconfirmed convention proposal and a synthetic
// market, every figure simulated. The page's commands are the server's (ADR-0016 notes 34 to 36): a button runs the
// line a user types, through the page's door.

async function newWorkspace(page: Page): Promise<void> {
  await command(page, 'new rates');
  const dialog = page.getByRole('dialog', { name: 'New Rates workspace' });
  await expect(dialog.getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
}

const lines = (page: Page) => page.getByLabel('Lines', { exact: true });
// The grid's rows: its lines, each named by its line, and the blank rows below them, which hold no name (ADR-0015 note
// of 2026-11-14).
const gridRows = (page: Page) => lines(page).locator('[role=row]:has([role=gridcell])');
const lineRows = (page: Page) => lines(page).getByRole('row', { name: /^L\d+ / });
const lineIds = (page: Page) => lineRows(page).locator('[role=gridcell][aria-colindex="1"]');
const firstLine = (page: Page) => lineRows(page).first();
const notice = (page: Page) => page.getByTestId('notice');
const summary = (page: Page) => page.getByTestId('workspace-summary');
// A line is named L1, L2, … in the order its workspace makes them, and no number is given twice (ADR-0016 note 65), so a
// test reads a line's id from the grid where it needs one, and matches it by its form elsewhere.

/** A cell of a row by its column's position, 0 from the left: off-screen columns are not in the DOM, so never by count. */
const cell = (page: Page, column: number) => firstLine(page).locator(`[role=gridcell][aria-colindex="${column + 1}"]`);

// The inputs first and the result after them (owner, 2026-11-04): line, Flows, trade, two legs, result. Folded, each
// leg is one column (ADR-0015); open, its basic terms and its overrides.
const PRODUCT = 2;
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

// ADR-0009, docs/design-guidelines.md §10: Escape shuts a date field's calendar, not the dialog the field is in (a
// Mantine Modal hears Escape first, on the window); and a toolbar's date whose text is not one moves nothing, in words.
test('ADR-0009: Escape shuts a date field\'s calendar before its dialog, and a valuation date that is not a date is refused in words', async ({ page }) => {
  await open(page);
  await command(page, 'new rates');
  const dialog = page.getByRole('dialog', { name: 'New Rates workspace' });
  const field = dialog.getByLabel('Valuation date');
  await expect(field).toBeFocused();
  await field.click();
  const day = page.getByRole('button', { name: '27 October 2026' });
  await expect(day).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(day).toBeHidden();
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');

  const date = page.getByRole('tabpanel').getByLabel('Valuation date');
  await date.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('2026-02-30');
  await page.keyboard.press('Enter');
  await expect(notice(page)).toHaveAttribute('data-kind', 'refused');
  await expect(notice(page)).toContainText('The valuation date was not moved: 2026-02-30 is not a date as yyyy-MM-dd, so it stays 2026-10-27.');
  await expect(date).toHaveValue('2026-02-30');
  await expect(date).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toBeVisible();
});

test('C40, docs/07: a line typed by keyboard ticks live, is captured and priced once', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  // The button runs `add line irs`, the line a user types (ADR-0016 note 36): the notice is the server's sentence.
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await expect(notice(page)).toHaveText(/^Added L\d+, an empty irs line\.$/);
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

  // The toggle runs `live start`; the page attaches to the subscription the workspace then says is running.
  await page.getByTestId('live-toggle').click();
  await expect(notice(page)).toHaveText('Live is running.');
  await expect(page.getByTestId('live-toggle')).toHaveText('Stop live');
  // The line in hand shows its result above the grid, though the result columns are off screen (owner, 2026-11-04).
  await expect(page.getByTestId('line-result-status')).toHaveText('priced');
  await expect(page.getByTestId('line-result-pv')).toHaveText(/^PV -?[\d,]+ JPY$/);
  // With the legs open, the result columns are scrolled to.
  await goTo(page, OPEN.STATUS);
  await expect(cell(page, OPEN.STATUS)).toHaveText('priced');
  await expect(cell(page, OPEN.PV)).toHaveText(/^-?[\d,]+$/);
  await expect(page.getByTestId('live-frame')).toContainText('simulated');

  // Capture runs `capture` for the frame the page drew, and the Run its answer names opens in a panel of its own.
  await page.getByRole('button', { name: 'Capture' }).click();
  await expect(notice(page)).toHaveText(/^Captured the frame of market \S+ at revision \d+ as Run \S+\.$/);
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByRole('tab', { name: /^Capture / })).toHaveAttribute('aria-selected', 'true');

  // Leaving the page only detached it: back on it, the page attaches to the same live subscription by itself.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).click();
  await settled(page, 'Lines');
  await expect(page.getByTestId('live-toggle')).toHaveText('Stop live');
  await expect(page.getByTestId('live-frame')).toContainText('live');
  // `price` says before Enter what it would price and on which market, and Enter prices on that one.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type('price');
  await expect(page.getByTestId('command-preview')).toHaveText(/^Prices 1 line on market \S+, as one Run\.$/);
  await page.keyboard.press('Enter');
  await expect(notice(page)).toHaveText(/^Price was accepted as Run \S+\.$/);
  // Its Run opens in a panel titled by the market it priced on, beside the workspace's own, which is titled by its date.
  await expect(page.getByRole('tab', { name: /^Rates \d+(?![\d-])/ })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  // The Run's view is a grid titled Lines too; only the Run's tab panel is mounted now.
  await expect(page.getByRole('tabpanel').getByLabel('Lines', { exact: true }).getByRole('row', { name: /^L\d+ priced / })).toBeVisible();

  // `live stop` from the line stops it for every page watching: the toggle offers to start again, and the frame stays.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).click();
  await settled(page, 'Lines');
  await taken(page, 'live stop');
  await expect(notice(page)).toHaveText('Live stopped, for every page watching it.');
  await expect(page.getByTestId('live-toggle')).toHaveText('Resume live');
  await expect(page.getByTestId('live-stopped')).toContainText('live stopped');
  await expect(page.getByTestId('live-frame')).toContainText('last frame');
  // The toggle is the same two lines: it starts live again, and stops it.
  await page.getByTestId('live-toggle').click();
  await expect(notice(page)).toHaveText('Live is running.');
  await expect(page.getByTestId('live-toggle')).toHaveText('Stop live');
  await page.getByTestId('live-toggle').click();
  await expect(notice(page)).toHaveText('Live stopped, for every page watching it.');
  await expect(page.getByTestId('live-toggle')).toHaveText('Resume live');
});

test('docs/07 §3, ADR-0016 note 36, ADR-0010 §6\'s note of 2026-11-21: the Price button cannot be pressed until the market is read, then prices on the market the page shows, though a later one is published; Add CCS runs `add line ccs`', async ({ page }) => {
  await open(page);
  // The page's first reading of the market waits until the test lets it through; then it is held at the first it
  // shows, while the server's market moves on.
  let release: (() => void) | undefined;
  const read = new Promise<void>((resolve) => { release = resolve; });
  let held: { readonly snapshotId: string } | undefined;
  await page.route('**/api/rates/market', async (route) => {
    await read;
    held ??= await (await route.fetch()).json() as { readonly snapshotId: string };
    await route.fulfill({ json: held });
  });
  await newWorkspace(page);
  // Price is not pressed while the market it prices on is still loading (the owner, 2026-11-21), and says why.
  const price = page.getByRole('button', { name: 'Price' });
  await expect(page.getByTestId('market')).toHaveText('no market yet');
  await expect(price).toBeDisabled();
  await expect(price).toHaveAttribute('title', 'The market is still loading; Price waits for it.');
  if (release === undefined) throw new Error('The market read was never held.');
  release();
  await expect(price).toBeEnabled();
  await taken(page, 'new', 'irs jpy 10mm 5y pay');
  await expect(summary(page)).toContainText('1 line(s)');
  await expect(page.getByTestId('market')).toHaveText(/^market \S+ #\d+$/);
  if (held === undefined) throw new Error('The page shows a market it never read.');
  const shown = held.snapshotId;
  // A later snapshot is published: the latest is no longer the one the page shows.
  await expect.poll(async () => (await (await page.request.get(new URL('/api/rates/market', page.url()).href)).json() as { snapshotId: string }).snapshotId).not.toBe(shown);

  await price.click();
  await expect(notice(page)).toHaveText(/^Price was accepted as Run \S+\.$/);
  // The Run's panel is titled by the market it priced on: the one the page showed.
  // A strip's tab is named by its title and its close button, an area's by its title alone (ADR-0018 §1).
  await expect(page.getByRole('tab', { name: new RegExp(`^Rates ${shown.slice(shown.lastIndexOf(':') + 1)}(?: |$)`) })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');

  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).click();
  await settled(page, 'Lines');
  await page.getByRole('button', { name: 'Add CCS' }).click();
  await expect(notice(page)).toHaveText(/^Added L\d+, an empty ccs line\.$/);
  await expect(summary(page)).toContainText('2 line(s)');
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

  // While they are open, the cashflows are a place of the page beside its lines (ADR-0016 note 20): `go` puts the
  // keyboard in either, and says where it went.
  const id = await firstLine(page).locator('[role=gridcell][aria-colindex="1"]').innerText();
  await taken(page, 'go', 'flows');
  await expect(notice(page)).toHaveText(`The keyboard is in the cashflows of ${id}.`);
  await expect(page.getByLabel(`Cashflows of ${id}`, { exact: true }).locator(':focus')).toHaveCount(1);
  await taken(page, 'go', 'lines');
  await expect(notice(page)).toHaveText('The keyboard is in the lines.');
  await expect(lines(page).locator(':focus')).toHaveCount(1);
});

test('ADR-0015: at 1280px every column up to the par figure is in view without scrolling, the legs folded', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await open(page);
  await newWorkspace(page);
  // The widest summaries a line makes: an off-market IRS, and a CCS whose legs differ in currency and notional.
  const made = ['irs usd 500mm 5y pay 4.1%', 'ccs usd jpy 100mm 15bn 7y pay freq1=3m freq2=3m index1=sofr index2=tona spread2=0bp'];
  for (const [i, terms] of made.entries()) {
    await taken(page, 'new', terms);
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

test('ADR-0008, ADR-0016 note 36: a line is made from short words in any order, read by the server, which says what it would make before Enter and why it cannot', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const options = page.getByRole('listbox');
  const read = page.getByTestId('command-preview');

  // `/` opens the line from outside a text field or grid; `new` offers the products this workspace makes.
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).focus();
  await page.keyboard.press('/');
  await expect(line).toBeFocused();
  await page.keyboard.type('new i');
  await expect(options.getByRole('option')).toHaveText([/^irs/]);
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('new irs ');

  // Each word is completed from the description: a currency the server's profiles name, taken with Tab.
  await page.keyboard.type('jp');
  // The list is of this text once the server's reading of it is in: until then it says it is busy, and Tab takes
  // nothing from a list of an earlier text.
  await expect(options).not.toHaveAttribute('aria-busy');
  await expect(options.getByRole('option').first()).toContainText('JPY');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('new irs JPY ');

  // The rest in any order; the line shows how each word was read, and the line Enter would make. The index and the
  // frequencies come from the JPY profile's template and the quotes from C47 (at the money), so nothing more is typed.
  await page.keyboard.type('3bn 10y rec');
  const pending = page.getByTestId('command-pending');
  await expect(pending).toContainText('product irs');
  await expect(pending).toContainText('notional 3bn');
  await expect(pending).toContainText('end 10y');
  await expect(pending).toContainText('direction receive');
  await expect(read).toContainText(/^Makes L\d+: Interest rate swap\. Not typed: index TONA from /);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice(page)).toHaveText(/^Made L\d+: Interest rate swap\.$/);
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');
  await expect(cell(page, END)).toHaveText('10y');
  // Folded, leg 1 reads its notional in the scale it was typed in (ADR-0015 §1).
  await expect(cell(page, LEG1_SUMMARY)).toHaveText('JPY 3bn Fixed par 12m');
  await settled(page, 'Lines');
  // A line that can be priced has no issues.
  await goTo(page, FOLDED_ISSUES);
  await expect(cell(page, FOLDED_ISSUES)).toHaveText('');

  // What no default supplies is said by name while it is typed, and Enter keeps the line with the same sentence: no
  // line is made, and nothing was sent to be refused (the fixture fails the test on a console error).
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).focus();
  await page.keyboard.press('/');
  await expect(line).toBeFocused();
  await page.keyboard.type('new irs 5y');
  const missing = 'irs still needs currency, index, notional, direction, fixedFrequency and floatFrequency: type them, or set a default for each.';
  await expect(read).toHaveText(missing);
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('command-problems')).toHaveText(missing);
  await expect(line).toHaveValue('new irs 5y');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();
  await expect(page.getByTestId('workspace-summary')).toContainText('1 line(s)');

  // `new rates` is still the command that opens a workspace, though `new` begins it (note 36).
  await command(page, 'new rates');
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' })).toBeVisible();
});

test('ADR-0016 notes 16, 21, 25 and 34: with two lines selected the line says its screen, `set notional 5mm` writes both and the grid\'s undo key takes it back; `find` goes to a line, and the menu removes it', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  for (const [i, terms] of ['irs jpy 10mm 5y pay', 'irs usd 10mm 7y rec'].entries()) {
    await taken(page, 'new', terms);
    await expect(summary(page)).toContainText(`${i + 1} line(s)`);
  }
  await settled(page, 'Lines');
  await expect(lineRows(page)).toHaveCount(2);
  const [FIRST, SECOND] = await lineIds(page).allInnerTexts() as [string, string];
  const leg1 = (id: string) => lines(page).getByRole('row', { name: new RegExp(`^${id} `) }).locator(`[role=gridcell][aria-colindex="${LEG1_SUMMARY + 1}"]`);
  await expect(leg1(FIRST)).toHaveText('JPY 10mm Fixed par 12m');

  // Both lines selected by keyboard, from the first line's id: the line says so before anything is typed.
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('command-context')).toHaveText(`On screen: lines, 2 selected, ${FIRST} · line`);
  // `set` with no rows named writes the rows selected; an IRS's notional is both its legs'.
  await page.keyboard.type('set notional 5mm');
  await expect(page.getByTestId('command-pending')).toContainText(`rows from the screen: ${FIRST}, ${SECOND}`);
  await expect(read).toHaveText(`lines, 2 rows: ${FIRST}, ${SECOND}notional: 2 10mm to 5mm`);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice(page)).toHaveText(`Set notional to 5mm on ${FIRST} and ${SECOND}.`);
  await expect(leg1(FIRST)).toHaveText('JPY 5mm Fixed par 12m');
  await expect(leg1(SECOND)).toContainText('USD 5mm ');

  // The keyboard is back in the grid, whose undo key runs `undo` (§11): both lines are as they were.
  await page.keyboard.press('ControlOrMeta+Z');
  await expect(notice(page)).toHaveText(`Took back: set lines ${FIRST}, ${SECOND} notional 5mm. redo puts it back.`);
  await expect(leg1(FIRST)).toHaveText('JPY 10mm Fixed par 12m');
  await expect(leg1(SECOND)).toContainText('USD 10mm ');

  // `find` puts the Focus on a line by its id, with the keyboard in the grid: the result strip follows the Focus.
  await taken(page, 'find', SECOND);
  await expect(notice(page)).toHaveText(`The keyboard is on ${SECOND} in lines.`);
  await expect(page.getByTestId('line-result')).toContainText(SECOND);

  // The grid's menu holds its table's actions (note 25), each with the line that does the same: Remove line removes
  // the line the menu was opened on, and `undo` puts it back.
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  await expect(page.getByRole('menuitem')).toHaveText(['Copy', 'Copy with headers', 'Reset this cell · reset', 'Remove line · remove selected']);
  await page.getByRole('menuitem', { name: 'Remove line · remove selected' }).click();
  await expect(notice(page)).toHaveText(`Removed ${SECOND}.`);
  await expect(summary(page)).toContainText('1 line(s)');
  await taken(page, 'undo');
  await expect(notice(page)).toHaveText(`Took back: remove lines ${SECOND}. redo puts it back.`);
  await expect(summary(page)).toContainText('2 line(s)');

  // Reset this cell puts the cell under the Focus back to what the line was made with.
  await taken(page, 'set', `${FIRST} end 7y`);
  await expect(cell(page, END)).toHaveText('7y');
  await goTo(page, END);
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem').first()).toBeFocused();
  await page.getByRole('menuitem', { name: 'Reset this cell · reset' }).click();
  await expect(notice(page)).toHaveText(`Reset ${FIRST} end.`);
  await expect(cell(page, END)).toHaveText('5y');
});

test('ADR-0016 note 34, ADR-0017 §3: a default is set alone and a new line takes it, and the page says only the defaults set; a value a default cannot hold is said on the line as the server says it', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');
  const defaults = page.getByRole('tabpanel').getByTestId('line-defaults');
  // A workspace that holds no default says none.
  await expect(defaults).toHaveCount(0);

  // One default named: the others are left as they are, where the old command needed all three.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set default currency jpy');
  await expect(read).toContainText('default currency: empty to JPY');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice(page)).toHaveText('Set workspace default currency to JPY.');
  // The two defaults not set are not said: the server's view leaves them out.
  await expect(defaults).toHaveText('New lines default to JPY set in this workspace.');
  await taken(page, 'set', 'default notional=500 default direction=rec');

  // A value the default cannot hold is refused by the server's own sentence, before Enter and at it: no stack trace.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set default notional 0');
  await expect(read).toHaveText('A default notional is more than 0.');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('command-problems')).toHaveText('A default notional is more than 0.');
  await page.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // A line made from its end alone takes the three defaults, and says so before Enter.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('new irs 5y');
  await expect(read).toContainText('currency JPY from workspace');
  await expect(read).toContainText('notional 500mm from workspace');
  await expect(read).toContainText('direction receive from workspace');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(summary(page)).toContainText('1 line(s)');
  await expect(cell(page, LEG1_SUMMARY)).toHaveText('JPY 500mm Fixed par 12m');
});

test('ExGrid ADR-0016, ADR-0017 §5: a column dragged wider by its header grip keeps its width when its page comes back, after a reload and in another workspace', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  // A column in view at the left: off-screen columns, the result among them, are not in the DOM.
  const product = lines(page).getByRole('columnheader', { name: 'Product', exact: true });
  const width = async () => Math.round((await product.boundingBox())?.width ?? 0);
  const before = (await product.boundingBox())!;

  // The grip is the header's right edge; the width moves on release.
  await page.mouse.move(before.x + before.width - 2, before.y + before.height / 2);
  await page.mouse.down();
  await page.mouse.move(before.x + before.width + 98, before.y + before.height / 2, { steps: 10 });
  await page.mouse.up();
  await expect.poll(width).toBe(Math.round(before.width) + 100);

  // Another tab unmounts the page; the width is the panel's, so it comes back with it.
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await page.getByRole('tab', { name: 'Rates 2026-10-27' }).click();
  await settled(page, 'Lines');
  await expect.poll(width).toBe(Math.round(before.width) + 100);

  // The width is the user's (ADR-0017 §5): a reload, which brings the panels back (ADR-0018 §7), and another workspace
  // find it as it was released.
  await page.reload();
  await settled(page, 'Lines');
  await expect.poll(width).toBe(Math.round(before.width) + 100);
  await command(page, 'close all panels');
  await expect(page.getByText('Nothing open yet.')).toBeVisible();
  await newWorkspace(page);
  await settled(page, 'Lines');
  await expect.poll(width).toBe(Math.round(before.width) + 100);
});

test('ADR-0016 notes 39 to 41: the line grid shows the lines the server says a filter leaves and says so, its button takes the filter off, and a sort its grid does not do is refused on the line', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  for (const [i, terms] of ['irs jpy 10mm 5y pay', 'irs usd 10mm 7y rec'].entries()) {
    await taken(page, 'new', terms);
    await expect(summary(page)).toContainText(`${i + 1} line(s)`);
  }
  await settled(page, 'Lines');
  const ids = () => lineIds(page);
  await expect(ids()).toHaveCount(2);
  const [FIRST, SECOND] = await ids().allInnerTexts() as [string, string];
  const filtered = page.getByTestId('grid-filter');

  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  await taken(page, 'filter', 'where ccy=USD');
  await expect(notice(page)).toHaveText('Showing 1 of 2 rows of lines where currency = USD.');
  await expect(filtered.getByRole('status')).toHaveText('Filter: ccy=USD · 1 of 2');
  await expect(ids()).toHaveText([SECOND]);
  // ADR-0015 note of 2026-11-14: a filtered grid shows the lines the conditions leave and no blank row, and the blank
  // rows are back once the filter is off.
  const rows = gridRows(page);
  await expect(rows).toHaveCount(1);

  await page.getByRole('button', { name: 'Take the filter off the lines' }).click();
  await expect(filtered).toHaveCount(0);
  await expect(ids()).toHaveText([FIRST, SECOND]);
  await expect(rows).toHaveCount(8);

  // The line grid shows a term as typed and sorts by none: the server reads the sort, and the page says why it is not
  // done, on the line.
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await command(page, 'sort', 'by end desc');
  await expect(page.getByTestId('command-problems')).toHaveText('The grid on screen does not sort by end.');
  await expect(ids()).toHaveText([FIRST, SECOND]);
});

test('ADR-0016 notes 43 and 44: the line grid takes a Selection from the line, and `export` saves the lines it shows as a CSV file', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  for (const [i, terms] of ['irs jpy 10mm 5y pay', 'irs usd 10mm 7y rec'].entries()) {
    await taken(page, 'new', terms);
    await expect(summary(page)).toContainText(`${i + 1} line(s)`);
  }
  await settled(page, 'Lines');
  const ids = () => lineIds(page);
  // Both lines are drawn before their ids are read.
  await expect(ids()).toHaveCount(2);
  const [FIRST, SECOND] = await ids().allInnerTexts() as [string, string];

  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  await taken(page, 'select', 'shown');
  await expect(notice(page)).toHaveText('Selected 2 rows of lines.');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await expect(page.getByTestId('command-context')).toContainText(`On screen: lines, 2 selected, ${FIRST} · `);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeHidden();

  const saving = page.waitForEvent('download');
  await taken(page, 'export');
  const saved = await saving;
  await expect(notice(page)).toContainText('Exported 2 rows of lines to lines.csv, every field (');
  expect(saved.suggestedFilename()).toBe('lines.csv');
  const written = (await readFile(await saved.path())).subarray(3).toString('utf8').split('\r\n');
  expect(written.slice(1).map((cells) => cells.split(',')[0])).toEqual([FIRST, SECOND, '']);
});

test('ADR-0018 §5: a Run\'s view stands its two grids side by side in a 1440px panel, and one above the other in a 720px area of the same window', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page);
  await newWorkspace(page);
  await taken(page, 'new', 'irs jpy 10mm 5y pay');
  await page.getByRole('button', { name: 'Price' }).click();
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  const run = page.getByRole('tabpanel').filter({ has: page.getByTestId('run-status') });
  const grid = async (title: string) => {
    const found = await run.getByLabel(title, { exact: true }).boundingBox();
    if (found === null) throw new Error(`The Run's ${title} grid is not drawn.`);
    return found;
  };
  // The Run alone in a 1440px window, its panel as wide: its Lines and its Risk share a row.
  await expect(run.locator('.qube-panel-page')).toHaveAttribute('data-panel-width', '1440');
  await expect.poll(async () => { const [a, b] = [await grid('Lines'), await grid('Risk')]; return b.x > a.x && b.y === a.y; }).toBe(true);
  // In its own area of 720px beside the workspace: the panel's width, not the window's, puts one above the other.
  await command(page, 'split panel', 'right');
  await expect(run.locator('.qube-panel-page')).toHaveAttribute('data-panel-width', '720');
  await expect.poll(async () => { const [a, b] = [await grid('Lines'), await grid('Risk')]; return b.y > a.y && b.x === a.x; }).toBe(true);
});

test('ADR-0016 §8, notes 60 to 64: a sequence is saved from the line and offered by name; its run makes a line and prices it, the Run opens, and undo takes the run\'s line back', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const options = page.getByRole('listbox');
  const read = page.getByTestId('command-preview');
  const workspace = page.getByRole('tab', { name: 'Rates 2026-10-27' });

  // The lines are typed with `then` between them. The reading says each with its number, and that `price` is refused
  // while the workspace has nothing to price: it is read again when the sequence runs. A run of this spec's file
  // repeated finds the sequence saved, and says that it replaces it.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('sequence save ten-year new irs jpy 10y 1bn pay then price');
  await expect(read).toHaveText(new RegExp('^Saves 2 lines as the sequence ten-year, for you alone(, replacing your own of that name)?\\. '
    + 'Line 2 is refused on the workspace as it stands \\(.+\\); it is read again when the sequence runs, on what the lines before it leave\\. '
    + '1\\. `new irs jpy 10y 1bn pay` 2\\. `price`$'));
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice(page)).toHaveText(/^Saved 2 lines as the sequence ten-year, for you alone/);
  // Saving ran none of them.
  await expect(summary(page)).toContainText('0 line(s)');

  // `sequence run` offers what the user can see, and its reading says every line and what the first would do here.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('sequence run ten');
  await expect(options).not.toHaveAttribute('aria-busy');
  await expect(options.getByRole('option').first()).toContainText('ten-year');
  await expect(options.getByRole('option').first()).toContainText('2 lines, for you alone, saved by ');
  await page.keyboard.press('Tab');
  await expect(line).toHaveValue('sequence run ten-year ');
  await expect(read).toHaveText(new RegExp('^Runs ten-year, of 2 lines, each as its own command, until one is refused: 1\\. `new irs jpy 10y 1bn pay` 2\\. `price`\\. '
    + 'Each line after the first is read as it is reached, on what the lines before it leave\\. Line 1, as the workspace stands: Makes L\\d+: Interest rate swap\\.'));
  await page.keyboard.press('Enter');
  await expect(notice(page)).toHaveText(/^Ran ten-year: 2 lines, from revision \d+ to \d+\. Price was accepted as Run \S+\.$/);
  // The page was told what the last line's command answered, as if `price` had been typed (note 64): its Run opens.
  await expect(page.getByRole('tab', { name: /^Rates \d+(?![\d-])/ })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');

  // Back on the workspace the line is there. Each line of the run is a command of its own: undo says that a saved
  // sequence ran the one it would take back, and takes the line away.
  await workspace.click();
  await settled(page, 'Lines');
  await expect(summary(page)).toContainText('1 line(s)');
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('undo');
  await expect(read).toHaveText(/^Takes back: .+ \(a saved sequence for you, \d+ edits?\)/);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice(page)).toHaveText(/^Took back: /);
  await expect(summary(page)).toContainText('0 line(s)');
});

// ADR-0015's note of 2026-11-14 (owner: five to ten blank rows from the start, and more as they fill). Only a browser
// shows the rows the grid draws, and that a line typed into one goes through the grid's editor and the page's door.
test('ADR-0015 note of 2026-11-14: the line grid shows rows to type in from the start, and a product typed in one makes the line', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  const rows = gridRows(page);
  const result = page.getByTestId('line-result');
  const refusal = 'This row holds no line yet: type IRS or CCS under Product to make one.';

  // No line yet, and eight rows to type in.
  await expect(summary(page)).toContainText('0 line(s)');
  await expect(rows).toHaveCount(8);
  await expect(lineRows(page)).toHaveCount(0);

  // A product typed under Product in a blank row, the third here, makes the line: it stands first, and seven stay blank.
  await goTo(page, PRODUCT);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.type('IRS');
  await page.keyboard.press('Enter');
  await expect(notice(page)).toHaveText(/^Added L\d+, an empty irs line\.$/);
  await expect(summary(page)).toContainText('1 line(s)');
  await settled(page, 'Lines');
  await expect(rows).toHaveCount(8);
  await expect(rows.first()).toHaveAccessibleName(/^L\d+ Flows IRS /);
  await expect(lineRows(page)).toHaveCount(1);
  // The keyboard is on a blank row, which has no result: the strip shows the only line's, as with no row selected.
  await expect(result).toHaveAttribute('aria-label', /^Result of L\d+$/);

  // The keyboard stayed on the blank row Enter left it on, below the one typed in: the next product is typed there
  // with no going to it, and its line stands second, above that row.
  await page.keyboard.type('ccs');
  await page.keyboard.press('Tab');
  await expect(summary(page)).toContainText('2 line(s)');
  await expect(rows.nth(1)).toHaveAccessibleName(/^L\d+ Flows CCS /);
  await expect(result).toHaveText('Select a line to see its result here.');

  // Tab left the keyboard on that blank row still, under Start. Anything but a product typed in a blank row is
  // refused where it was typed, and makes nothing.
  await page.keyboard.type('10y');
  await page.keyboard.press('Enter');
  // Said beside the editor, which stays open on what was typed, and to a screen reader.
  await expect(page.getByText(refusal).first()).toBeVisible();
  await expect(page.getByRole('tabpanel').getByRole('status')).toHaveText(refusal);
  await page.keyboard.press('Escape');
  await expect(summary(page)).toContainText('2 line(s)');
  await expect(lineRows(page)).toHaveCount(2);

  // A blank row's menu holds the grid's own items and no command of the table: there is no line to remove.
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menuitem')).toHaveText(['Copy', 'Copy with headers']);
  await page.keyboard.press('Escape');

  // What a product is stays the server's to say: the notice is its sentence, after the line the cell ran.
  await goTo(page, PRODUCT);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.type('swaption');
  await page.keyboard.press('Enter');
  await expect(notice(page)).toHaveAttribute('data-kind', 'refused');
  await expect(notice(page)).toHaveText('add line swaption: swaption is no product a line is added for: irs or ccs.');

  // A blank row has no cashflows: its button says so and opens none.
  await rows.nth(3).getByRole('button', { name: 'Flows' }).click();
  await expect(notice(page)).toHaveText(refusal);
  await expect(page.getByTestId('cashflows')).toHaveCount(0);

  // A blank row is no row of the table: every row selected, a command on the selection reads the two lines.
  await goTo(page, PRODUCT);
  await page.keyboard.press('ControlOrMeta+A');
  const line = page.getByRole('combobox', { name: 'Command' });
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('command-context')).toHaveText(/^On screen: lines, 2 selected, L\d+ · product$/);
  await page.keyboard.type('remove lines selected');
  await expect(page.getByTestId('command-preview')).toContainText(/L\d+/);
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice(page)).toHaveText(/^Removed L\d+ and L\d+\.$/);
  await expect(summary(page)).toContainText('0 line(s)');
  await expect(rows).toHaveCount(8);

  // More as they fill: from the seventh line the grid grows a row a line, so two blank rows always stand below.
  for (let made = 1; made <= 7; made++) {
    await page.getByRole('button', { name: 'Add IRS' }).click();
    await expect(summary(page)).toContainText(`${made} line(s)`);
    await expect(lineRows(page)).toHaveCount(made);
    // The keyboard is put on the first line's End once the grid shows it, to be found there after seven more lines.
    if (made === 1) await goTo(page, END);
  }
  await taken(page, 'add line', 'ccs');
  await expect(lineRows(page)).toHaveCount(8);
  await settled(page, 'Lines');
  await expect(rows).toHaveCount(10);
  // A button's line and the command line's are this page's own too: the Selection is where it was, and a command reads it.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await expect(page.getByTestId('command-context')).toHaveText(/^On screen: lines, 1 selected, L\d+ · end$/);
  await page.keyboard.press('Escape');
});

/**
 * Holds `add line` from the server until the function this answers is called, so a key is typed before the line comes.
 * Once called, every `add line` goes straight through; a later hold stands before an earlier one.
 */
async function holdAddLine(page: Page): Promise<() => void> {
  let release = (): void => undefined;
  const held = new Promise<void>((resolve) => { release = resolve; });
  await page.route('**/api/commands/rates.line.add/**', async (route) => { await held; await route.continue(); });
  return release;
}

// ADR-0015's note of 2026-11-14, point 8. The grid drops a Selection as its rows' order moves, and its source gathers
// a change that comes within 250ms of the one before, so where the keyboard is after a line is made is a browser's to
// show: by the keys typed next, at machine speed, and with the line's answer held where a key must come before it.
test('ADR-0015 note of 2026-11-14: a line made in a blank row keeps the keyboard where its key left it, whether the line comes before the next key or after it', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  const rows = gridRows(page);
  const end = (row: number) => rows.nth(row).locator(`[role=gridcell][aria-colindex="${END + 1}"]`);

  // Two products, one after the other with no wait: Enter leaves the keyboard on the blank row below, where the
  // second is typed, and Tab on the second line's Start. Its End is typed on from there. The second line comes
  // within the source's interval of the first, which is when the Selection was lost on the first build.
  await goTo(page, PRODUCT);
  await page.keyboard.type('irs');
  await page.keyboard.press('Enter');
  await page.keyboard.type('ccs');
  await page.keyboard.press('Tab');
  await expect(summary(page)).toContainText('2 line(s)');
  await expect(rows.nth(1)).toHaveAccessibleName(/^L\d+ Flows CCS /);
  // The grid is given the line and the Selection together, so the row once drawn is the row the keyboard is on.
  await page.keyboard.press('Tab');
  await page.keyboard.type('7y');
  await page.keyboard.press('Enter');
  await expect(end(1)).toHaveText('7y');
  await expect(end(0)).toHaveText('');

  // A product half typed as the line before it comes is not cut in two: the edit stays open in its row, and what
  // it runs is the whole word.
  let comes = await holdAddLine(page);
  await goTo(page, PRODUCT);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.type('irs');
  await page.keyboard.press('Enter');
  await page.keyboard.type('cc');
  comes();
  await expect(summary(page)).toContainText('3 line(s)');
  await expect(rows.nth(2)).toHaveAccessibleName(/^L\d+ Flows IRS /);
  await page.keyboard.type('s');
  await page.keyboard.press('Tab');
  await expect(summary(page)).toContainText('4 line(s)');
  await expect(rows.nth(3)).toHaveAccessibleName(/^L\d+ Flows CCS /);
  await expect(notice(page)).toHaveText(/^Added L\d+, an empty ccs line\.$/);
  await page.keyboard.press('Tab');
  await page.keyboard.type('3y');
  await page.keyboard.press('Enter');
  await expect(end(3)).toHaveText('3y');

  // A term half typed in the blank row as the line takes its place is not written to the line unseen: the grid
  // refuses the commit, the page says so, and the line's cell stays empty.
  comes = await holdAddLine(page);
  await goTo(page, PRODUCT);
  for (let down = 0; down < 4; down++) await page.keyboard.press('ArrowDown');
  await page.keyboard.type('irs');
  await page.keyboard.press('Tab');
  await page.keyboard.press('Tab');
  await page.keyboard.type('5');
  comes();
  await expect(summary(page)).toContainText('5 line(s)');
  await expect(rows.nth(4)).toHaveAccessibleName(/^L\d+ Flows IRS /);
  await page.keyboard.type('y');
  await page.keyboard.press('Enter');
  await expect(notice(page)).toHaveAttribute('data-kind', 'refused');
  await expect(notice(page)).toHaveText('Nothing was written: the row this was typed in is not in the grid now. The edit stays open: press Escape to leave it without writing.');
  await page.keyboard.press('Escape');
  await expect(end(4)).toHaveText('');
});

// The owner's switch of 2026-11-17: a line that asks for risk shows its deltas under the result strip, tenors across and
// curves down, par (per quote) by default and zero (per curve node) on the switch. The market holds the curves and their
// Jacobians (ADR-0020), so the simulated provider returns both.
test('owner 2026-11-17, ADR-0020: a line with risk shows its par deltas by default and its zero deltas on the switch', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  await taken(page, 'new', 'irs jpy 10mm 10y pay');
  await settled(page, 'Lines');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('ControlOrMeta+Home');
  await taken(page, 'set', 'risk true');
  await page.getByTestId('live-toggle').click();
  await expect(page.getByTestId('line-result-status')).toHaveText('priced');

  const risk = page.getByTestId('line-risk');
  await expect(risk).toHaveAttribute('data-kind', 'par');
  const ladder = risk.getByRole('table', { name: /^Par delta of L\d+$/ });
  await expect(ladder.getByRole('rowheader', { name: 'JPY/TONA' })).toBeVisible();
  await expect(ladder.getByRole('columnheader', { name: 'Total' })).toBeVisible();
  // A 10y payer gains as its rates rise; a negative figure is red (C48), a positive one is not.
  await expect(ladder.getByRole('cell', { name: /^Par delta JPY\/TONA 10y: [\d,]+$/ })).not.toHaveAttribute('data-negative');
  const negative = ladder.getByRole('cell', { name: /: -[\d,]+$/ });
  await expect(ladder.locator('td[data-negative]')).toHaveCount(await negative.count());
  if (process.env.QUBE_SCREENS !== undefined) await page.screenshot({ path: `${process.env.QUBE_SCREENS}/rates-par-delta.png` });

  await risk.getByText('Zero', { exact: true }).click();
  await expect(risk).toHaveAttribute('data-kind', 'zero');
  const zero = risk.getByRole('table', { name: /^Zero delta of L\d+$/ });
  await expect(zero.getByRole('rowheader', { name: 'JPY/TONA' })).toBeVisible();
  await expect(zero.getByRole('cell', { name: /^Zero delta JPY\/TONA 10y: [\d,]+$/ })).toBeVisible();
  await expect(risk.getByRole('radio', { name: 'Zero' })).toBeChecked();
  // The switch's highlight slides; a screenshot for a pull request waits for it to settle.
  if (process.env.QUBE_SCREENS !== undefined) { await page.waitForTimeout(500); await page.screenshot({ path: `${process.env.QUBE_SCREENS}/rates-zero-delta.png` }); }
});
