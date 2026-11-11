import { test as base, expect, type APIRequestContext, type Page } from '@playwright/test';

/**
 * The user's settings, as each test starts (ADR-0017 §2): every one the user set is reset, as a fresh browser began
 * from nothing when the theme, the command ranking and the widths were the browser's. The tests share one server and one
 * user, so what a test sets would otherwise reach every test after it.
 */
export async function resetSettings(request: APIRequestContext): Promise<void> {
  const read = await request.get('/api/settings');
  expect(read.ok()).toBe(true);
  for (const setting of ((await read.json()) as { settings: { key: string; set: boolean }[] }).settings.filter((s) => s.set)) {
    const reset = await request.post('/api/commands/settings.reset/1.0.0', { data: { inputs: { request: { setting: setting.key } }, idempotencyKey: crypto.randomUUID() } });
    expect(reset.ok(), await reset.text()).toBe(true);
  }
}

// An unexpected console message or page error fails the test that caused it.
export const test = base.extend<{ problems: string[] }>({
  problems: [async ({ page }, use) => {
    await resetSettings(page.request);
    const problems: string[] = [];
    page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`${m.type()}: ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
    await use(problems);
    expect(problems).toEqual([]);
  }, { auto: true }],
});
export { expect };

export async function open(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByText('Nothing open yet.')).toBeVisible();
}

/**
 * Runs a command from the command line (ADR-0006): its words, then its arguments if it takes any. Enter runs what the
 * line shows it will run, so the test reads that first. A line the server reads at Enter (ADR-0016 §5) stays until it
 * is read, and the keys typed meanwhile go to nothing: the line before is gone before this one is opened.
 */
export async function command(page: Page, words: string, args?: string): Promise<void> {
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeHidden();
  await page.keyboard.press('ControlOrMeta+K');
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeFocused();
  await page.keyboard.type(args === undefined ? words : `${words} ${args}`);
  await expect(page.getByTestId('command-pending')).toContainText(words);
  await page.keyboard.press('Enter');
}

/**
 * Runs a command the server reads (ADR-0016 §5) and waits until it was taken: its line closes once the server has
 * answered and the page shows what the command left. A test that goes on with keys of its own, to a grid or to a line
 * it opens by hand, runs such a command with this: a key sent while the line is still open is the line's.
 */
export async function taken(page: Page, words: string, args?: string): Promise<void> {
  await command(page, words, args);
  await expect(page.getByRole('combobox', { name: 'Command' })).toBeHidden();
}

export const panel = (page: Page) => page.getByRole('tabpanel');

/** Opens a new XVA workspace for a counterparty with this many synthetic existing trades, and waits for its grids. */
export async function newWorkspace(page: Page, registration: string, trades: number): Promise<void> {
  await command(page, 'new xva');
  const dialog = page.getByRole('dialog', { name: 'New XVA workspace' });
  await expect(dialog.getByRole('combobox', { name: 'Counterparty' })).toBeFocused();
  await page.keyboard.type(registration);
  // The counterparties load as the dialog opens: the option is read once it is listed, never before.
  await expect(page.getByRole('option', { name: new RegExp(`^${registration} `) })).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('combobox', { name: 'Counterparty' })).toHaveValue(new RegExp(`^${registration} `));
  await dialog.getByLabel('Existing trades (synthetic)').fill(String(trades));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('tab', { name: `XVA ${registration}` })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'New trades');
  await settled(page, 'Existing trades');
}

/**
 * Awaits ExGrid's Settled Mark on the grid with this label. A key sent before a newly drawn grid has
 * settled is lost, by ExGrid's own contract (ex-grid tests/ExGrid.Browser/README.md, "Before the first
 * key of each page"), so a test waits for it before the first key and before it reads the grid.
 */
export async function settled(page: Page, label: string): Promise<void> {
  await expect(page.getByLabel(label, { exact: true }).locator('.ex-grid')).toHaveAttribute('data-ex-settled', '');
}

// docs/design-guidelines.md §7 (WCAG 1.4.3), measured in the browser, which alone knows the colours it painted.
/** Each enabled text under `root` whose colour reads below `minimum` against its backgrounds, composited as painted. */
export function textBelowContrast(root: Element, minimum: number): readonly string[] {
  // Any other colour the browser computes (ExGrid Mantine's strong colour is an oklch() relative to the primary) is read
  // back as the browser paints it in sRGB.
  const canvas = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  const painted = (css: string): readonly [number, number, number, number] => {
    if (canvas === null) throw new Error(`No canvas to read ${css} back from`);
    canvas.clearRect(0, 0, 1, 1);
    canvas.fillStyle = css;
    canvas.fillRect(0, 0, 1, 1);
    const [r, g, b, a] = canvas.getImageData(0, 0, 1, 1).data;
    if (r === undefined || g === undefined || b === undefined || a === undefined) throw new Error(`${css} painted nothing`);
    return [r, g, b, a / 255];
  };
  // A computed colour is rgb() or rgba(), or color(srgb …) in 0 to 1 where it was mixed (the light theme's text).
  const rgba = (css: string): readonly [number, number, number, number] => {
    if (/^okl(ch|ab)\(/.test(css)) return painted(css);
    const match = /^(rgba?|color)\((?:srgb\s+)?([^)]+)\)$/.exec(css);
    const parts = match?.[2]?.split(/[\s,/]+/).filter((p) => p !== '').map(Number);
    if (match === null || parts === undefined || parts.length < 3 || parts.some(Number.isNaN)) throw new Error(`Not an sRGB colour: ${css}`);
    const scale = match[1] === 'color' ? 255 : 1;
    return [(parts[0] ?? 0) * scale, (parts[1] ?? 0) * scale, (parts[2] ?? 0) * scale, parts[3] ?? 1];
  };
  const over = (top: readonly number[], bottom: readonly number[]) =>
    [0, 1, 2].map((i) => (top[i] ?? 0) * (top[3] ?? 1) + (bottom[i] ?? 0) * (1 - (top[3] ?? 1)));
  const luminance = (c: readonly number[]) => [0.2126, 0.7152, 0.0722].reduce((sum, weight, i) => {
    const v = (c[i] ?? 0) / 255;
    return sum + weight * (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
  }, 0);
  const faint: string[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const element = node.parentElement;
    const text = node.textContent?.trim() ?? '';
    if (element === null || text === '' || element.getClientRects().length === 0) continue;
    // WCAG 1.4.3 exempts a control that cannot be used.
    if (element.closest('[disabled], [data-disabled], [aria-disabled="true"]') !== null) continue;
    const layers: (readonly number[])[] = [];
    let opacity = 1;
    for (let e: Element | null = element; e !== null; e = e.parentElement) {
      const style = getComputedStyle(e);
      opacity *= Number(style.opacity);
      const fill = rgba(style.backgroundColor);
      if (fill[3] > 0 && (layers.length === 0 || (layers[layers.length - 1]?.[3] ?? 0) < 1)) layers.push(fill);
    }
    const background = layers.reduceRight<readonly number[]>((below, layer) => [...over(layer, below), 1], [255, 255, 255, 1]);
    const ink = rgba(getComputedStyle(element).color);
    const shown = over([ink[0], ink[1], ink[2], ink[3] * opacity], background);
    const [light, dark] = [luminance(shown), luminance(background)].sort((x, y) => y - x);
    const ratio = ((light ?? 0) + 0.05) / ((dark ?? 0) + 0.05);
    if (ratio < minimum) faint.push(`${text.slice(0, 40)}: ${ratio.toFixed(2)}`);
  }
  return faint;
}
