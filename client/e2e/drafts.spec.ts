import { expect, newWorkspace, open, taken, test } from './fixtures';

// ADR-0016 §10, notes 54 to 57 of 2026-11-12: a row's dialog and its draft, through the built client and the host.
// What a line means in a draft is the kernel's, held by its own tests; this flow holds what only a browser shows: the
// button in the grid, the dialog over the page, the line typed over the dialog, and the page after Save.

const TRADE = 'ABC-LE-01-E00004';

test('ADR-0016 note 57: a row\'s edit button opens its dialog; fields and a leg typed on the line are its draft, and Save writes them as one command', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  const summary = page.getByTestId('workspace-summary');
  const row = page.getByLabel('Existing trades').getByRole('row', { name: new RegExp(`^${TRADE} `) });
  await expect(row).toContainText('3,600,000,000');

  // The grid takes its own presses: the button is pressed where it is drawn.
  await row.getByRole('button', { name: 'Edit' }).click({ force: true });
  const pending = page.getByTestId('draft-pending');
  await expect(pending).toHaveText('nothing pending');
  const dialog = page.getByRole('dialog').filter({ has: pending });
  await expect(page.getByTestId('draft-note')).toContainText('Price keeps every value set here in the input it freezes.');

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
  const legs = page.getByTestId('under-xva.legs');
  await expect(legs.getByRole('row', { name: new RegExp(`^${TRADE}/3 `) })).toContainText('new');
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
