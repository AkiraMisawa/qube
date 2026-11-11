import type { APIRequestContext } from '@playwright/test';
import { command, expect, open, test } from './fixtures';

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

test('ADR-0017 §5: the theme the header sets follows the user to another browser', async ({ page, browser, baseURL }) => {
  await open(page);
  const html = page.locator('html');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'dark');
  await command(page, 'toggle theme');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'light');
  await expect.poll(async () => (await held(page.request, 'shell.theme')).value).toBe('light');

  // Another browser, with nothing of this one's: the theme is the user's, read from the server as the page starts.
  if (baseURL === undefined) throw new Error('The configuration names no baseURL.');
  const other = await browser.newContext({ baseURL });
  try {
    const elsewhere = await other.newPage();
    const problems: string[] = [];
    elsewhere.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`${m.type()}: ${m.text()}`); });
    elsewhere.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
    await open(elsewhere);
    await expect(elsewhere.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'light');
    expect(problems).toEqual([]);
  } finally {
    await other.close();
  }
});

test('ADR-0017 §4: the line sets a setting and resets it, saying each time what it replaced and how to take it back', async ({ page }) => {
  await open(page);
  const html = page.locator('html');
  const notice = page.getByTestId('notice');

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

test('ADR-0017 §4: the settings dialog lists each setting with where its value comes from, and resets one', async ({ page }) => {
  await open(page);
  await command(page, 'settings theme', 'light');
  await expect(page.getByTestId('notice')).toContainText('Theme is light');

  await command(page, 'settings');
  const dialog = page.getByRole('dialog', { name: 'Settings' });
  const theme = dialog.getByRole('row').filter({ hasText: 'Theme' });
  await expect(theme.getByRole('combobox', { name: 'Theme' })).toHaveValue('light');
  await expect(theme).toContainText('set by you');
  const ranking = dialog.getByRole('row').filter({ hasText: 'Command ranking' });
  await expect(ranking.getByRole('button', { name: 'Reset command ranking' })).toBeVisible();

  await theme.getByRole('button', { name: 'Reset theme' }).click();
  await expect(page.getByTestId('notice')).toHaveText('Theme is dark again, as registered; it was light. To set it back: settings theme light.');
  await expect(theme).toContainText('registered');
  await expect(theme.getByRole('button', { name: 'Reset theme' })).toBeDisabled();
  await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', 'dark');
});
