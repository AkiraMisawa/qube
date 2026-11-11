import type { APIRequestContext, Browser, Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, taken, test, watchProblems } from './fixtures';

// ADR-0017: a user's settings live on the server, apart from workspaces, and are changed by the line, the settings
// dialog and the AI through the same three commands.

/** The value the server holds for one of the user's settings. */
async function held(request: APIRequestContext, key: string): Promise<{ value: string; set: boolean }> {
  const read = await request.get('/api/settings');
  expect(read.ok()).toBe(true);
  const setting = ((await read.json()) as { settings: { key: string; value: string; set: boolean }[] }).settings.find((s) => s.key === key);
  if (setting === undefined) throw new Error(`The server lists no setting ${key}.`);
  return setting;
}

/** Opens the workbench in another browser, with nothing of this one's, and hands it to `look`; its problems fail the test. */
async function elsewhere(browser: Browser, baseURL: string | undefined, look: (page: Page) => Promise<void>): Promise<void> {
  if (baseURL === undefined) throw new Error('The configuration names no baseURL.');
  const other = await browser.newContext({ baseURL });
  try {
    const page = await other.newPage();
    const problems = watchProblems(page);
    await open(page);
    await look(page);
    expect(problems).toEqual([]);
  } finally {
    await other.close();
  }
}

test('ADR-0017 §5: the theme toggled in one browser follows the user to another', async ({ page, browser, baseURL }) => {
  await open(page);
  const html = page.locator('html');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'dark');
  await command(page, 'toggle theme');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'light');
  await expect.poll(async () => (await held(page.request, 'shell.theme')).value).toBe('light');

  await elsewhere(browser, baseURL, async (other) => {
    await expect(other.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'light');
  });
});

test("ADR-0017 §6.2: the theme `auto` follows the device's own scheme", async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' });
  await open(page);
  await command(page, 'settings theme', 'auto');
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'light');
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'dark');
});

test('ADR-0017 §4: the line sets a setting and resets it, saying each time what it replaced and how to take it back', async ({ page }) => {
  await open(page);
  const html = page.locator('html');
  const notice = page.getByTestId('notice');

  // The values a setting takes are offered as its value is typed.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type('settings theme ');
  await expect(page.getByRole('option')).toHaveText([/^dark/, /^light/, /^auto/]);
  await page.keyboard.press('Escape');

  await command(page, 'settings theme', 'light');
  await expect(notice).toHaveText('Theme is light; it was dark, as registered. To set it back: settings reset theme.');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'light');

  await command(page, 'settings reset', 'theme');
  await expect(notice).toHaveText('Theme is dark again, as registered; it was light. To set it back: settings theme light.');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'dark');
  expect((await held(page.request, 'shell.theme')).set).toBe(false);
});

test('ADR-0017 §2: a value a setting does not take is refused by name, and the setting stays as it was', async ({ page, problems }) => {
  await open(page);
  await command(page, 'settings theme', 'pink');
  await expect(page.getByTestId('notice')).toHaveText('settings: Theme is one of dark, light, auto; pink is not.');
  // The browser reports the refusal's status; the page said what was refused.
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'dark');
  expect((await held(page.request, 'shell.theme')).set).toBe(false);
});

test('ADR-0017 §4: the settings dialog lists each setting with where its value comes from, changes one and resets it', async ({ page }) => {
  await open(page);
  await command(page, 'settings');
  const dialog = page.getByRole('dialog', { name: 'Settings' });
  const theme = dialog.getByRole('row').filter({ hasText: 'Theme' });
  await expect(theme.getByRole('combobox', { name: 'Theme' })).toHaveValue('dark');
  await expect(theme).toContainText('registered');
  await expect(theme.getByRole('button', { name: 'Reset theme' })).toBeDisabled();

  await theme.getByRole('combobox', { name: 'Theme' }).click();
  await page.getByRole('option', { name: 'light' }).click();
  await expect(page.getByTestId('notice')).toHaveText('Theme is light; it was dark, as registered. To set it back: settings reset theme.');
  await expect(theme).toContainText('set by you');
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'light');

  await theme.getByRole('button', { name: 'Reset theme' }).click();
  await expect(page.getByTestId('notice')).toHaveText('Theme is dark again, as registered; it was light. To set it back: settings theme light.');
  await expect(theme).toContainText('registered');
  await expect(theme.getByRole('button', { name: 'Reset theme' })).toBeDisabled();
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'dark');
});

test("ADR-0017 §4, design guidelines §9: on a phone the settings dialog fits its width, with each setting's source and reset under its name", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await command(page, 'settings');
  const dialog = page.getByRole('dialog', { name: 'Settings' });
  const table = dialog.getByRole('table', { name: 'Your settings' });
  const [room, used] = [await dialog.boundingBox(), await table.boundingBox()];
  if (room === null || used === null) throw new Error('The settings dialog, or its table, is not on screen.');
  expect(used.x + used.width).toBeLessThanOrEqual(room.x + room.width);

  const theme = table.getByRole('row').filter({ hasText: 'Theme' });
  await expect(theme.getByText('registered').filter({ visible: true })).toBeInViewport();
  await theme.getByRole('combobox', { name: 'Theme' }).click();
  await page.getByRole('option', { name: 'light' }).click();
  await expect(theme.getByText('set by you').filter({ visible: true })).toBeInViewport();
  await theme.getByRole('button', { name: 'Reset theme' }).click();
  await expect(theme.getByText('registered').filter({ visible: true })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('ADR-0006 §6, ADR-0017 §5: a command run in one browser ranks first among its group in another', async ({ page, browser, baseURL }) => {
  await open(page);
  const first = (on: Page) => on.getByRole('group', { name: 'Settings' }).getByRole('option').first();
  // Untouched, the group lists its commands as registered: `settings` first.
  await page.keyboard.press('ControlOrMeta+K');
  await expect(first(page)).toContainText('Show your settings');
  await page.keyboard.press('Escape');

  await taken(page, 'settings theme', 'light');
  await taken(page, 'settings reset', 'theme');
  await expect.poll(async () => JSON.parse((await held(page.request, 'commands.usage')).value) as Record<string, unknown>)
    .toHaveProperty(['settings.reset']);

  await elsewhere(browser, baseURL, async (other) => {
    await other.keyboard.press('ControlOrMeta+K');
    await expect(first(other)).toContainText('Reset one of your settings');
  });
});

test("ADR-0017 §5: a column dragged in the pricer's existing trades keeps its width after a reload and in another workspace, and a reset puts the declared width back", async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  // The header's name holds its column menu's button too.
  const product = page.getByLabel('Existing trades', { exact: true }).getByRole('columnheader', { name: 'Product Column menu', exact: true });
  const width = async () => Math.round((await product.boundingBox())?.width ?? 0);
  const before = (await product.boundingBox())!;

  // The grip is the header's right edge; the width moves on release.
  await page.mouse.move(before.x + before.width - 2, before.y + before.height / 2);
  await page.mouse.down();
  await page.mouse.move(before.x + before.width + 58, before.y + before.height / 2, { steps: 10 });
  await page.mouse.up();
  await expect.poll(width).toBe(Math.round(before.width) + 60);
  await expect.poll(async () => (await held(page.request, 'grid.widths.xva/trades/existing')).set).toBe(true);

  // A reload, and another workspace: the width is the user's for that grid.
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 2);
  await expect.poll(width).toBe(Math.round(before.width) + 60);

  await taken(page, 'settings reset', 'widths xva/trades/existing');
  await expect(page.getByTestId('notice')).toHaveText('Column widths (widths xva/trades/existing) is as registered again; its page writes it again as you use it.');
  await settled(page, 'Existing trades');
  await expect.poll(width).toBe(Math.round(before.width));
});

test('ADR-0017 §3: a pricer workspace made after the display currency is set starts in it and says so on the page; one chosen in the workspace is its own', async ({ page }) => {
  await open(page);
  await taken(page, 'settings display currency', 'USD');
  await expect(page.getByTestId('notice')).toHaveText('Display currency is USD; it was JPY, as registered. To set it back: settings reset display currency.');
  await newWorkspace(page, 'ABC/LE-01', 0);
  const panel = page.getByRole('tabpanel');
  const currency = panel.getByRole('combobox', { name: 'Currency', exact: true });
  // Said on the status line, which a touch screen reads too.
  const from = panel.getByTestId('display-currency-from');
  await expect(currency).toHaveValue('USD');
  await expect(from).toHaveText('Display currency USD, from your settings as this workspace was made.');

  await currency.click();
  await page.getByRole('option', { name: 'EUR', exact: true }).click();
  await expect(currency).toHaveValue('EUR');
  await expect(from).toHaveText('Display currency EUR, chosen in this workspace.');
});

test("ADR-0017 §3: a Rates workspace starts with the user's defaults for a new line, says so on the page, and a new line says each came from them", async ({ page }) => {
  await open(page);
  await taken(page, 'settings new line currency', 'JPY');
  await taken(page, 'settings new line notional', '500');
  await taken(page, 'settings new line direction', 'rec');
  await command(page, 'new rates');
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' }).getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await settled(page, 'Lines');
  await expect(page.getByRole('tabpanel').getByTestId('line-defaults')).toHaveText('New lines default to JPY · 500mm · receive from your settings.');

  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type('new irs 5y');
  const read = page.getByTestId('command-preview');
  await expect(read).toContainText('currency JPY from settings');
  await expect(read).toContainText('notional 500mm from settings');
  await expect(read).toContainText('direction receive from settings');
  await page.keyboard.press('Escape');
});
