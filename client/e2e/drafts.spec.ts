import type { Locator, Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, taken, test } from './fixtures';

// ADR-0016 §10, notes 54 to 57 of 2026-11-12: a row's dialog and its draft, through the built client and the host.
// What a line means in a draft is the kernel's, held by its own tests; this flow holds what only a browser shows: the
// button in the grid, the dialog over the page, the line typed over the dialog, and the page after Save.

const TRADE = 'ABC-LE-01-E00004';
const HELD = `The dialog of ${TRADE} is open: apply writes its draft, and cancel discards it.`;
const NOTE = "The simulated measures do not read a trade's legs or its fields outside the grids yet. Price keeps every value set here in the input it freezes.";

const existing = (page: Page) => page.getByLabel('Existing trades');
const trade = (page: Page) => existing(page).getByRole('row', { name: new RegExp(`^${TRADE} `) });
/** The table of the trade's legs in its dialog, and a leg's row there by its number. */
const legs = (page: Page) => page.getByTestId('under-xva.legs');
const leg = (page: Page, n: number) => legs(page).getByRole('row', { name: new RegExp(`^${TRADE}/${n} `) });

/** A row's cell under the column so headed: the grid numbers its columns, and the header says which is which. */
async function under(grid: Locator, row: Locator, header: string): Promise<Locator> {
  const column = await grid.getByRole('columnheader', { name: header, exact: true }).getAttribute('aria-colindex');
  if (column === null) throw new Error(`The grid has no column headed ${header}.`);
  return row.locator(`[aria-colindex="${column}"]`);
}

/** Presses what a grid draws, where it is drawn: the grid takes its own presses. Brought clear of the dialog's footer first. */
async function press(drawn: Locator): Promise<void> {
  await drawn.evaluate((element) => element.scrollIntoView({ block: 'center' }));
  await drawn.click({ force: true });
}

test('ADR-0016 note 57: a row\'s edit button opens its dialog; fields and a leg typed on the line are its draft, and Save writes them as one command', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const summary = page.getByTestId('workspace-summary');
  const row = trade(page);
  await expect(row).toContainText('3,600,000,000');

  // The grid takes its own presses: the button is pressed where it is drawn.
  await row.getByRole('button', { name: 'Edit' }).click({ force: true });
  const pending = page.getByTestId('draft-pending');
  await expect(pending).toHaveText('nothing pending');
  const dialog = page.getByRole('dialog').filter({ has: pending });
  await expect(page.getByTestId('draft-note')).toHaveText(NOTE);

  // A field changed is a line of the draft, and the workspace is not written.
  const notional = dialog.getByRole('textbox', { name: /^Notional/ });
  await notional.fill('5bn');
  await notional.press('Enter');
  await expect(pending).toHaveText('1 pending');
  await expect(notional).toHaveAccessibleName(/changed/);
  await expect(summary).toContainText('0 edit(s)');

  // A refusal is the server's sentence at the field, and the draft gains nothing.
  const rate = dialog.getByRole('textbox', { name: /^Fixed rate/ });
  const held = await rate.inputValue();
  await rate.fill('high');
  await rate.press('Enter');
  await expect(dialog.getByText(/; high is not\.$/)).toBeVisible();
  await expect(pending).toHaveText('1 pending');
  await rate.fill(held);
  await rate.press('Enter');
  await expect(dialog.getByText(/; high is not\.$/)).toBeHidden();

  // The line is typed over the dialog, and acts on the draft's row: the leg is the trade's, which the line does not name.
  await taken(page, 'new', 'leg eur 250,000');
  await expect(pending).toHaveText('2 pending');
  await expect(leg(page, 3)).toContainText('new');
  await expect(page.getByTestId('draft-lines')).toContainText(`new leg trade=${TRADE} eur 250,000`);
  await expect(summary).toContainText('0 edit(s)');

  // Cancel with lines pending asks first.
  await dialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByRole('alertdialog', { name: 'Discard the draft' })).toContainText('Discard the 2 pending lines?');
  await page.getByRole('button', { name: 'Keep editing' }).click();
  await expect(pending).toHaveText('2 pending');

  // Save writes the draft's lines as one command: the grid behind shows them, and one undo takes them all back.
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(pending).toBeHidden();
  await expect(summary).toContainText('2 edit(s)');
  await expect(row).toContainText('5,000,000,000');
  await taken(page, 'undo');
  await expect(summary).toContainText('0 edit(s)');
  await expect(row).toContainText('3,600,000,000');
});

test('ADR-0016 notes 55 to 57: Space on a row\'s button opens its dialog; a cell typed and a row removed in its grid are lines of the draft; the undo key and the shell\'s chords are held while it is open; Save gives the keyboard back to the grid', async ({ page }) => {
  await open(page);
  // A second panel, so that the chord for the next panel has one to go to.
  await command(page, 'new rates');
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' }).getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await settled(page, 'Lines');
  await newWorkspace(page, 'ABC/LE-01', 5);
  const summary = page.getByTestId('workspace-summary');
  const pending = page.getByTestId('draft-pending');
  const lines = page.getByTestId('draft-lines');

  // The keyboard reaches the button: its cell is the one beside the row's key, and Space presses it.
  await press(trade(page).getByRole('gridcell').first());
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Space');
  await expect(pending).toHaveText('nothing pending');
  const dialog = page.getByRole('dialog').filter({ has: pending });

  // A cell of a leg typed into is a line that names the leg, and the leg then says it changed.
  await press(await under(legs(page), leg(page, 1), 'Notional'));
  await page.keyboard.type('4bn');
  await page.keyboard.press('Enter');
  await expect(pending).toHaveText('1 pending');
  await expect(lines).toContainText(`set legs ${TRADE}/1 notional 4bn`);
  await expect(await under(legs(page), leg(page, 1), 'Notional')).toHaveText('4,000,000,000');
  await expect(await under(legs(page), leg(page, 1), 'Draft')).toHaveText('changed');

  // A leg's Remove is a line too; pressed again on the leg it removed, the server's refusal is said under the grid.
  await press(leg(page, 2).getByRole('button', { name: 'Remove' }));
  await expect(pending).toHaveText('2 pending');
  await expect(lines).toContainText(`remove legs ${TRADE}/2`);
  await expect(await under(legs(page), leg(page, 2), 'Draft')).toHaveText('removed');
  await press(leg(page, 2).getByRole('button', { name: 'Remove' }));
  await expect(legs(page).getByRole('alert')).toHaveText(`No row ${TRADE}/2 in legs: its rows are ${TRADE}/1.`);
  await expect(pending).toHaveText('2 pending');

  // The grid's undo key is the workspace's command, and a draft does not read it: nothing is taken back.
  await press(await under(legs(page), leg(page, 1), 'Notional'));
  await page.keyboard.press('ControlOrMeta+z');
  await expect(page.getByTestId('notice')).toHaveText(HELD);
  // Nor does a chord of the shell change the panel under the draft.
  await page.keyboard.press('ControlOrMeta+Alt+ArrowRight');
  await expect(page.getByTestId('notice').filter({ hasText: 'next panel' })).toHaveText(`next panel: ${HELD}`);
  await expect(page.getByRole('tab', { name: 'XVA ABC/LE-01' })).toHaveAttribute('aria-selected', 'true');
  await expect(pending).toHaveText('2 pending');
  await expect(summary).toContainText('0 edit(s)');

  // Escape asks before two lines are discarded, and Escape again keeps them. The keyboard is in the dialog's grid,
  // which has nothing of its own left to dismiss and hands the key on.
  await page.keyboard.press('Escape');
  await expect(page.getByRole('alertdialog', { name: 'Discard the draft' })).toContainText('Discard the 2 pending lines?');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('alertdialog')).toBeHidden();
  await expect(pending).toHaveText('2 pending');

  // Saved, the grid the dialog stood over has the keyboard again.
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(pending).toBeHidden();
  await expect(summary).toContainText('2 edit(s)');
  await expect(existing(page).locator(':focus')).toHaveCount(1);

  // So it has after a dialog the line opened: the line gave the keyboard to the dialog, and the dialog gives it back.
  await taken(page, 'open', `trades ${TRADE}`);
  await expect(pending).toHaveText('nothing pending');
  await page.keyboard.press('Escape');
  await expect(pending).toBeHidden();
  await expect(existing(page).locator(':focus')).toHaveCount(1);
});

test.describe('on a touch screen', () => {
  test.use({ hasTouch: true });

  test('ADR-0016 note 57, design guidelines §9: on a phone a tap on a row\'s button opens its dialog over the whole screen, with Cancel and Save in view, and Cancel asks before a pending line is discarded', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await open(page);
    await newWorkspace(page, 'ABC/LE-01', 5);
    const pending = page.getByTestId('draft-pending');

    await trade(page).getByRole('button', { name: 'Edit' }).tap({ force: true });
    await expect(pending).toHaveText('nothing pending');
    const dialog = page.getByRole('dialog').filter({ has: pending });
    // Once it has opened it is the screen.
    await expect.poll(() => dialog.boundingBox()).toEqual({ x: 0, y: 0, width: 390, height: 844 });
    await expect(page.getByTestId('draft-note')).toHaveText(NOTE);
    await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeInViewport();
    await expect(dialog.getByRole('button', { name: 'Save' })).toBeInViewport();

    const notional = dialog.getByRole('textbox', { name: /^Notional/ });
    await notional.fill('5bn');
    await notional.press('Enter');
    await expect(pending).toHaveText('1 pending');
    await dialog.getByRole('button', { name: 'Cancel' }).tap();
    const asked = page.getByRole('alertdialog', { name: 'Discard the draft' });
    await expect(asked).toContainText('Discard the pending line?');
    await expect(asked.getByRole('button', { name: 'Discard' })).toBeInViewport();
    await asked.getByRole('button', { name: 'Discard' }).tap();
    await expect(pending).toBeHidden();
    await expect(page.getByTestId('workspace-summary')).toContainText('0 edit(s)');
  });
});

test('ADR-0016 note 58: a scenario opens as a row with its shifts under it; a size typed in a shift\'s cell is a line of the draft that names the shift, a unit the index does not shift by is refused at the cell, and Save writes the draft', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await taken(page, 'scenario', 'up jpy_ois +10bp; usdjpy -5%');
  await command(page, 'open', 'up');
  const pending = page.getByTestId('draft-pending');
  await expect(pending).toHaveText('nothing pending');
  const dialog = page.getByRole('dialog').filter({ has: pending });
  const shifts = page.getByTestId('under-xva.shifts');
  const curve = shifts.getByRole('row', { name: /^up\/\d+ IR / });
  const rate = shifts.getByRole('row', { name: /^up\/\d+ FX / });
  await expect(await under(shifts, curve, 'Size')).toHaveText('10 bp');
  await expect(await under(shifts, rate, 'Size')).toHaveText('-5 %');

  await press(await under(shifts, curve, 'Size'));
  await page.keyboard.type('15bp');
  await page.keyboard.press('Enter');
  await expect(pending).toHaveText('1 pending');
  await expect(page.getByTestId('draft-lines')).toContainText(/set shifts up\/\d+ size 15bp/);
  await expect(await under(shifts, curve, 'Size')).toHaveText('15 bp');
  await expect(await under(shifts, curve, 'Draft')).toHaveText('changed');

  // An FX rate shifts by a percentage. The server refuses basis points by the shift's key, and the grid says so.
  await press(await under(shifts, rate, 'Size'));
  await page.keyboard.type('3bp');
  await page.keyboard.press('Enter');
  await expect(shifts.getByRole('status')).toHaveText(/^up\/\d+, Size: an FX shift is relative, in %: write \+3%\.$/);
  await page.keyboard.press('Escape');
  await expect(await under(shifts, rate, 'Size')).toHaveText('-5 %');
  await expect(pending).toHaveText('1 pending');

  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByTestId('notice')).toHaveText('Applied 1 line to up.');
  await command(page, 'open scenarios');
  await expect(page.getByLabel('Shifts', { exact: true }).getByRole('row', { name: /^up IR jpy_ois all abs \+15bp/ })).toHaveCount(1);
});
