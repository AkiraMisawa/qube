import type { Page } from '@playwright/test';
import { command, expect, newWorkspace, open, settled, test, textBelowContrast } from './fixtures';

// Ticket 07 (docs/specs/spot-ccr-capital/issues/07-trace-view.md): one view draws every trace (ADR-0011 §6), opened from a
// figure of the dock or by `explain` (§7), on both sides, on one side, and on a phone.

const view = (page: Page) => page.getByTestId('trace-view');
const term = (page: Page) => page.getByTestId('trace-detail-label');
const sideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
/** The formula's lines, by what each says: the symbols, a side's values, or the Diff. */
const lines = (page: Page) => page.getByTestId('trace-formula').locator('[data-side]').evaluateAll((all) => all.map((l) => l.getAttribute('data-side')));

/** A workspace of ABC/LE-01 priced with spot capital on; `before: 'none'` leaves its existing trades out of Before. */
async function priced(page: Page, before: 'ticked' | 'none'): Promise<void> {
  await newWorkspace(page, 'ABC/LE-01', 3);
  // A new workspace starts on its owner's last selection, which another test may have left.
  await command(page, 'measures', 'kva ccr.spotcapital');
  await expect(page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot CCR capital' })).toHaveAttribute('aria-pressed', 'true');
  if (before === 'none') {
    await command(page, 'before', 'none grid=existing');
    await expect(page.getByTestId('price')).toHaveText(/^Price After only · /);
  }
  await command(page, 'price');
  await expect(page.getByTestId('xva-result-status')).toHaveText('succeeded');
}

test('ADR-0011 §6: a figure opens its trace, each side\'s values typeset in its formula, and its terms open by pointer, keyboard and `explain`', async ({ page }) => {
  await open(page);
  await priced(page, 'ticked');
  await page.getByTestId('xva-result').getByRole('button', { name: /^Capital After .+: open its trace$/ }).click();

  await expect(view(page)).toHaveAttribute('data-layout', 'tree');
  await expect(page.getByTestId('trace-title')).toHaveText('Spot CCR capital');
  await expect(page.getByTestId('trace-simulated')).toBeVisible();
  await expect(term(page)).toHaveText('Spot CCR capital');
  // The formula with its symbols, then with Before's values, After's and the Diff, each typeset by KaTeX.
  await expect.poll(() => lines(page)).toEqual(['symbolic', 'before', 'after', 'diff']);
  await expect(page.getByTestId('trace-formula').locator('.katex')).toHaveCount(4);
  await expect(view(page).locator('.qwb-tex-error')).toHaveCount(0);

  // An operand of the formula opens its term, which the tree then shows below its parent.
  await page.getByTestId('trace-operand').filter({ hasText: 'Risk-weighted assets' }).click();
  await expect(term(page)).toHaveText('Risk-weighted assets');
  await expect(page.getByTestId('trace-path')).toHaveText('Spot CCR capital›Risk-weighted assets');
  const tree = page.getByTestId('trace-tree');
  await expect(tree).toHaveAttribute('data-rows', '2');

  // The keyboard: the panel's grid is the tree, its Focus on the term just opened. Alt+→ opens it, ↓ selects the next
  // term, Alt+← on a closed term goes to its parent and on an open one closes it; → and ← stay the grid's.
  await settled(page, 'Terms');
  await page.keyboard.press('ControlOrMeta+Shift+G');
  await page.keyboard.press('Alt+ArrowRight');
  await expect(tree).toHaveAttribute('data-rows', '4');
  await page.keyboard.press('ArrowDown');
  await expect(term(page)).toHaveText('Risk weight');
  // The risk weight is a decision: its steps in order, each with its outcome.
  await expect(page.getByTestId('trace-steps')).toContainText('Exposure class');
  await page.keyboard.press('Alt+ArrowLeft');
  await expect(term(page)).toHaveText('Risk-weighted assets');
  await page.keyboard.press('Alt+ArrowLeft');
  await expect(tree).toHaveAttribute('data-rows', '2');

  // `explain` on the trace opens a term by its path: the table row the lookup took is marked in the matrix.
  await command(page, 'explain', 'ccr.spotCapital rw/table');
  await expect(term(page)).toHaveText('Table weight');
  await expect(page.getByTestId('trace-parameters').locator('[data-row="marked"]').first()).toBeVisible();
  await expect(page.getByTestId('trace-parameters')).toContainText('lookup');

  // Down to a netting set's collateral, whose rows below are a grid of their own, paged by the server.
  await command(page, 'explain', 'ccr.spotCapital ead');
  await expect(tree.getByText('EAD of NS-001 CSA-A', { exact: true })).toBeVisible();
  await command(page, 'explain', 'ccr.spotCapital NS-001 CSA-A/m/rc/c');
  await expect(term(page)).toHaveText('Collateral after haircuts');
  const leaves = page.getByTestId('trace-leaves');
  await settled(page, 'Leaf rows');
  await expect(leaves).toContainText(/Before \d+ · After \d+/);
  await expect(page.getByLabel('Side the rows show')).toBeVisible();
  // The row at the Focus has its leaf set's formulas typeset with its values.
  await leaves.getByRole('row').filter({ has: page.getByRole('gridcell') }).first().getByRole('gridcell').first().click({ force: true });
  await expect(page.getByTestId('trace-leaf-formulas').locator('.katex').first()).toBeVisible();
  await expect(view(page).locator('.qwb-tex-error')).toHaveCount(0);
});

// Akira, 2026-11-07: computing without Before gives After alone, so no comparison is wanted, and drilling down still
// works. A case priced on one side is drawn on that side alone, said once (ADR-0011 §4).
test('ADR-0011 §4, §6: with Before not priced, the trace is drawn on After alone, says so once, and still drills down', async ({ page }) => {
  await open(page);
  await priced(page, 'none');
  await command(page, 'explain', 'ccr.spotCapital');

  await expect(view(page)).toHaveAttribute('data-layout', 'tree');
  await expect(page.getByTestId('trace-one-side')).toHaveText('After only: Before was not priced.');
  await settled(page, 'Terms');
  const tree = page.getByTestId('trace-tree');
  await expect(tree.getByRole('columnheader', { name: 'After', exact: true })).toBeVisible();
  await expect(tree.getByRole('columnheader', { name: 'Before', exact: true })).toHaveCount(0);
  await expect(tree.getByRole('columnheader', { name: 'Diff', exact: true })).toHaveCount(0);
  await expect.poll(() => lines(page)).toEqual(['symbolic', 'after']);

  await page.getByTestId('trace-operand').filter({ hasText: 'Risk-weighted assets' }).click();
  await expect(term(page)).toHaveText('Risk-weighted assets');
  await expect.poll(() => lines(page)).toEqual(['symbolic', 'after']);
  await command(page, 'explain', 'ccr.spotCapital NS-001 CSA-A/m/rc/c');
  await expect(term(page)).toHaveText('Collateral after haircuts');
  await settled(page, 'Leaf rows');
  const leaves = page.getByTestId('trace-leaves');
  // One side's rows, so no choice of side.
  await expect(leaves).toContainText(/Rows below\s*After \d+/);
  await expect(leaves).not.toContainText('Before');
  await expect(page.getByLabel('Side the rows show')).toHaveCount(0);
});

// Below `sm` the view drills in a level at a time (ADR-0011 §6, design guidelines §9), and every text reads at 4.5:1 in
// both themes, there and on a desktop screen (§7).
test('ADR-0011 §6, docs/design-guidelines.md §7, §9: at 390px the trace drills in and back, never scrolls sideways, and reads at 4.5:1', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await open(page);
  await newWorkspace(page, 'ABC/LE-01', 3);
  await command(page, 'measures', 'kva ccr.spotcapital');
  await expect(page.getByRole('group', { name: 'Measures' }).getByRole('button', { name: 'Spot CCR capital' })).toHaveAttribute('aria-pressed', 'true');
  await page.getByText('Results', { exact: true }).click();
  await command(page, 'price');
  await expect(page.getByTestId('xva-result-status')).toHaveText('succeeded');
  await command(page, 'explain', 'ccr.spotCapital');

  await expect(view(page)).toHaveAttribute('data-layout', 'drill');
  await expect(term(page)).toHaveText('Spot CCR capital');
  const children = page.getByTestId('trace-children');
  await children.getByRole('button', { name: /^Risk-weighted assets/ }).click();
  await expect(term(page)).toHaveText('Risk-weighted assets');
  await children.getByRole('button', { name: /^EAD of the counterparty/ }).click();
  await expect(term(page)).toHaveText('EAD of the counterparty');
  await expect(children.getByRole('button', { name: /^EAD of NS-001 CSA-A/ })).toBeVisible();
  expect(await sideways(page)).toBe(0);
  await page.getByTestId('trace-path').getByRole('button', { name: 'Spot CCR capital' }).click();
  await expect(term(page)).toHaveText('Spot CCR capital');
  expect(await sideways(page)).toBe(0);

  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(view(page)).toHaveAttribute('data-layout', 'drill');
    expect(await page.locator('.qwb-trace').evaluate(textBelowContrast, 4.5), `${scheme} at 390px: text below 4.5:1`).toEqual([]);
    await page.setViewportSize({ width: 1440, height: 900 });
    await expect(view(page)).toHaveAttribute('data-layout', 'tree');
    await settled(page, 'Terms');
    expect(await page.locator('.qwb-trace').evaluate(textBelowContrast, 4.5), `${scheme} at 1440px: text below 4.5:1`).toEqual([]);
  }
});
