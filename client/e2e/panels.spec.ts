import type { Page } from '@playwright/test';
import { command, expect, open, settled, test } from './fixtures';

// ADR-0006 note 7 of 2026-11-12: managing the panels' tabs. What only a browser shows: whether a tab is in sight in a
// strip too narrow for them all, the keys and the pointer buttons a tab takes, and a drag. What each verb does to the
// strip is the store's and the verbs' (Vitest). From 768px the panels stand in areas, each area's strip note 7's
// (ADR-0018 §1), so these run on one area's strip at 768px and the default 1280px, and on the phone's one strip at 390px.

const strip = (page: Page) => page.locator('.qube-panels');
/** The tab labelled so, by its title alone: a tab's name holds its close button's too, and one label begins another's. */
const tab = (page: Page, label: string) =>
  strip(page).getByRole('tab').filter({ has: page.locator('.qube-panel-tab-title').getByText(label, { exact: true }) });
const labels = (page: Page) => strip(page).getByRole('tab').evaluateAll((tabs) => tabs.map((t) => t.querySelector('.qube-panel-tab-title')?.textContent));

/** Opens a Rates workspace of the default date: every one is titled alike, so each after the first is numbered. */
async function newRates(page: Page, label: string): Promise<void> {
  await command(page, 'new rates');
  // The dialog's field takes the keyboard as it mounts; Enter before that goes nowhere.
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' }).getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(tab(page, label)).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
}

/** Whether the tab is wholly inside the strip's scrolling part, as the user sees it. */
const inSight = (page: Page, label: string) => tab(page, label).evaluate((t) => {
  // The phone's strip scrolls in Mantine's viewport; an area's in dockview's list of tabs, the tab's parent.
  const viewport = t.closest('.mantine-ScrollArea-viewport') ?? (t.closest('.dv-tabs-and-actions-container') === null ? null : t.parentElement);
  if (viewport === null) throw new Error('The tab is not in a strip.');
  const [a, v] = [t.getBoundingClientRect(), viewport.getBoundingClientRect()];
  return a.left >= v.left - 0.5 && a.right <= v.right + 0.5;
});

async function sixPanels(page: Page): Promise<void> {
  await open(page);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await newRates(page, 'Rates 2026-10-27');
  for (const n of [2, 3, 4, 5]) await newRates(page, `Rates 2026-10-27 · ${n}`);
}

for (const width of [768, 390]) {
  test(`ADR-0006 note 7, UX03: at ${width}px the panel opened or chosen is in sight, and the list names every panel apart`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 });
    await sixPanels(page);
    await expect.poll(() => inSight(page, 'Rates 2026-10-27 · 5')).toBe(true);
    await expect.poll(() => inSight(page, 'Pricing DB')).toBe(false);

    await page.getByRole('button', { name: 'All panels (6)' }).click();
    const list = page.getByRole('menu', { name: 'All panels (6)', exact: true });
    await expect(list.getByRole('menuitem')).toHaveText([
      'Pricing DB', 'Rates 2026-10-27', 'Rates 2026-10-27 · 2', 'Rates 2026-10-27 · 3', 'Rates 2026-10-27 · 4', 'Rates 2026-10-27 · 5',
      // The active panel's verbs, each with the words the line takes.
      /^Close.*close panel$/, /^Close others/, /^Close to the right/, /^Close all/, /^Pin/, /^Move left/, /^Move right/, /^Rename…rename panel$/,
      // In an area, the verbs that arrange (ADR-0018 §3); the phone's one strip has no areas to arrange.
      ...(width === 390 ? [] : [/^Split/, /^Send to/, /^Float/, /^Size/, /^Maximize/, /^Gather all panels here/]),
      /^Reopen closed panel/,
    ]);
    // The last panel goes no further right: the item says why under it, where a phone reads it too, and stays in the
    // arrows' reach; choosing it leaves the list open and the panel where it is.
    const moveRight = list.getByRole('menuitem', { name: /^Move right/ });
    await expect(moveRight).toHaveAttribute('aria-disabled', 'true');
    await expect(moveRight).toHaveAccessibleDescription('This is the last panel already.');
    await expect(list.getByText('This is the last panel already.')).toBeVisible();
    await list.getByRole('menuitem', { name: /^Move left/ }).focus();
    await page.keyboard.press('ArrowDown');
    await expect(moveRight).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(moveRight).toBeFocused();
    await expect.poll(() => labels(page)).toEqual(['Pricing DB', 'Rates 2026-10-27', 'Rates 2026-10-27 · 2', 'Rates 2026-10-27 · 3', 'Rates 2026-10-27 · 4', 'Rates 2026-10-27 · 5']);
    await list.getByRole('menuitem', { name: 'Pricing DB' }).click();
    await expect(tab(page, 'Pricing DB')).toHaveAttribute('aria-selected', 'true');
    await expect.poll(() => inSight(page, 'Pricing DB')).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBe(0);
  });
}

test('ADR-0006 note 7: a tab\'s menu opens by right click and by Shift+F10, and its verbs act on that tab', async ({ page }) => {
  await sixPanels(page);
  await tab(page, 'Pricing DB').click({ button: 'right' });
  const menu = page.getByRole('menu', { name: 'Pricing DB', exact: true });
  await menu.getByRole('menuitem', { name: /^Pin/ }).click();
  await expect(menu).toBeHidden();
  await expect(tab(page, 'Pricing DB').getByLabel('pinned', { exact: true })).toBeVisible();

  // From the keyboard the menu takes it on its first item, and gives it back to the tab when it closes.
  const active = tab(page, 'Rates 2026-10-27 · 5');
  await active.focus();
  const activeMenu = page.getByRole('menu', { name: 'Rates 2026-10-27 · 5', exact: true });
  await page.keyboard.press('Shift+F10');
  await expect(activeMenu.getByRole('menuitem', { name: /^Close.*close panel$/ })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(active).toBeFocused();
  // The menu key on Windows sends a contextmenu to the tab as it is let go, which may come before the keyboard has
  // reached the menu its keydown opened: that menu stays, with the keyboard on its first item.
  await page.keyboard.press('ContextMenu');
  await active.dispatchEvent('contextmenu');
  await expect(activeMenu.getByRole('menuitem', { name: /^Close.*close panel$/ })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(active).toBeFocused();
  // Where the menu closed its own tab, the keyboard goes to the tab shown next.
  await page.keyboard.press('Shift+F10');
  await expect(activeMenu.getByRole('menuitem', { name: /^Close.*close panel$/ })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(tab(page, 'Rates 2026-10-27 · 4')).toBeFocused();
  await command(page, 'reopen panel');
  await expect(active).toHaveAttribute('aria-selected', 'true');

  await active.click({ button: 'right' });
  await activeMenu.getByRole('menuitem', { name: /^Close others/ }).click();
  await expect.poll(() => labels(page)).toEqual(['Pricing DB', 'Rates 2026-10-27 · 5']);
  await expect(page.getByTestId('notice')).toContainText('Closed 4 panels. 1 pinned panel was kept.');

  // A close of several comes back from its left, each where it stood.
  await command(page, 'reopen panel');
  await expect(tab(page, 'Rates 2026-10-27')).toHaveAttribute('aria-selected', 'true');
  await expect.poll(() => labels(page)).toEqual(['Pricing DB', 'Rates 2026-10-27', 'Rates 2026-10-27 · 5']);
});

test('ADR-0006 note 7: Delete closes the focused tab and the keyboard follows to the panel shown before; the middle button closes a tab', async ({ page }) => {
  await sixPanels(page);
  await tab(page, 'Rates 2026-10-27 · 2').click();
  await tab(page, 'Rates 2026-10-27 · 4').click();
  await page.keyboard.press('Delete');
  await expect(tab(page, 'Rates 2026-10-27 · 2')).toBeFocused();
  await expect(tab(page, 'Rates 2026-10-27 · 2')).toHaveAttribute('aria-selected', 'true');
  await expect(tab(page, 'Rates 2026-10-27 · 4')).toHaveCount(0);

  await tab(page, 'Pricing DB').click({ button: 'middle' });
  await expect.poll(() => labels(page)).toEqual(['Rates 2026-10-27', 'Rates 2026-10-27 · 2', 'Rates 2026-10-27 · 3', 'Rates 2026-10-27 · 5']);
});

test('ADR-0006 note 7: a tab dragged onto another takes its place', async ({ page }) => {
  await sixPanels(page);
  await tab(page, 'Rates 2026-10-27 · 5').dragTo(tab(page, 'Rates 2026-10-27'));
  await expect.poll(() => labels(page)).toEqual(['Pricing DB', 'Rates 2026-10-27 · 5', 'Rates 2026-10-27', 'Rates 2026-10-27 · 2', 'Rates 2026-10-27 · 3', 'Rates 2026-10-27 · 4']);
});

// ADR-0006 note 9: a tab's Rename, its F2, its double click and the list's Rename (a phone's way) open the line on
// `rename panel`, the name typed and selected, so typing replaces it; the keyboard goes back to the tab as the line
// closes. In an area's strip at 1280px and in the phone's one strip, whose tabs and keys are drawn apart.
for (const width of [1280, 390]) {
  test(`ADR-0006 note 9: at ${width}px a tab is renamed on the line from its menu, F2, a double click and the list`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 });
    await open(page);
    await command(page, 'open pricing db');
    await settled(page, 'Pricing DB');
    await newRates(page, 'Rates 2026-10-27');
    await newRates(page, 'Rates 2026-10-27 · 2');
    const line = page.getByRole('combobox', { name: 'Command' });
    /** The line's text, and the part of it selected. */
    const typed = () => line.evaluate((input: HTMLInputElement) => ({ value: input.value, selected: input.value.slice(input.selectionStart ?? 0, input.selectionEnd ?? 0) }));
    /** The line is open on `rename panel` with this name selected, and has the keyboard. */
    const renaming = async (name: string) => {
      await expect(line).toBeFocused();
      await expect.poll(typed).toEqual({ value: `rename panel ${name}`, selected: name });
    };

    // From a tab that is not shown: the menu shows it, and the line names it.
    await tab(page, 'Rates 2026-10-27').click({ button: 'right' });
    await page.getByRole('menu', { name: 'Rates 2026-10-27', exact: true }).getByRole('menuitem', { name: /^Rename/ }).click();
    await renaming('Rates 2026-10-27');
    await expect(tab(page, 'Rates 2026-10-27')).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.type('Hedge desk');
    await page.keyboard.press('Enter');
    await expect(line).toBeHidden();
    await expect(tab(page, 'Hedge desk')).toHaveAttribute('aria-selected', 'true');
    await expect(tab(page, 'Hedge desk')).toBeFocused();

    // F2 on the focused tab; Escape names nothing, and the keyboard is the tab's again.
    await tab(page, 'Pricing DB').click();
    await page.keyboard.press('F2');
    await renaming('Pricing DB');
    await page.keyboard.press('Escape');
    await expect(line).toBeHidden();
    await expect(tab(page, 'Pricing DB')).toBeFocused();

    // The menu a key opened, Rename reached by the arrows: the line has the keyboard, not the tab, until it closes. At
    // 1280px this is the first tab of an area at the window's edge, whose menu is drawn too.
    await page.keyboard.press('Shift+F10');
    const menu = page.getByRole('menu', { name: 'Pricing DB', exact: true });
    await expect(menu.getByRole('menuitem', { name: /^Close.*close panel$/ })).toBeFocused();
    for (let i = 0; i < 7; i += 1) await page.keyboard.press('ArrowDown');
    await expect(menu.getByRole('menuitem', { name: /^Rename/ })).toBeFocused();
    await page.keyboard.press('Enter');
    await renaming('Pricing DB');
    await page.keyboard.type('Book');
    await page.keyboard.press('Enter');
    await expect(tab(page, 'Book')).toBeFocused();

    // The list's Rename, which a touch screen reaches: the shown panel's name.
    await page.getByRole('button', { name: 'All panels (3)' }).click();
    await page.getByRole('menu', { name: 'All panels (3)', exact: true }).getByRole('menuitem', { name: /^Rename/ }).click();
    await renaming('Book');
    await page.keyboard.type('Runs');
    await page.keyboard.press('Enter');
    await expect(tab(page, 'Runs')).toBeFocused();

    // A double click; its page's title gives the panel that title back, and the lowest number free among that title.
    await tab(page, 'Hedge desk').dblclick();
    await renaming('Hedge desk');
    await page.keyboard.type('Rates 2026-10-27');
    await page.keyboard.press('Enter');
    await expect(line).toBeHidden();
    await expect.poll(() => labels(page)).toEqual(['Runs', 'Rates 2026-10-27', 'Rates 2026-10-27 · 2']);
  });
}
