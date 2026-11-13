import type { Locator, Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, taken, test } from './fixtures';
import client from '../package.json' with { type: 'json' };

/** The ExGrid the workbench takes, every `@ex-grid/*` package at one version (ADR-0001). */
const EX_GRID = client.dependencies['@ex-grid/core'];

// ADR-0018 step 2 (docs/specs/panels-2-windows): a panel in another window of the browser. What only a browser shows:
// the window opened and closed, its look kept the main window's, its menus, selects, tooltips and dialogs opened in it,
// the shell's keys heard there, a grid there that takes clicks and keys and reads that window's Device Pixel, and the
// panels back in the main window after a reload, a window refused, and the strip. What each verb refuses and asks of
// the arrangement is Vitest's (areaVerbs.test, layout.test). Chromium opens a window a test asks for.

const areasRoot = (page: Page) => page.locator('.qube-areas');
const notice = (page: Page) => page.getByTestId('notice');
/** The tab of this title, in whichever area of this window holds it. */
const tab = (page: Page, title: string) =>
  page.getByRole('tab').filter({ has: page.locator('.qube-panel-tab-title').getByText(title, { exact: true }) });
const RATES = 'Rates 2026-10-27';

/** Opens a Rates workspace of the default date: every one is titled alike, so each after the first is numbered. */
async function newRates(page: Page, title = RATES): Promise<void> {
  await command(page, 'new rates');
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' }).getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(tab(page, title)).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
}

/** Pricing DB and a Rates workspace in one area, the Rates workspace shown. */
async function twoPanels(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await newRates(page);
}

/**
 * The window a panel is popped out into, once it shows the panel: its console messages and page errors are the test's
 * problems as the main page's are, and fail the test that caused them.
 */
async function windowOf(opening: Promise<Page>, title: string, problems: string[]): Promise<Page> {
  const other = await opening;
  other.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`other window ${m.type()}: ${m.text()}`); });
  other.on('pageerror', (e) => problems.push(`other window pageerror: ${e.message}`));
  await expect(other.getByRole('tabpanel', { name: title, exact: true })).toBeVisible();
  return other;
}

/** Pops out the active panel by the line, and answers its window. */
async function popOut(page: Page, title: string, problems: string[]): Promise<Page> {
  const opening = page.waitForEvent('popup');
  await command(page, 'pop out panel');
  return windowOf(opening, title, problems);
}

/**
 * Runs a command from the line of another window that closes that window as it runs: the window closes on Enter's down,
 * so its up, which would go to a window that is gone, is never sent. The window can be gone before the browser has
 * answered that the key went down, so the key's own answer is read only where the window is still open: what the test
 * waits for is the window closing.
 */
async function closingWith(other: Page, words: string): Promise<void> {
  await other.keyboard.press('ControlOrMeta+K');
  await expect(other.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await other.keyboard.type(words);
  await expect(other.getByTestId('command-pending')).toContainText(words);
  const closing = other.waitForEvent('close');
  await other.keyboard.down('Enter').catch((error: unknown) => {
    if (!other.isClosed()) throw error;
  });
  await closing;
}

/** Where an element is drawn in its window, in CSS pixels. */
async function boxOf(element: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const box = await element.boundingBox();
  if (box === null) throw new Error(`${element.toString()} is not drawn.`);
  return box;
}

/** The area that shows the panel of this title, in this window. */
const areaOf = (page: Page, title: string) =>
  page.locator('.dv-groupview').filter({ has: page.getByRole('tabpanel', { name: title, exact: true }) });

/** What ExGrid says one Device Pixel is, in CSS pixels, on a grid's root (ex-grid ADR-0090). */
const devicePixelOf = (grid: Locator) => grid.evaluate((root) => /--ex-dp:\s*([^;]+)/.exec(root.getAttribute('style') ?? '')?.[1]);

test('ADR-0018 §9: pop out panel moves a panel into a window of its own, titled by it, drawn anew there with its screen, whose grid takes presses and keys; dock panel puts it back', async ({ page, problems }) => {
  await twoPanels(page);
  // A line, and its first leg open: what the page shows, which its panel keeps (ADR-0006 note 10).
  await page.getByRole('button', { name: 'Add IRS' }).click();
  await settled(page, 'Lines');
  await page.getByLabel('Lines', { exact: true }).getByRole('button', { name: 'Expand Leg 1' }).click();
  await settled(page, 'Lines');

  const other = await popOut(page, RATES, problems);
  await expect(other).toHaveTitle(`${RATES} · Qube`);
  // The main window keeps the other panel, in its area; the panel popped out is in no area of it.
  await expect(tab(page, 'Pricing DB')).toHaveAttribute('aria-selected', 'true');
  await expect(tab(page, RATES)).toHaveCount(0);
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');

  // The page was drawn anew there with what it showed: its grid settles, and takes a press and the arrows.
  const lines = other.getByLabel('Lines', { exact: true });
  await expect(lines.locator('.ex-grid')).toHaveAttribute('data-ex-settled', '');
  await expect(lines.getByRole('button', { name: 'Collapse Leg 1' })).toHaveAttribute('aria-expanded', 'true');
  await expect(lines.getByRole('row', { name: /^L\d+ / })).toHaveCount(1);
  // The grid's keyboard field names the cell its Focus is on.
  const field = lines.locator('[aria-activedescendant]');
  await lines.getByRole('gridcell').first().click({ force: true });
  await expect(field).toHaveAttribute('aria-activedescendant', /r0c0$/);
  await other.keyboard.press('ArrowRight');
  await expect(field).toHaveAttribute('aria-activedescendant', /r0c1$/);

  // `dock panel`, run in that window, puts the panel back in the main window, and the window closes.
  await closingWith(other, 'dock panel');
  await expect(tab(page, RATES)).toHaveAttribute('aria-selected', 'true');
  await expect(notice(page)).toHaveText(`Docked ${RATES} back into the area of Pricing DB.`);
  await settled(page, 'Lines');
  await expect(page.getByLabel('Lines', { exact: true }).getByRole('button', { name: 'Collapse Leg 1' })).toHaveAttribute('aria-expanded', 'true');
});

test('ADR-0018 §9: a whole area popped out from its tab\'s menu keeps its place, and the window closed by the user puts it back there, said', async ({ page, problems }) => {
  await twoPanels(page);
  await command(page, 'split panel', 'right');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
  const left = (await boxOf(tab(page, 'Pricing DB'))).x;
  const stood = await boxOf(areaOf(page, RATES));
  const popOutByMenu = async () => {
    await tab(page, RATES).click({ button: 'right' });
    const opening = page.waitForEvent('popup');
    await page.getByRole('menu', { name: RATES, exact: true }).getByRole('menuitem', { name: /^Pop out/ }).click();
    const other = await windowOf(opening, RATES, problems);
    // Its area's place is kept, hidden: Pricing DB's area has the room.
    await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');
    expect((await boxOf(tab(page, 'Pricing DB'))).x).toBe(left);
    return other;
  };
  // Back where its area stood, as wide as it was.
  const backWhereItStood = async () => {
    await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
    const back = await boxOf(areaOf(page, RATES));
    expect(back.x).toBeCloseTo(stood.x, 0);
    expect(back.width).toBeCloseTo(stood.width, 0);
  };

  // Closed as a user closes it, the page told it is going.
  await (await popOutByMenu()).close({ runBeforeUnload: true });
  await expect(notice(page)).toHaveText(`The window of ${RATES} closed: it is back where it was.`);
  await backWhereItStood();

  // `dock panel` from that window puts it back in the place kept, too.
  await closingWith(await popOutByMenu(), 'dock panel');
  await expect(notice(page)).toHaveText(`Docked ${RATES} back where its area stood.`);
  await backWhereItStood();
});

test('ADR-0018 §9: the other window takes the main one\'s look and follows its scheme, and its menus, tooltips and selects open in it', async ({ page, problems }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await newWorkspace(page, 'ABC/LE-01', 3);
  const other = await popOut(page, 'XVA ABC/LE-01', problems);
  const html = other.locator('html');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'dark');
  const body = () => other.evaluate(() => getComputedStyle(document.body).backgroundColor);
  const dark = await body();
  await command(page, 'toggle theme');
  await expect(html).toHaveAttribute('data-mantine-color-scheme', 'light');
  await expect.poll(body).not.toBe(dark);

  // The tab's close button tells what it does, in that window.
  await tab(other, 'XVA ABC/LE-01').locator('.qube-area-tab-close').hover();
  await expect(other.getByRole('tooltip', { name: 'Close (close panel)' })).toBeVisible();
  // The list at the end of its area's strip, a menu, opens there and a press outside it closes it.
  await other.getByRole('button', { name: 'Panels in this area' }).click();
  await expect(other.getByRole('menu')).toBeVisible();
  await expect(page.getByRole('menu')).toHaveCount(0);
  await other.mouse.click(700, 600);
  await expect(other.getByRole('menu')).toHaveCount(0);
  // A select of the page, there too.
  await other.getByRole('combobox', { name: 'Currency', exact: true }).click();
  await expect(other.getByRole('listbox')).toBeVisible();
  await expect(page.getByRole('listbox')).toHaveCount(0);
  await other.keyboard.press('Escape');
  await expect(other.getByRole('listbox')).toHaveCount(0);

  // A window shorter than the menu of every verb: the menu keeps inside the window and scrolls to its last item.
  await other.setViewportSize({ width: 880, height: 480 });
  await tab(other, 'XVA ABC/LE-01').click({ button: 'right' });
  const verbs = other.getByRole('menu', { name: 'XVA ABC/LE-01', exact: true });
  const box = await boxOf(verbs);
  expect(box.y + box.height).toBeLessThanOrEqual(480);
  const last = verbs.getByRole('menuitem', { name: /^Reopen closed panel/ });
  await last.scrollIntoViewIfNeeded();
  await expect(last).toBeInViewport();
  await other.keyboard.press('Escape');
  await expect(verbs).toBeHidden();
});

test('ADR-0018 §9: Ctrl+K, / and a tab\'s Rename open the line in the other window, on its panel; a dialog opened from there opens there, with its Escape and Tab; a press in the main window takes the line back there', async ({ page, problems }) => {
  await twoPanels(page);
  const other = await popOut(page, RATES, problems);
  await other.keyboard.press('ControlOrMeta+K');
  const line = other.getByRole('combobox', { name: 'Command' });
  await expect(line).toBeFocused();
  await expect(page.getByRole('combobox', { name: 'Command' })).toHaveCount(0);
  await other.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // `/` where nothing is typed into opens it too.
  await other.getByText('Select a line to see its result here.').click();
  await other.keyboard.press('/');
  await expect(line).toBeFocused();
  await other.keyboard.press('Escape');
  await expect(line).toBeHidden();

  // The tab's Rename opens it on the panel's name, there, and the window takes the new name as its title.
  await tab(other, RATES).click({ button: 'right' });
  await other.getByRole('menu', { name: RATES, exact: true }).getByRole('menuitem', { name: /^Rename/ }).click();
  await expect(line).toBeFocused();
  await other.keyboard.type('Hedges');
  await expect(other.getByTestId('command-pending')).toContainText('rename panel');
  await other.keyboard.press('Enter');
  await expect(tab(other, 'Hedges')).toBeVisible();
  await expect(other).toHaveTitle('Hedges · Qube');

  // A dialog the line opens is drawn in that window, and its focus trap and Escape are its own.
  await command(other, 'new rates');
  const dialog = other.getByRole('dialog', { name: 'New Rates workspace' });
  await expect(dialog.getByLabel('Valuation date')).toBeFocused();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await other.keyboard.press('Shift+Tab');
  await expect(dialog.getByLabel('Valuation date')).not.toBeFocused();
  expect(await dialog.evaluate((d) => d.contains(d.ownerDocument.activeElement))).toBe(true);
  await other.keyboard.press('Escape');
  await expect(dialog).toBeHidden();

  // A press in the main window makes it the window in use again: its header's Commands opens the line there.
  await page.getByRole('button', { name: 'Open command line' }).click();
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await expect(line).toHaveCount(0);
});

test('ADR-0018 §9: send panel to moves a panel into the other window and back, the window is titled by the panel it shows, and it closes as its last panel leaves', async ({ page, problems }) => {
  await twoPanels(page);
  const other = await popOut(page, RATES, problems);
  await newRates(page, `${RATES} · 2`);
  await tab(page, 'Pricing DB').click();
  await command(page, 'send panel to', RATES);
  await expect(tab(other, 'Pricing DB')).toHaveAttribute('aria-selected', 'true');
  await expect(tab(other, RATES)).toBeVisible();
  await expect(tab(page, 'Pricing DB')).toHaveCount(0);
  // The window is titled by the panel it shows, as a tab is chosen there.
  await expect(other).toHaveTitle('Pricing DB · Qube');
  await tab(other, RATES).click();
  await expect(other).toHaveTitle(`${RATES} · Qube`);

  // Sent from there into an area of the main window; the window keeps its other panel.
  await tab(other, 'Pricing DB').click();
  await command(other, 'send panel to', `${RATES} · 2`);
  await expect(tab(page, 'Pricing DB')).toHaveAttribute('aria-selected', 'true');
  await expect(tab(other, 'Pricing DB')).toHaveCount(0);
  await expect(other).toHaveTitle(`${RATES} · Qube`);
  // Its last panel closed there, the window closes.
  await closingWith(other, 'close panel');
  await expect(tab(page, RATES)).toHaveCount(0);
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');
  await expect(page.getByRole('tab')).toHaveCount(2);
});

test('ADR-0018 §9: ai from the other window asks with its panel, and says the answer is in the main window', async ({ page, problems }) => {
  await twoPanels(page);
  const other = await popOut(page, RATES, problems);
  await taken(other, 'ai', 'what is priced here?');
  await expect(notice(other)).toHaveText('Asked the AI: its answer is in the AI pane of the main window.');
  await expect(page.getByTestId('ai-pane')).toContainText('what is priced here?');
});

test('ADR-0018 §9: a window the browser does not open is said, and the panel stays where it was', async ({ page, problems }) => {
  await twoPanels(page);
  await page.evaluate(() => { window.open = () => null; });
  await command(page, 'pop out panel');
  await expect(notice(page)).toHaveText('pop out panel: The browser did not open the window: allow this site its pop-ups. The panel stays where it was.');
  await expect(tab(page, RATES)).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
  // dockview logs the window it could not open, beside the browser's own refusal: that message, and only it, is this
  // test's to meet.
  expect(problems).toEqual(['error: dockview: failed to create popout. perhaps you need to allow pop-ups for this website']);
  problems.length = 0;
});

test('ADR-0018 §9: a reload brings the other windows\' panels back into the main window, said, and pop out panel sends one out again', async ({ page, problems }) => {
  await twoPanels(page);
  const other = await popOut(page, RATES, problems);
  const closing = other.waitForEvent('close');
  await page.reload();
  await closing;
  await expect(notice(page)).toHaveText(`Back in this window, as a browser opens a window only on a key or a press: ${RATES} where it was. pop out panel sends a panel out again.`);
  await expect(tab(page, RATES)).toBeVisible();
  await tab(page, RATES).click();
  await settled(page, 'Lines');
  const again = await popOut(page, RATES, problems);
  await expect(again.getByLabel('Lines', { exact: true }).locator('.ex-grid')).toHaveAttribute('data-ex-settled', '');
});

test('ADR-0018 §5 and §9: the strip closes the other windows and their panels join it; with room again they are in the main window', async ({ page, problems }) => {
  await twoPanels(page);
  const other = await popOut(page, RATES, problems);
  const closing = other.waitForEvent('close');
  await page.setViewportSize({ width: 700, height: 900 });
  await closing;
  await expect(notice(page)).toHaveText(`The other windows closed as the panels became one strip: ${RATES} is in it.`);
  await expect(page.getByRole('tab', { name: RATES })).toBeVisible();
  await page.setViewportSize({ width: 1440, height: 900 });
  await expect(areasRoot(page)).toBeVisible();
  await expect(tab(page, RATES)).toBeVisible();
});

test('ADR-0018 §8 and §9: arrange closes the other windows and places their panels in the main one; arrange back draws them there, said', async ({ page, problems }) => {
  await twoPanels(page);
  await newRates(page, `${RATES} · 2`);
  await tab(page, RATES).click();
  const other = await popOut(page, RATES, problems);
  await page.getByRole('button', { name: 'Show the AI pane' }).click();
  await page.getByRole('textbox', { name: 'Ask the AI' }).fill(`put ${RATES} · 2 and Pricing DB side by side`);
  await page.keyboard.press('Enter');

  // The proposal arranges the main window: the arrangement drawn anew closes the window, and its panel joins the first
  // area as a panel the proposal does not place.
  const closing = other.waitForEvent('close');
  await page.getByTestId('proposal').getByRole('button', { name: 'Apply' }).click();
  await closing;
  await expect(notice(page)).toHaveText(`Arranged as proposal 1: 2 areas. 1 panel the proposal did not place is in the area of ${RATES} · 2, the first in reading order. To put back what it replaced: arrange back.`);
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');

  // What it replaced comes back with the window's panel in the main window, where it stood.
  await command(page, 'arrange back');
  await expect(notice(page)).toHaveText(`Put back the arrangement proposal 1 replaced. Back in this window from the windows proposal 1 closed: ${RATES} where it was. pop out panel sends a panel out again.`);
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');
  await expect(tab(page, RATES)).toBeVisible();
});

test('ADR-0018 §9, ex-grid ADR-0090: a grid in the other window is told that window\'s Device Pixel, not the main one\'s', async ({ page, context, problems }) => {
  // ExGrid 0.2.0 reads the main window's Device Pixel; 0.3.0-rc.1 reads its own window's (ex-grid #565, fixed by
  // #566). Until the workbench takes it this test fails, as it should, and is marked so: it passes, and must, once it
  // does.
  test.fail(EX_GRID === '0.2.0', 'ExGrid 0.2.0 reads the main window\'s Device Pixel (ex-grid #565).');
  await twoPanels(page);
  const other = await popOut(page, RATES, problems);
  const grid = other.getByLabel('Lines', { exact: true }).locator('.ex-grid');
  await expect(grid).toHaveAttribute('data-ex-settled', '');
  const opener = await page.evaluate(() => devicePixelRatio);
  // The window is dragged to a display of 200%: its scale changes, and with it its width, as a window's does.
  const session = await context.newCDPSession(other);
  await session.send('Emulation.setDeviceMetricsOverride', { width: 1200, height: 800, deviceScaleFactor: 2, mobile: false });
  await expect.poll(() => other.evaluate(() => devicePixelRatio)).toBe(2);
  await expect.poll(() => devicePixelOf(grid)).toBe('0.5px');
  expect(await page.evaluate(() => devicePixelRatio)).toBe(opener);
});
