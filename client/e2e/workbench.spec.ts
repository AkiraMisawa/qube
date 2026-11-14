import { command, expect, open, settled, test } from './fixtures';

// Slice 01 (docs/plans/slice-01-generic-workbench.md): the generic workbench driven as a user drives it.

test('UX01, CO05: a workflow runs from the palette by keyboard alone, and its row actions open a view and run a workflow', async ({ page }) => {
  await open(page);
  await command(page, 'run station summary');
  // The run dialog's first field holds the keyboard once the dialog is up.
  await expect(page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days')).toBeFocused();
  await page.keyboard.type('5');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByRole('heading', { name: 'Mean by station' })).toBeVisible();

  // A second registered view over the same retained outputs, chosen from the palette.
  await command(page, 'show station summary');
  await settled(page, 'Summary by station');

  // Row action that opens a view over retained data: Show readings (the first action in its cell).
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next one moves it.
  await page.keyboard.press('Home');
  await page.keyboard.press('ControlOrMeta+ArrowRight');
  await page.keyboard.press('Space');
  await page.keyboard.press('Space');
  await expect(page.getByRole('tab', { name: 'Readings' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByLabel('Daily readings')).toHaveAttribute('data-rows', '5');

  // Row action that runs a workflow with a bound parameter: Summarise this station.
  await page.keyboard.press('ControlOrMeta+Alt+ArrowLeft');
  await settled(page, 'Summary by station');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next one moves it.
  await page.keyboard.press('Home');
  await page.keyboard.press('ControlOrMeta+ArrowRight');
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Space');
  await expect(page.getByRole('tab', { name: 'Single station summary' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByText('stationId ST-01')).toBeVisible();
});

test('UX01: a context rebound by keyboard reaches the next run, whose failed item stays unavailable', async ({ page }) => {
  await open(page);
  await command(page, 'set region', 'South');
  await expect(page.getByRole('combobox', { name: 'Region' })).toHaveValue('South');
  await command(page, 'run station summary');
  await expect(page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days')).toBeFocused();
  await page.keyboard.type('3');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-status')).toHaveText('partiallySucceeded');
  await expect(page.getByText(/^region South · asOf/)).toBeVisible();
  await command(page, 'show station summary');
  // The failed station is a row of its own, unavailable, with its figures blank and never zero.
  const row = page.getByLabel('Summary by station').getByRole('row', { name: /^ST-05 / });
  await expect(row.getByRole('gridcell')).toHaveText(['ST-05', 'unavailable', '', '', '', '', 'Synthetic station ST-05 is offline.', /Show readings/]);
});

// ADR-0009, docs/design-guidelines.md §10: a date reads yyyy-MM-dd whatever the browser's locale. The native date input
// it replaced drew 11/11/2026 here, and a year typed into it went into the month.
test('ADR-0009: the As of field reads and takes a date as yyyy-MM-dd, says when its text is not one, and picks one from its calendar', async ({ page }) => {
  await open(page);
  const asOf = page.locator('header').getByLabel('As of');
  await expect(asOf).toHaveValue(/^\d{4}-\d{2}-\d{2}$/);

  // February has no 30th. The field keeps what was typed and is marked, and the context in force is said to stay.
  await asOf.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('2026-02-30');
  await page.keyboard.press('Tab');
  await expect(asOf).toHaveValue('2026-02-30');
  await expect(asOf).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByTestId('context-pending')).toContainText('As of: A date as yyyy-MM-dd is required.');

  await asOf.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('2026-10-27');
  await page.keyboard.press('Tab');
  await expect(asOf).toHaveValue('2026-10-27');
  await expect(page.getByTestId('context-pending')).toBeHidden();

  // The calendar opens on October 2026, the month the field names; a day picked there is written as yyyy-MM-dd.
  await asOf.click();
  await page.getByRole('button', { name: '15 October 2026' }).click();
  await expect(asOf).toHaveValue('2026-10-15');
  await expect(page.getByRole('button', { name: '15 October 2026' })).toBeHidden();

  await command(page, 'run station summary');
  await expect(page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days')).toBeFocused();
  await page.keyboard.type('1');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByText(/^region North · asOf 2026-10-15/)).toBeVisible();
});

test('the palette gives the keyboard back to the grid it was opened from', async ({ page }) => {
  await open(page);
  await command(page, 'run station summary');
  await expect(page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days')).toBeFocused();
  await page.keyboard.type('2');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await settled(page, 'Mean by station');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  const grid = page.getByLabel('Mean by station', { exact: true }).getByRole('grid');
  await expect(grid).toBeFocused();
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(grid).toBeFocused();
});

test('UX03: the theme toggles from the keyboard', async ({ page }) => {
  await open(page);
  const html = page.locator('html');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'dark');
  await command(page, 'toggle theme');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'light');
});

test('UX03: on a phone-width screen the page does not scroll sideways and runs by pointer', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await page.getByRole('button', { name: 'Open command line' }).click();
  await page.getByRole('combobox', { name: 'Command' }).fill('station');
  await page.getByRole('option', { name: /run station summary/ }).click();
  const days = page.getByRole('dialog', { name: 'Station summary' }).getByLabel('Days');
  await days.fill('4');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page.getByTestId('run-status')).toHaveText('succeeded');
  await expect(page.getByRole('heading', { name: 'Mean by station' })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBe(0);
});

test('ADR-0006: Ctrl+N and Ctrl+P move through the command line\'s list as the arrows do, and show panel names a panel', async ({ page }) => {
  await open(page);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await command(page, 'new rates');
  // The dialog's field takes the keyboard as it mounts; Enter before that goes nowhere.
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' }).getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: 'Rates 2026-10-27' })).toHaveAttribute('aria-selected', 'true');
  // The page's grid adds `focus grid` to the list as it is drawn: the list is read once it is there, never across it.
  await settled(page, 'Lines');

  await page.keyboard.press('ControlOrMeta+K');
  // The line takes the keyboard as it mounts: a key typed before that is the grid's, as the fixture's `command` knows.
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type('panel');
  const options = page.getByRole('listbox', { name: 'Commands' }).getByRole('option');
  const highlighted = page.getByRole('listbox', { name: 'Commands' }).getByRole('option', { selected: true });
  const first = await options.nth(0).textContent();
  const second = await options.nth(1).textContent();
  await expect(highlighted).toHaveText(first!);
  await page.keyboard.press('Control+n');
  await expect(highlighted).toHaveText(second!);
  await page.keyboard.press('Control+p');
  await expect(highlighted).toHaveText(first!);
  await page.keyboard.press('Escape');

  await command(page, 'show panel', 'pricing');
  await expect(page.getByRole('tab', { name: 'Pricing DB' })).toHaveAttribute('aria-selected', 'true');
});

test('ADR-0006 note 11: on Windows and Linux the line edits with Emacs keys, Ctrl+A still selects all, its list follows, and Ctrl+Z takes a deletion back', async ({ page }) => {
  // A Mac's Chromium has no Ctrl+A select all, and Playwright sends a Mac's editing commands with its Ctrl keys there.
  test.skip(process.platform === 'darwin', 'the keys of Windows and Linux are driven where the browser is theirs');
  await open(page);
  await page.keyboard.press('ControlOrMeta+K');
  const line = page.getByRole('combobox', { name: 'Command' });
  await expect(line).toBeFocused();
  // A query no command matches until the line's keys take its end off.
  await page.keyboard.type('pricing dbxyz');
  await expect(page.getByText('No command matches.')).toBeVisible();
  const caret = async (at: number) => {
    await expect(line).toHaveJSProperty('selectionStart', at);
    await expect(line).toHaveJSProperty('selectionEnd', at);
  };

  // Ctrl+A stays the field's select all (the owner, 2026-11-14); Ctrl+B from a selection goes to its start.
  await page.keyboard.press('Control+a');
  await expect(line).toHaveJSProperty('selectionStart', 0);
  await expect(line).toHaveJSProperty('selectionEnd', 13);
  await page.keyboard.press('Control+b');
  await caret(0);
  await page.keyboard.press('Control+e');
  await page.keyboard.press('Control+b');
  await page.keyboard.press('Control+b');
  await page.keyboard.press('Control+b');
  await caret(10);
  // Ctrl+K is the line's here, not the chord that opens the line: it deletes to the end, and the list reads what is left.
  await page.keyboard.press('Control+k');
  await expect(line).toHaveValue('pricing db');
  await expect(page.getByRole('listbox', { name: 'Commands' }).getByRole('option', { selected: true })).toContainText('Pricing DB');
  // The deletion was the browser's own edit, so its undo brings the text back, and the line reads it again.
  await page.keyboard.press('Control+z');
  await expect(line).toHaveValue('pricing dbxyz');
  await expect(page.getByText('No command matches.')).toBeVisible();

  // Alt is the line's too, where the browser's menu would have it.
  await page.keyboard.press('Control+e');
  await page.keyboard.press('Alt+b');
  await caret(8);
  await page.keyboard.press('Alt+d');
  await expect(line).toHaveValue('pricing ');
  await page.keyboard.press('Control+h');
  await expect(line).toHaveValue('pricing');
  await page.keyboard.press('Control+u');
  await expect(line).toHaveValue('');
  // The keys were the line's alone: it is still open and still has the keyboard.
  await expect(line).toBeFocused();
});

test('ADR-0006 note 11: on a Mac the Ctrl keys are the text field\'s own, and the chord that opens the line no longer takes Ctrl+K', async ({ page }) => {
  // A Mac's keyboard, as the page reads it. On Linux the field underneath does nothing with Ctrl+E or Ctrl+K; on a Mac
  // it moves to the end and deletes nothing there. Either way the line leaves the text as it was.
  await page.addInitScript(() => Object.defineProperty(Navigator.prototype, 'platform', { get: () => 'MacIntel' }));
  await open(page);
  await page.keyboard.press('ControlOrMeta+K');
  const line = page.getByRole('combobox', { name: 'Command' });
  await expect(line).toBeFocused();
  await page.keyboard.type('open pricing db');
  // Whether a key's default was prevented, read once every listener has had it.
  await page.evaluate(() => {
    const seen: string[] = [];
    (window as unknown as { seen: string[] }).seen = seen;
    window.addEventListener('keydown', (e) => { if (e.ctrlKey && e.key.length === 1) setTimeout(() => seen.push(`${e.key}:${e.defaultPrevented}`)); }, { capture: true });
  });

  await page.keyboard.press('Home');
  await page.keyboard.press('Control+e');
  await page.keyboard.press('Control+k');
  // Neither key was prevented, and the line did nothing with either: they were left to the field.
  await expect.poll(() => page.evaluate(() => (window as unknown as { seen: string[] }).seen)).toEqual(['e:false', 'k:false']);
  await expect(line).toHaveValue('open pricing db');
  await page.keyboard.press('End');
  // Ctrl+U, which a Mac's field lacks, is the line's there too.
  await page.keyboard.press('Control+u');
  await expect(line).toHaveValue('');
  await expect(line).toBeFocused();
});

// ADR-0009: what only a browser shows. A header control a few pixels off its neighbours, or a header taller than the
// space AppShell keeps for it, still passes every other test; both were on screen before ADR-0009.
for (const width of [1440, 992, 768, 390]) {
  test(`ADR-0009, UX03: at ${width}px every header control is one Control Height on a shared row, and the header covers nothing`, async ({ page }) => {
    await page.setViewportSize({ width, height: 700 });
    await open(page);
    const header = page.locator('header');
    const controls = header.locator('button, input:visible');
    // Commands, Region, As of, AI and the account menu.
    await expect(controls).toHaveCount(5);
    const boxes = await controls.evaluateAll((elements) => elements.map((e) => {
      const r = e.getBoundingClientRect();
      return { top: r.top, bottom: r.bottom, right: r.right };
    }));
    // One Control Height: Mantine `xs`, 30px.
    for (const box of boxes) expect(box.bottom - box.top).toBeCloseTo(30, 0);
    // Every control sits on one of at most two rows, its top and bottom exactly those of the row.
    const rows = [...new Set(boxes.map((b) => Math.round(b.top)))];
    expect(rows.length).toBeLessThanOrEqual(width >= 992 ? 1 : 2);
    // Nothing reaches past the right edge, and the main area starts below the header.
    for (const box of boxes) expect(box.right).toBeLessThanOrEqual(width);
    const headerBottom = await header.evaluate((e) => e.getBoundingClientRect().bottom);
    const lowest = Math.max(...boxes.map((b) => b.bottom));
    expect(lowest).toBeLessThanOrEqual(headerBottom);
    const firstLine = await page.getByText('Nothing open yet.').evaluate((e) => e.getBoundingClientRect().top);
    expect(firstLine).toBeGreaterThanOrEqual(headerBottom);
  });
}
