import type { Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, test, textBelowContrast } from './fixtures';

// Ticket 04 (docs/specs/spot-simm-page/issues/04-the-page.md): spot IM on the pricer page (ADR-0012 §3, §7 with their
// notes of 2026-11-08). Its IM-R and IM-P on both trade grids and the IM tab while it is selected, an IM-R edit, the
// Metrics with each side's governing regime and the DEMO basis, and a cell that opens the trace at its direction.
// Every name and figure is synthetic.

const trade = (page: Page, id: string) => page.getByRole('row', { name: new RegExp(`^${id} `) });
/** A cell of the New grid by the grid's own 1-based column index: the grid draws the columns in view. */
const cell = (page: Page, id: string, column: number) => trade(page, id).locator(`[aria-colindex="${column}"]`);
/** A trade grid's column header by its title, which ExGrid names with its menu (`IM-R Column menu`). */
const header = (page: Page, grid: string, title: string) =>
  page.getByLabel(grid, { exact: true }).getByRole('columnheader', { name: `${title} Column menu`, exact: true });
const sideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

// The New grid's columns while CVA and spot IM are selected: the assignments, IM-R and IM-P beside the CSA, B and A,
// XVA's PV01, Margin and PV Adj, then spot IM's scopes (ADR-0010 §6, ADR-0012 §3).
const COLUMN = { imR: 10, imP: 11, scopes: 17 } as const;

/** Puts the New grid's Focus on a cell of its first row, `right` columns from the first. */
async function focusCell(page: Page, right: number): Promise<void> {
  await page.keyboard.press('ControlOrMeta+Shift+G');
  // The grid's first key only places its Focus (ex-grid ADR-0037); the next ones move it.
  await page.keyboard.press('ControlOrMeta+Home');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < right; i++) await page.keyboard.press('ArrowRight');
}

/** Types into the New grid's first row's IM-R, and waits for the server's answer on the row. */
async function typeImR(page: Page, value: string): Promise<void> {
  await focusCell(page, COLUMN.imR - 1);
  await page.keyboard.type(value);
  await page.keyboard.press('Enter');
  await expect(cell(page, 'DRAFT-1', COLUMN.imR)).toHaveText(value);
}

/** The regime that governs collect in the SIMM tab's tree, by the mark on its row: `JFSA` of `JFSA regime`. */
async function governingRegime(page: Page): Promise<string> {
  const governing = page.getByTestId('trace-tree').locator('.qwb-trace-term[data-governs="true"]');
  await expect(governing.getByTestId('trace-governs')).toHaveText('Governs');
  return (await governing.locator('.qwb-trace-label').innerText()).replace(/ regime$/, '');
}

/**
 * Drills from the selected term to a risk factor in the tree by the keyboard, as a user does (ADR-0011 §6): Alt+→ opens
 * the term at the Focus and ↓ selects its first child, until the selected term is a factor, whose formula names a risk
 * weight. Returns the factor's id, read off the line that comes back to it.
 */
async function drillToFactor(page: Page): Promise<string> {
  const tree = page.getByTestId('trace-tree');
  const detail = page.getByTestId('trace-detail-label');
  const weight = page.locator('[data-testid="trace-parameter"][data-name="rw"]');
  await settled(page, 'Terms');
  // The page's grid is the tree while the dock shows a trace, its Focus on the term selected.
  await page.keyboard.press('ControlOrMeta+Shift+G');
  for (let level = 0; ; level++) {
    expect(level, 'a risk factor is within eight levels of a regime').toBeLessThan(8);
    // The formula pane draws the term's parameters with it, so once it names the term, a factor's weight is there.
    if (await weight.count() > 0) break;
    const label = await detail.innerText();
    await page.keyboard.press('Alt+ArrowRight');
    await expect(tree.getByRole('button', { name: `Close ${label}`, exact: true })).toHaveCount(1);
    // Its children read, so that ↓ reaches the first of them rather than the term after it.
    await expect(tree.getByText('Reading its terms…')).toHaveCount(0);
    await page.keyboard.press('ArrowDown');
    await expect(detail).not.toHaveText(label);
  }
  return (await page.getByTestId('trace-explain').locator('code').innerText()).replace(/^explain simm\.spotIm /, '');
}

async function selectSpotIm(page: Page): Promise<void> {
  // A new workspace starts on its owner's last selection, which another test may have left.
  await command(page, 'measures', 'simm.spotim');
  await expect(page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot IM' })).toHaveAttribute('aria-pressed', 'true');
}

test('ADR-0012 §3, §7: spot IM shows its columns and IM tab, refuses a wrong IM-R by name, and opens its trace at each direction', async ({ page }) => {
  // A wide desktop, so that the grids draw the columns up to IM-P without scrolling: ExGrid draws only those in view.
  await page.setViewportSize({ width: 1920, height: 1000 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await command(page, 'measures', 'cva');
  // Unselected, spot IM shows nothing of its own.
  await expect(header(page, 'New trades', 'IM-R')).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveCount(0);
  // The toolbar's own toggle adds spot IM to what Price prices.
  const toggle = page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot IM' });
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('price')).toHaveText('Price CVA Spot IM · 1 case');

  // IM-R and IM-P on both grids, a new trade's the counterparty's IM CSAs, and the trade's scopes in the compact form.
  for (const grid of ['New trades', 'Existing trades']) {
    await settled(page, grid);
    for (const title of ['IM-R', 'IM-P']) await expect(header(page, grid, title)).toBeVisible();
  }
  await expect(cell(page, 'DRAFT-1', COLUMN.imR)).toHaveText('IMR-ABC-01');
  await expect(cell(page, 'DRAFT-1', COLUMN.imP)).toHaveText('IMP-ABC-01');

  // The IM tab, by its command: the IM CSAs with their regimes from their dates, and the keyboard in it.
  await command(page, 'open', 'im');
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveAttribute('aria-selected', 'true');
  const imCsas = page.getByRole('table', { name: 'IM CSAs' });
  await expect(imCsas.getByRole('row', { name: /^IMR-ABC-01 / })).toContainText('JFSA 2022-09-01 · CFTC 2025-09-01');
  await expect(imCsas.getByRole('row', { name: /^IMP-ABC-01 / })).toContainText('DEMO-2.x');
  await expect(page.getByLabel('IM CSAs', { exact: true }).first()).toBeFocused();

  // An IM-R naming the counterparty's IM-P CSA: the trade is out of IM-R, and Price refuses spot IM alone, by name.
  await typeImR(page, 'IMP-ABC-01');
  await focusCell(page, COLUMN.scopes - 1);
  await expect(cell(page, 'DRAFT-1', COLUMN.scopes)).toHaveText(/^In · R: none · P: /);
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  // IM-R and IM-P, Before and After, say refused; CVA is priced, a slip in an IM column never blocking it.
  await expect(result.locator('td[data-status="refused"]')).toHaveCount(4);
  await expect(result.getByTestId('xva-result-reason').filter({ hasText: /^Spot IM refused: / })).toContainText("DRAFT-1's IM-R names IMP-ABC-01");
  await expect(result.locator('td[data-status="priced"]')).toHaveCount(9);

  // Back to the counterparty's IM-R CSA: priced, each side beside its governing regime, with the DEMO basis said.
  await typeImR(page, 'IMR-ABC-01');
  await command(page, 'price');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-revision')).toHaveText(/^revision \d+$/);
  await expect(result.getByTestId('xva-result-basis')).toHaveText('Spot IM · SIMM PARAMETERS DEMO');
  const imRAfter = result.getByRole('button', { name: /^IM-R After (JFSA|CFTC) [\d,]+\.\d{2}: open its trace$/ });
  await expect(imRAfter).toBeVisible();
  await expect(result.getByRole('button', { name: /^IM-P Before (JFSA|EMIR) [\d,]+\.\d{2}: open its trace$/ })).toBeVisible();
  await expect(result.getByTestId('xva-result-cell-note').first()).toHaveText(/^(JFSA|CFTC|EMIR)$/);

  // An IM-R cell opens the SIMM tab at collect, an IM-P cell at post (ADR-0012 §7's note of 2026-11-08).
  await imRAfter.click();
  await expect(page.getByRole('tab', { name: 'SIMM', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM-R · collect');
  await page.getByRole('tab', { name: 'Metrics', exact: true }).click();
  await result.getByRole('button', { name: /^IM-P Before .+: open its trace$/ }).click();
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM-P · post');

  // In both themes, at each width ADR-0009 names, every text of the page reads at 4.5:1, the IM tab and the Metrics
  // included, and the page never scrolls sideways.
  await command(page, 'dock restore');
  await page.getByRole('tab', { name: 'Metrics', exact: true }).click();
  await command(page, 'open', 'im');
  await expect(imCsas).toBeVisible();
  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    for (const width of [1440, 992, 768]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(imCsas).toBeVisible();
      await expect(result.getByTestId('xva-result-basis')).toBeVisible();
      expect(await sideways(page), `${scheme} at ${width}px: the page scrolls sideways`).toBe(0);
      expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at ${width}px: text below 4.5:1`).toEqual([]);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByText('Inputs', { exact: true }).click();
    await expect(imCsas).toBeVisible();
    expect(await sideways(page)).toBe(0);
    expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 390px, Inputs: text below 4.5:1`).toEqual([]);
    await page.getByText('Results', { exact: true }).click();
    await expect(result.getByTestId('xva-result-basis')).toBeVisible();
    expect(await sideways(page)).toBe(0);
    expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 390px, Results: text below 4.5:1`).toEqual([]);
    await page.getByText('Trades', { exact: true }).click();
  }
});

test('ADR-0010 §7 with §3: the SIMM tab draws the case in view, a case\'s cell opens it on that case, and the dock\'s selector moves it', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await selectSpotIm(page);
  await command(page, 'vary', 'DRAFT-1 notional 5bn');
  await expect(page.getByTestId('price')).toHaveText(/ · 2 cases$/);
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');

  // The case's IM-R After opens the SIMM tab on that case, which the selector names.
  const larger = result.getByRole('table', { name: 'Metrics' }).getByRole('row', { name: /^DRAFT-1 notional 5bn / });
  await larger.getByRole('button', { name: /^IM-R After .+: open its trace$/ }).click();
  await expect(page.getByRole('tab', { name: 'SIMM', exact: true })).toHaveAttribute('aria-selected', 'true');
  const selector = result.getByRole('combobox', { name: 'Case' });
  await expect(selector.locator('option:checked')).toHaveText('DRAFT-1 notional 5bn');
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM-R · collect');
  await settled(page, 'Terms');
  const cards = page.getByTestId('trace-card');
  await expect(cards).toHaveCount(2);
  const ofCase = await cards.first().innerText();

  // The Base's trace is its own: its new trade is smaller, so collect reads otherwise.
  await selector.selectOption({ label: 'Base' });
  await expect(selector.locator('option:checked')).toHaveText('Base');
  await settled(page, 'Terms');
  await expect(cards).toHaveCount(2);
  await expect(cards.first()).not.toHaveText(ofCase);
});

// Akira, 2026-11-07: computing without Before gives After alone, and its drill-down works as both sides' does.
test('ADR-0012 §7, ADR-0011 §4: with Before not priced, spot IM is After alone, and its IM-R opens the trace at collect on After', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await selectSpotIm(page);
  await command(page, 'before', 'none grid=existing');
  await expect(page.getByTestId('price')).toHaveText(/^Price After only · /);
  await command(page, 'price');

  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.getByTestId('xva-result-note')).toHaveText('After only: Before was not priced');
  await expect(result.getByRole('columnheader', { name: 'IM-R Before' })).toHaveCount(0);
  await result.getByRole('button', { name: /^IM-R After (JFSA|CFTC) [\d,]+\.\d{2}: open its trace$/ }).click();

  await expect(page.getByTestId('trace-one-side')).toHaveText('After only: Before was not priced.');
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM-R · collect');
  await settled(page, 'Terms');
  await expect(page.getByTestId('trace-tree').getByRole('columnheader', { name: 'Before', exact: true })).toHaveCount(0);

  // Each direction's card has After alone, with no Before and no Diff.
  const cards = page.getByTestId('trace-card');
  await expect(cards).toHaveCount(2);
  for (const card of [cards.nth(0), cards.nth(1)]) {
    await expect(card).toContainText('After (USD)');
    await expect(card.getByText(/^(Before|Diff) /)).toHaveCount(0);
  }

  // The governing regime's trades on After alone, named by the module: each trade's share, no Diff, and they add up
  // to the regime's.
  await command(page, 'explain', `simm collect/${await governingRegime(page)}`);
  const entities = page.getByTestId('trace-entities');
  await settled(page, 'Entities below the term');
  await expect(entities.getByRole('columnheader', { name: 'After (USD)', exact: true })).toBeVisible();
  await expect(entities.getByRole('columnheader', { name: /^(Before|Diff) / })).toHaveCount(0);
  await expect(page.getByTestId('trace-entities-check')).toHaveText(/^They add up to its allocation: -?[\d,]+\.\d{2}$/);

  // Down at a risk factor, its trades on After alone too.
  await drillToFactor(page);
  await settled(page, 'Entities below the term');
  await expect(entities.getByRole('columnheader', { name: /^(Before|Diff) / })).toHaveCount(0);
  await expect(page.getByTestId('trace-entities-check')).toHaveText(/^They add up to its allocation: -?[\d,]+\.\d{2}$/);
});

test('ADR-0012 §2, §4, ADR-0010 §10: a counterparty with no IM CSA says so, its IM is not priced and says why, and spot IM deselected takes its columns and tab away', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1000 });
  await open(page);
  await newWorkspace(page, 'XYZ/LE-03', 3);
  await command(page, 'measures', 'cva simm.spotim');
  const toggle = page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot IM' });
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');

  // The IM tab says the counterparty exchanges no initial margin, in words, where a table of IM CSAs would be empty.
  await command(page, 'open', 'im');
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('XYZ/LE-03 has no IM CSA: it exchanges no initial margin, so spot IM has no scope.')).toBeVisible();
  await expect(page.getByRole('table', { name: 'IM CSAs' })).toHaveCount(0);

  // Priced, spot IM has no figure on either side, never 0, and says why without saying it failed; CVA is priced.
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');
  await expect(result.locator('td[data-status="notPriced"]').filter({ hasText: /^not priced$/ })).toHaveCount(4);
  await expect(result.getByTestId('xva-result-reason')).toHaveText('Spot IM not priced: XYZ/LE-03 has no IM CSA: it exchanges no initial margin.');
  // A side with no figure opens no trace.
  await expect(result.getByRole('button', { name: /^IM-[RP] / })).toHaveCount(0);
  await expect(result.locator('td[data-status="priced"]').first()).toBeVisible();

  // Its SIMM tab says the same, rather than reading a trace that was never written.
  await command(page, 'open result', 'simm');
  await expect(result.getByTestId('xva-result-tab-unpriced')).toHaveText('Spot IM not priced: XYZ/LE-03 has no IM CSA: it exchanges no initial margin.');
  await expect(page.getByTestId('trace-view')).toHaveCount(0);
  await command(page, 'dock restore');

  // Deselected, spot IM takes its IM-R and IM-P columns and its IM tab with it, and Inputs fall back to Netting.
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('tab', { name: 'IM', exact: true })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Netting' })).toHaveAttribute('aria-selected', 'true');
  for (const grid of ['New trades', 'Existing trades']) {
    await settled(page, grid);
    for (const title of ['IM-R', 'IM-P']) await expect(header(page, grid, title)).toHaveCount(0);
  }
  await expect(page.getByTestId('price')).toHaveText('Price CVA · 1 case');
});

test('ADR-0012 §6, §7: the SIMM tab shows a card per direction, marks the regime that governs, and drills down to the trades and CRIF lines, which add up on each side', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 5);
  await selectSpotIm(page);
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');

  // The IM-R cell: the SIMM tab over the whole page, IM-R's card chosen, each card saying which regime governs, and the
  // rate the page's figures were converted at said once above them.
  await result.getByRole('button', { name: /^IM-R After .+: open its trace$/ }).click();
  await expect(page.getByRole('tab', { name: 'SIMM', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.qwb-xva')).toHaveAttribute('data-dock', 'full');
  await expect(page.getByTestId('trace-tab-caption')).toHaveText(/^Computed in USD; shown in ([A-Z]{3}) at [\d.]+ \1 per USD, the pinned FX of \d{4}-\d{2}-\d{2}\.$/);
  const cards = page.getByTestId('trace-card');
  await expect(cards).toHaveCount(2);
  await expect(cards.nth(0)).toHaveAttribute('aria-pressed', 'true');
  await expect(cards.nth(0)).toContainText('IM-R · collect');
  await expect(cards.nth(1)).toContainText('IM-P · post');
  await expect(page.getByTestId('trace-card-governing').first()).toHaveText(/^(JFSA|CFTC) regime governs$/i);
  // A card roots the tree at its direction, and back.
  await cards.nth(1).click();
  await expect(cards.nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM-P · post');
  await expect(page.getByTestId('trace-title')).toHaveText('IM-P · post');
  await cards.nth(0).click();
  await expect(page.getByTestId('trace-detail-label')).toHaveText('IM-R · collect');

  // Under collect, one regime governs and the other is marked as not governing.
  await settled(page, 'Terms');
  const regime = await governingRegime(page);
  await expect(page.getByTestId('trace-tree').locator('.qwb-trace-term[data-governs="false"]').getByTestId('trace-governs')).toHaveText('Not governing');

  // The governing regime: its trades, largest change first, each side adding up to the regime's allocation, and the
  // line that comes back to it, copied whole.
  await command(page, 'explain', `simm.spotIm collect/${regime}`);
  await expect(page.getByTestId('trace-detail-label')).toHaveText(`${regime} regime`);
  await expect(page.getByTestId('trace-explain')).toContainText(`explain simm.spotIm collect/${regime}`);
  await page.getByTestId('trace-explain-copy').click();
  await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(`explain simm.spotIm collect/${regime}`);
  await settled(page, 'Entities below the term');
  const entities = page.getByTestId('trace-entities');
  await expect(entities.getByRole('row', { name: /^DRAFT-1 / })).toContainText('After only');
  await expect(page.getByTestId('trace-entities-check')).toHaveText(/^They add up to its allocation: Before -?[\d,]+\.\d{2} · After -?[\d,]+\.\d{2}$/);
  // Its CRIF lines too, at every depth below it (ADR-0012 §6: every term shows its trades and CRIF lines).
  const views = page.getByTestId('trace-below-view');
  await expect(views).toContainText('By trade');
  await views.getByText('By CRIF line').click();
  const leaves = page.getByTestId('trace-leaves');
  await expect(leaves).toContainText('Rows at every depth below');
  await expect(leaves.getByText(/^\d+ in all$/)).toBeVisible();
  await expect(entities).toHaveCount(0);
  // And back by its command (ADR-0006).
  await command(page, 'trace rows', 'entities');
  await expect(entities).toBeVisible();

  // A risk factor, drilled down to by its formula's terms: its own parameters shown, those of the terms above folded.
  const factor = await drillToFactor(page);
  expect(factor.startsWith(`collect/${regime}/`), `${factor} lies under collect/${regime}`).toBe(true);
  await expect(page.getByTestId('trace-parameter').and(page.locator('[data-name="rw"]'))).toBeVisible();
  await expect(page.locator('[data-testid="trace-parameter"][data-name="rho"]')).toHaveCount(0);
  await command(page, 'trace parameters', 'show');
  await expect(page.locator('[data-testid="trace-parameter"][data-name="rho"]')).toBeVisible();
  await expect(page.getByTestId('trace-parameters-above')).toHaveAttribute('aria-expanded', 'true');

  // Its trades, each with how many CRIF lines make its share, and those lines: the server sums the contributions
  // (SimmPricerApiTests); here each trade's lines are the ones listed for it.
  await settled(page, 'Entities below the term');
  const first = entities.getByRole('row').nth(1);
  const trade = (await first.locator('[aria-colindex="1"]').innerText()).trim();
  const [, after] = (await first.locator('[aria-colindex="5"]').innerText()).split(' · ');
  expect(after, `${trade} has lines on After at ${factor}`).toMatch(/^\d+$/);
  await command(page, 'trace rows', 'rows');
  await settled(page, 'Leaf rows');
  await expect(leaves.getByRole('row').filter({ has: page.locator('[aria-colindex="2"]', { hasText: new RegExp(`^${trade}$`) }) })).toHaveCount(Number(after));

  // Restored, the dock gives the page back its inputs and grids.
  await page.getByTestId('xva-result-full').click();
  await expect(page.locator('.qwb-xva')).toHaveAttribute('data-dock', 'part');

  // Widened again, in both themes and at each width ADR-0009 names, every text reads at 4.5:1 and nothing scrolls the
  // page sideways, the matrices included; on a phone the trace drills in a level at a time.
  await command(page, 'dock full');
  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    for (const width of [1440, 992, 768]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(page.getByTestId('trace-view')).toHaveAttribute('data-layout', 'tree');
      // The view keeps its fold from pane to pane, so the matrices above are drawn, and measured, at every width.
      await expect(page.locator('[data-testid="trace-parameter"][data-name="rho"]')).toBeVisible();
      expect(await sideways(page), `${scheme} at ${width}px: the page scrolls sideways`).toBe(0);
      expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at ${width}px: text below 4.5:1`).toEqual([]);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByText('Results', { exact: true }).click();
    await expect(page.getByTestId('trace-view')).toHaveAttribute('data-layout', 'drill');
    expect(await sideways(page), `${scheme} at 390px: the page scrolls sideways`).toBe(0);
    expect(await page.locator('.qwb-xva').evaluate(textBelowContrast, 4.5), `${scheme} at 390px: text below 4.5:1`).toEqual([]);
  }

  // On a phone the regimes are a list, and say which governs as the tree's rows do.
  await command(page, 'explain', 'simm collect');
  const children = page.getByTestId('trace-children');
  await expect(children.locator('[data-governs="true"]').getByTestId('trace-governs')).toHaveText('Governs');
  await expect(children.locator('[data-governs="false"]').getByTestId('trace-governs')).toHaveText('Not governing');
});

test('ADR-0010 §4 with ADR-0012 §7: the SIMM tab draws the market the dock\'s scenario selector shows', async ({ page }) => {
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await selectSpotIm(page);
  await command(page, 'scenario', 'rates jpy_ois +100bp; usd_sofr +100bp; usdjpy -10%');
  await expect(page.getByTestId('price')).toHaveText(/ · 1 case × 2 markets$/);
  await command(page, 'price');
  const result = page.getByTestId('xva-result');
  await expect(result.getByTestId('xva-result-status')).toHaveText('succeeded');

  await command(page, 'open result', 'simm');
  await expect(page.getByRole('tab', { name: 'SIMM', exact: true })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Terms');
  const cards = page.getByTestId('trace-card');
  await expect(cards).toHaveCount(2);
  const ofBase = await cards.first().innerText();

  // The scenario's trace is its own: its shifted curves and FX give another CRIF, so collect reads otherwise.
  const selector = result.getByRole('combobox', { name: 'Scenario' });
  await selector.selectOption({ label: 'rates' });
  await expect(selector.locator('option:checked')).toHaveText('rates');
  await settled(page, 'Terms');
  await expect(cards).toHaveCount(2);
  await expect(cards.first()).not.toHaveText(ofBase);
});
