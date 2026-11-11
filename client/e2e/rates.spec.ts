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
const firstLine = (page: Page) => lines(page).getByRole('row', { name: /^L\d+ / }).first();
const notice = (page: Page) => page.getByTestId('notice');
const summary = (page: Page) => page.getByTestId('workspace-summary');
// A line is named by the revision that makes it (`L3` in a workspace just made), so a test reads a line's id from the
// grid where it needs one, and matches it by its form elsewhere.

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
  const rows = lines(page).getByRole('row', { name: /^L\d+ / });
  await expect(rows).toHaveCount(2);
  const [FIRST, SECOND] = await rows.locator('[role=gridcell][aria-colindex="1"]').allInnerTexts() as [string, string];
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
});

test('ADR-0016 note 34: a default is set alone and a new line takes it; a value a default cannot hold is said on the line as the server says it', async ({ page }) => {
  await open(page);
  await newWorkspace(page);
  const line = page.getByRole('combobox', { name: 'Command' });
  const read = page.getByTestId('command-preview');

  // One default named: the others are left as they are, where the old command needed all three.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(line).toBeFocused();
  await page.keyboard.type('set default currency jpy');
  await expect(read).toContainText('default currency: empty to JPY');
  await page.keyboard.press('Enter');
  await expect(line).toBeHidden();
  await expect(notice(page)).toHaveText('Set workspace default currency to JPY.');
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

test('ExGrid ADR-0016: a column dragged wider by its header grip keeps its width when its page comes back', async ({ page }) => {
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
});
