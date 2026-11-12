import type { CDPSession, Locator, Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, taken, test, textBelowContrast } from './fixtures';

// ADR-0018 step 1 (docs/specs/panels-1-areas): the panels in areas. What only a browser shows: where dockview draws
// each area after a verb, a drag with the mouse and with a finger, the keys of a splitter and a title bar, the panels'
// space as the window and the AI pane leave it, the arrangement kept by the browser tab, the AI's proposal applied, and
// a page laid out by its area's width. What each verb refuses and asks of the arrangement is Vitest's (areaVerbs.test).

const areasRoot = (page: Page) => page.locator('.qube-areas');
const panels = (page: Page) => page.locator('.qube-panels');
/** The area showing the panel of this title, by its page. */
const areaOf = (page: Page, title: string) =>
  page.locator('.qube-areas .dv-groupview').filter({ has: page.getByRole('tabpanel', { name: title, exact: true }) });
/** The tab of this title, in whichever area holds it. */
const tab = (page: Page, title: string) =>
  panels(page).getByRole('tab').filter({ has: page.locator('.qube-panel-tab-title').getByText(title, { exact: true }) });
const floating = (page: Page) => page.locator('.qube-areas .dv-resize-container');
const titleBar = (page: Page) => page.getByRole('button', { name: /^Floating area/ });
const notice = (page: Page) => page.getByTestId('notice');
const sideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const found = await locator.boundingBox();
  if (found === null) throw new Error('The element is not drawn.');
  return found;
}

/** Opens a Rates workspace of the default date: every one is titled alike, so each after the first is numbered. */
async function newRates(page: Page, title: string): Promise<void> {
  await command(page, 'new rates');
  // The dialog's field takes the keyboard as it mounts; Enter before that goes nowhere.
  await expect(page.getByRole('dialog', { name: 'New Rates workspace' }).getByLabel('Valuation date')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(tab(page, title)).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Lines');
}

/** Pricing DB and two Rates workspaces, in one area, the second Rates workspace shown. */
async function threePanels(page: Page): Promise<void> {
  await open(page);
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await newRates(page, 'Rates 2026-10-27');
  await newRates(page, 'Rates 2026-10-27 · 2');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');
}

test('ADR-0018 §3: split, size, float, dock, maximize and gather by the line, each drawn where it says', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await threePanels(page);
  const room = await box(areasRoot(page));

  // Split right: the panel's own area on the left, the new one on the right, half the room each.
  await command(page, 'split panel', 'right');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
  const [left, right] = [await box(areaOf(page, 'Rates 2026-10-27')), await box(areaOf(page, 'Rates 2026-10-27 · 2'))];
  expect(right.x).toBeCloseTo(left.x + left.width, 0);
  expect(right.width).toBeCloseTo(room.width / 2, -1);
  // The panel the line acts on is the one whose area is framed in the accent.
  await expect(areaOf(page, 'Rates 2026-10-27 · 2')).toHaveClass(/dv-active-group/);
  await expect(areaOf(page, 'Rates 2026-10-27')).not.toHaveClass(/dv-active-group/);

  // A quarter of the row.
  await command(page, 'size panel', '25%');
  await expect.poll(async () => Math.round((await box(areaOf(page, 'Rates 2026-10-27 · 2'))).width)).toBe(Math.round(room.width / 4));

  // Floated over the left half, its title bar moves it by the arrows and resizes it with Shift.
  await command(page, 'float panel', 'left');
  await expect(floating(page)).toHaveCount(1);
  const floated = await box(floating(page));
  expect(floated.x).toBeCloseTo(room.x, 0);
  expect(floated.width).toBeCloseTo(room.width / 2, -1);
  await titleBar(page).focus();
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => Math.round((await box(floating(page))).x)).toBe(Math.round(floated.x) + 24);
  await expect(titleBar(page)).toBeFocused();
  await page.keyboard.press('Shift+ArrowLeft');
  await expect.poll(async () => Math.round((await box(floating(page))).width)).toBe(Math.round(floated.width) - 24);

  // Docked again, it says where; maximized, its area has the room, and restored, the other comes back.
  await command(page, 'dock panel');
  await expect(notice(page)).toHaveText(/^Docked Rates 2026-10-27 · 2 /);
  await expect(floating(page)).toHaveCount(0);
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
  await command(page, 'maximize panel');
  await expect(areaOf(page, 'Rates 2026-10-27')).toBeHidden();
  await expect.poll(async () => Math.round((await box(areaOf(page, 'Rates 2026-10-27 · 2'))).width)).toBe(Math.round(room.width));
  await command(page, 'unmaximize panel');
  await expect(areaOf(page, 'Rates 2026-10-27')).toBeVisible();

  // Gathered, every panel is in one area again, in reading order.
  await command(page, 'gather panels');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');
  await expect(panels(page).getByRole('tab')).toHaveCount(3);
});

test('ADR-0018 §3: a tab dragged with the mouse onto an area\'s edge splits it there, and with Shift held floats it, to be docked back where it came from; a floating area is placed whole from its menu', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await threePanels(page);
  const room = await box(areasRoot(page));
  const from = await box(tab(page, 'Pricing DB'));

  // Shift with a press on the empty end of the only area's strip would float the whole window's panels: refused.
  await page.locator('.qube-areas .dv-void-container').first().click({ modifiers: ['Shift'] });
  await expect(notice(page)).toHaveText('float panel: It is the only area docked in the window; floating it would leave none docked.');
  await expect(floating(page)).toHaveCount(0);

  // Onto the right edge of the area's page: the tab's panel stands in a new area there.
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(room.x + room.width - 20, room.y + room.height / 2, { steps: 12 });
  await page.mouse.up();
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
  const moved = await box(areaOf(page, 'Pricing DB'));
  expect(moved.x).toBeGreaterThan(room.x + room.width / 3);

  // Shift held as the tab is taken: it floats where it is let go.
  const next = await box(tab(page, 'Rates 2026-10-27'));
  await page.keyboard.down('Shift');
  await page.mouse.move(next.x + next.width / 2, next.y + next.height / 2);
  await page.mouse.down();
  await page.mouse.move(room.x + 300, room.y + 300, { steps: 12 });
  await page.mouse.up();
  await page.keyboard.up('Shift');
  await expect(floating(page)).toHaveCount(1);
  await expect(floating(page).getByRole('tabpanel', { name: 'Rates 2026-10-27', exact: true })).toBeVisible();

  // Floated by the pointer, it is docked back into the area it was taken from, as one floated by `float panel` is.
  await command(page, 'dock panel');
  await expect(notice(page)).toHaveText('Docked Rates 2026-10-27 back into the area of Rates 2026-10-27 · 2.');
  await expect(floating(page)).toHaveCount(0);
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');

  // A floating area of two panels: Float from a tab's menu places the area whole, and the menu offers Dock beside it.
  await tab(page, 'Rates 2026-10-27').click();
  await command(page, 'float panel', 'centre');
  await expect(floating(page)).toHaveCount(1);
  await tab(page, 'Pricing DB').click({ button: 'right' });
  const pricing = page.getByRole('menu', { name: 'Pricing DB', exact: true });
  await pricing.getByRole('menuitem', { name: /^Send to/ }).hover();
  await page.getByRole('menuitem', { name: /^Rates 2026-10-27 send panel to/ }).click();
  await expect(floating(page).getByRole('tab')).toHaveCount(2);
  await tab(page, 'Pricing DB').click({ button: 'right' });
  await expect(pricing.getByRole('menuitem', { name: /^Dock/ })).toBeVisible();
  await pricing.getByRole('menuitem', { name: /^Float/ }).hover();
  await page.getByRole('menuitem', { name: /^Right/ }).click();
  await expect(floating(page)).toHaveCount(1);
  await expect(floating(page).getByRole('tab')).toHaveCount(2);
  await expect.poll(async () => (await box(floating(page))).x).toBeGreaterThan(room.x + room.width / 3);
});

test.describe('on a touch screen', () => {
  test.use({ hasTouch: true });

  test('ADR-0018 §3, UX03: a tab dragged with a finger onto an area\'s edge splits it there', async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    await threePanels(page);
    const room = await box(areasRoot(page));
    const from = await box(tab(page, 'Pricing DB'));
    const touch: CDPSession = await page.context().newCDPSession(page);
    const at = (x: number, y: number) => [{ x, y, id: 1, radiusX: 4, radiusY: 4, force: 1 }];
    await touch.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: at(from.x + from.width / 2, from.y + from.height / 2) });
    const [x0, y0, x1, y1] = [from.x + from.width / 2, from.y + from.height / 2, room.x + room.width - 20, room.y + room.height / 2];
    for (let i = 1; i <= 12; i++) {
      await touch.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: at(x0 + ((x1 - x0) * i) / 12, y0 + ((y1 - y0) * i) / 12) });
    }
    await touch.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
    expect((await box(areaOf(page, 'Pricing DB'))).x).toBeGreaterThan(room.x + room.width / 3);
  });
});

test('ADR-0018 §3, docs/design-guidelines.md §6: a splitter takes the keyboard, moves 24px an arrow, says where it stands, and stops at an area\'s minimum', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await threePanels(page);
  await command(page, 'split panel', 'right');
  const splitter = page.getByRole('separator', { name: 'Splitter between areas side by side' });
  const room = await box(areasRoot(page));
  const before = await box(areaOf(page, 'Rates 2026-10-27'));
  const share = (width: number) => String(Math.round((100 * width) / room.width));
  await expect(splitter).toHaveAttribute('aria-valuenow', share(before.width));
  await splitter.focus();
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => Math.round((await box(areaOf(page, 'Rates 2026-10-27'))).width)).toBe(Math.round(before.width) + 24);
  await expect(splitter).toHaveAttribute('aria-valuenow', share(before.width + 24));
  // Far to the left, the left area stops at 320px.
  for (let i = 0; i < 20; i++) await page.keyboard.press('ArrowLeft');
  await expect.poll(async () => Math.round((await box(areaOf(page, 'Rates 2026-10-27'))).width)).toBe(320);
  await expect(splitter).toBeFocused();
});

test('ADR-0018 §5, UX03: the panels\' space at 390, 768, 992 and 1440px, with the AI pane shut and open, in both schemes, draws areas where they fit and one strip where not', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await threePanels(page);
  // Three areas side by side: 960px.
  await command(page, 'split panel', 'right');
  await tab(page, 'Rates 2026-10-27').click();
  await command(page, 'split panel', 'right');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '3');

  // Pane shut: three areas from 960px; one strip below, which says why on the line's verbs that arrange.
  const shut = [[1440, 'areas'], [992, 'areas'], [768, 'strip'], [390, 'strip']] as const;
  // Pane open, its 340 to 400px are not the panels': a 1440px window leaves room for the three, a 992px one does not.
  const opened = [[1440, 'areas'], [992, 'strip'], [768, 'strip']] as const;
  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    for (const [pane, widths] of [['shut', shut], ['open', opened]] as const) {
      if (pane === 'open') await page.getByRole('button', { name: 'Show the AI pane' }).click();
      for (const [width, mode] of widths) {
        await page.setViewportSize({ width, height: 900 });
        await expect(panels(page), `${scheme}, pane ${pane}, ${width}px`).toHaveAttribute('data-mode', mode);
        if (mode === 'areas') await expect(areasRoot(page)).toHaveAttribute('data-areas', '3');
        expect(await sideways(page), `${scheme}, pane ${pane}, ${width}px: the page scrolls sideways`).toBe(0);
        expect(await panels(page).evaluate(textBelowContrast, 4.5), `${scheme}, pane ${pane}, ${width}px: text below 4.5:1`).toEqual([]);
      }
      if (pane === 'open') await page.getByRole('button', { name: 'Hide the AI pane' }).click();
    }
  }
  // In the strip a verb that arranges says when the arrangement comes back; with the room, it is as it was.
  await page.setViewportSize({ width: 900, height: 900 });
  await command(page, 'split panel', 'down');
  await expect(page.getByTestId('command-problems')).toHaveText('The arrangement needs 960px wide, so the panels are one strip until there is that room.');
  await page.keyboard.press('Escape');
  await page.setViewportSize({ width: 1440, height: 900 });
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '3');
});

test('ADR-0018 §7: a reload of the browser tab brings its panels and their arrangement back, a floating panel docking where it came from, and another tab keeps its own', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await threePanels(page);
  await command(page, 'split panel', 'right');
  await tab(page, 'Pricing DB').click();
  await command(page, 'float panel', 'centre');
  await expect(floating(page)).toHaveCount(1);

  await page.reload();
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '3');
  await expect(floating(page).getByRole('tabpanel', { name: 'Pricing DB', exact: true })).toBeVisible();
  await expect(areaOf(page, 'Rates 2026-10-27 · 2')).toBeVisible();
  await expect(areaOf(page, 'Rates 2026-10-27')).toBeVisible();
  // Where the floating panel came from is kept with the arrangement: docked, it goes back there.
  await command(page, 'dock panel');
  await expect(notice(page)).toHaveText('Docked Pricing DB back into the area of Rates 2026-10-27.');

  const other = await page.context().newPage();
  await other.goto('/');
  await expect(other.getByText('Nothing open yet.')).toBeVisible();
  await other.close();
});

test('ADR-0018 §8: the AI\'s proposal is drawn on its card, applied by Apply, put back by `arrange back`, and applied again by `arrange 1`', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await threePanels(page);
  await page.getByRole('button', { name: 'Show the AI pane' }).click();
  const ask = page.getByRole('textbox', { name: 'Ask the AI' });
  await ask.fill('put Rates 2026-10-27 · 2 and Pricing DB side by side');
  await page.keyboard.press('Enter');

  // Nothing moves until it is applied; the card draws the plan, with its words on the line.
  const card = page.getByTestId('proposal');
  await expect(card.getByRole('img', { name: 'Plan of proposal 1: Rates 2026-10-27 · 2; Pricing DB' })).toBeVisible();
  await expect(card.getByRole('code')).toHaveText('arrange 1');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');

  await card.getByRole('button', { name: 'Apply' }).click();
  await expect(notice(page)).toHaveText(/^Arranged as proposal 1: 2 areas\. .*To put back what it replaced: arrange back\.$/);
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
  const [left, right] = [await box(areaOf(page, 'Rates 2026-10-27 · 2')), await box(areaOf(page, 'Pricing DB'))];
  expect(right.x).toBeGreaterThan(left.x);

  await command(page, 'arrange back');
  await expect(notice(page)).toHaveText('Put back the arrangement proposal 1 replaced.');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '1');
  await command(page, 'arrange', '1');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '2');
});

test('ADR-0018 §5: the trace lays itself out by its area\'s width: it drills in an area under 768px, and is a tree in a wider one', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await taken(page, 'measures', 'kva ccr.spotcapital');
  await command(page, 'price');
  await expect(page.getByTestId('xva-result-status')).toHaveText('succeeded');
  // The Pricing DB split off to the right takes half the window, leaving the pricer 720px.
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await command(page, 'split panel', 'right');
  await tab(page, 'XVA ABC/LE-01').click();

  // In a 720px area the trace is a phone's: it drills in a level at a time, and nothing scrolls the page sideways.
  await command(page, 'explain', 'ccr.spotCapital');
  const view = page.getByTestId('trace-view');
  await expect(view).toHaveAttribute('data-layout', 'drill');
  expect(await sideways(page)).toBe(0);
  // Given two thirds of the row, 950px, it is a tree with the formula below it: side by side is from `md`, 992px, the
  // theme's boundary in pixels (a container query's `em` is the page's 13px). Three quarters, 1080px, puts them side
  // by side, in the same page: the trace stays open as its area widens.
  const columns = () => view.locator('.qube-trace-body').evaluate((e) => getComputedStyle(e).gridTemplateColumns.split(' ').length);
  await command(page, 'size panel', '66%');
  await expect(view).toHaveAttribute('data-layout', 'tree');
  await expect.poll(columns).toBe(1);
  await command(page, 'size panel', '75%');
  await expect(view).toHaveAttribute('data-layout', 'tree');
  await expect.poll(columns).toBe(2);
  await command(page, 'size panel', '33%');
  await expect(view).toHaveAttribute('data-layout', 'drill');
});

test('ADR-0018 §5, ADR-0010 §1: the pricer lays itself out by its area: its Existing grid takes the area\'s height, and under 768px it is the three tabs', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 40);
  await command(page, 'price');
  await expect(page.getByTestId('xva-result-status')).toHaveText('succeeded');
  const xva = 'XVA ABC/LE-01';
  const pricerPage = page.locator('.qube-panel-page').filter({ has: page.getByTestId('xva-result') });
  const body = pricerPage.locator('.qube-panel-page-body');
  const existing = page.locator('.qube-xva-trades');
  const sections = page.getByRole('radiogroup', { name: 'Part of the page' });
  const full = page.getByTestId('xva-result-full');
  const overflow = () => body.evaluate((e) => e.scrollHeight - e.clientHeight);
  const documentScrolls = () => page.evaluate(() => document.documentElement.scrollHeight - document.documentElement.clientHeight);
  const pageWidth = (width: number) => expect(pricerPage).toHaveAttribute('data-panel-width', String(width));
  const existingHeight = async () => (await box(existing)).height;
  // The Existing grid's bottom is above the dock's top: the page's one screen, the grid scrolling and not the page.
  const aboveTheDock = async () => {
    const grid = await box(existing);
    return grid.y + grid.height <= (await box(page.getByTestId('xva-result'))).y;
  };

  // Alone in the window, the page fits its area: the Existing grid has what the other parts leave, above the dock, and
  // the page itself does not scroll (§9).
  await pageWidth(1440);
  await expect(sections).toBeHidden();
  await expect(full).toBeVisible();
  await expect.poll(overflow).toBeLessThanOrEqual(0);
  await expect.poll(existingHeight).toBeGreaterThan(192);
  await expect.poll(aboveTheDock).toBe(true);
  const tall = await existingHeight();

  // In the top half, the grid keeps its six rows' minimum and the page scrolls inside its area, not the document.
  await command(page, 'open pricing db');
  await settled(page, 'Pricing DB');
  await command(page, 'split panel', 'down');
  await tab(page, xva).click();
  await expect.poll(existingHeight).toBe(192);
  await expect.poll(overflow).toBeGreaterThan(0);
  expect(await documentScrolls()).toBe(0);
  // Maximized, its area is the panels' whole space, and the grid has its height back; put back, it is half again.
  await command(page, 'maximize panel');
  await expect.poll(existingHeight).toBe(tall);
  await command(page, 'unmaximize panel');
  await expect.poll(existingHeight).toBe(192);
  // Too short a window for the two areas one above the other: the strip, 1440px wide, where the pricer is the one
  // screen and no switch, its grid at its minimum and the document scrolling.
  await page.setViewportSize({ width: 1440, height: 480 });
  await expect(panels(page)).toHaveAttribute('data-mode', 'strip');
  await tab(page, xva).click();
  await pageWidth(1440);
  await expect(sections).toBeHidden();
  await expect(full).toBeVisible();
  await expect.poll(existingHeight).toBe(192);
  await expect.poll(documentScrolls).toBeGreaterThan(0);
  await page.setViewportSize({ width: 1440, height: 900 });
  await expect(panels(page)).toHaveAttribute('data-mode', 'areas');
  // Gathered again, the grid has the whole area's height back.
  await command(page, 'gather panels');
  await tab(page, xva).click();
  await expect.poll(existingHeight).toBe(tall);
  // Floated to the right half, a floating area under 768px wide and the window's height: the three tabs, and the
  // Trades tab's grid above its 20rem floor, which it has only where the floating area's room reaches it; docked
  // again, the one screen.
  await command(page, 'float panel', 'right');
  await expect(floating(page)).toHaveCount(1);
  // Half the window, less the floating area's edges: from 600 to 767px.
  await expect(pricerPage).toHaveAttribute('data-panel-width', /^(6\d\d|7[0-5]\d|76[0-7])$/);
  await expect(sections).toBeVisible();
  await expect(full).toBeHidden();
  await expect.poll(existingHeight).toBeGreaterThan(320);
  expect(await sideways(page)).toBe(0);
  await command(page, 'dock panel');
  await expect(floating(page)).toHaveCount(0);
  await tab(page, xva).click();
  await expect(sections).toBeHidden();
  await expect.poll(existingHeight).toBe(tall);

  // Beside the Pricing DB, in 605px, the pricer is a phone's three tabs, under its context bar, and the dock has no
  // Full height; at three quarters of the row it is the one screen again, in the same page.
  await tab(page, 'Pricing DB').click();
  await command(page, 'split panel', 'right');
  await tab(page, xva).click();
  await command(page, 'size panel', '42%');
  await pageWidth(605);
  await expect(sections).toBeVisible();
  await expect(full).toBeHidden();
  await page.getByText('Results', { exact: true }).click();
  await expect(page.getByTestId('xva-result')).toBeVisible();
  await expect(existing).toBeHidden();
  expect(await sideways(page)).toBe(0);
  await command(page, 'size panel', '75%');
  await pageWidth(1080);
  await expect(sections).toBeHidden();
  await expect(full).toBeVisible();
  await expect(existing).toBeVisible();

  // Four areas in a row need 1280px, which the AI pane leaves a 1440px window short of: the strip, as wide as the pane
  // leaves it, where the pricer is the one screen and its grid has the window's height less the other parts, the
  // document not scrolling (the strip's room, ADR-0018 §5).
  await command(page, 'gather panels');
  await newRates(page, 'Rates 2026-10-27');
  await newRates(page, 'Rates 2026-10-27 · 2');
  await command(page, 'split panel', 'right');
  await tab(page, 'Rates 2026-10-27').click();
  await command(page, 'send panel to', 'Rates 2026-10-27 · 2');
  await tab(page, 'Pricing DB').click();
  await command(page, 'split panel', 'right');
  await tab(page, 'Rates 2026-10-27').click();
  await command(page, 'split panel', 'right');
  await expect(areasRoot(page)).toHaveAttribute('data-areas', '4');
  await page.getByRole('button', { name: 'Show the AI pane' }).click();
  await expect(panels(page)).toHaveAttribute('data-mode', 'strip');
  await tab(page, xva).click();
  // The page reads what the pane leaves of the window, its 400px from `lg` taken off.
  await pageWidth(1440 - 400);
  await expect(sections).toBeHidden();
  await expect(full).toBeVisible();
  await expect.poll(existingHeight).toBeGreaterThan(192);
  await expect.poll(aboveTheDock).toBe(true);
  await expect.poll(documentScrolls).toBeLessThanOrEqual(0);
});
