import type { Page } from '@playwright/test';
import { command, expect, open, settled, taken, test, textBelowContrast } from './fixtures';

// The Desk's first slice (ADR-0019, docs/specs/desk-position-keeper/issues/05-desk-page.md): the book an engine
// computed, read from its result files, and what-if hedges beside it. The host runs with the development settings: a
// sample cut of the engine's synthetic small test vector (server/src/Qube.Web/Samples/desk), every figure synthetic,
// and the what-if hedges' own risk from the synthetic calculator, which is Simulated and says so.

const VALUATION_DATE = '2026-11-13';
const ENGINE_COMMIT = '30341fefae1a684ed80a705225b4362129098fce';

async function newDesk(page: Page): Promise<void> {
  await command(page, 'new desk');
  await expect(page.getByRole('tab', { name: `Desk ${VALUATION_DATE}` })).toHaveAttribute('aria-selected', 'true');
  await settled(page, 'Values');
  await settled(page, 'Risk');
}

const grid = (page: Page, label: string) => page.getByLabel(label, { exact: true });
/** A grid's rows that hold cells: its header row holds none. */
const rows = (page: Page, label: string) => grid(page, label).locator('[role=row]:has([role=gridcell])');
const row = (page: Page, label: string, name: RegExp) => grid(page, label).getByRole('row', { name });

/** What the server says the position is, asked as the page asks it. */
async function position(page: Page, filter: { readonly kind?: string; readonly input?: string } = {}) {
  const workspaceId = await page.getByTestId('desk').getAttribute('data-workspace');
  const answered = await page.request.post('/api/commands/desk.position.read/1.0.0', {
    data: { inputs: { request: { workspaceId, ...filter } }, idempotencyKey: crypto.randomUUID() },
  });
  expect(answered.ok(), await answered.text()).toBe(true);
  return ((await answered.json()) as { outputs: { answer: {
    risk: { key: string; marks: string[] }[];
    pnl: { kind: string; input: string; marks: string[] }[];
    hedges: { hedge: string; marks: string[] }[];
    pnlTotalMarks?: string[];
  } } }).outputs.answer;
}

/** Shows one input's risk lines, with the keyboard: the lines of every input are more than a grid's height. */
async function showRiskOf(page: Page, input: string): Promise<void> {
  const select = page.getByRole('combobox', { name: 'Risk of' });
  await select.focus();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type(input);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(select).toHaveValue(input);
  await settled(page, 'Risk');
}

test('ADR-0019 §4: the Desk page says which book it shows: the engine and its commit, the input set and the valuation date, and its values', async ({ page }) => {
  await open(page);
  await newDesk(page);

  const identity = page.getByTestId('desk-identity');
  await expect(identity).toContainText('alpha');
  // The commit in short, and whole where a pointer or a screen reader asks for it.
  await expect(identity.getByTitle(ENGINE_COMMIT)).toHaveText(`commit ${ENGINE_COMMIT.slice(0, 7)}`);
  await expect(identity).toContainText('sample cut of synthetic/small, 128 paths');
  await expect(identity).toContainText(`valuation date ${VALUATION_DATE}`);
  await expect(identity).toContainText('result set small-128-sample');

  // Four counterparties, three metrics each, with the legal entity the result set maps each to. A cost is negative, as
  // the engine has it, and is painted as a loss (C48).
  await expect(rows(page, 'Values')).toHaveCount(12);
  const cva = row(page, 'Values', /^C0003 SYN-LE-0003 CVA /);
  await expect(cva).toContainText('-123,132,885');
  await expect(cva.locator('.ex-tone-negative').first()).toHaveText('-123,132,885');
  // The totals say what they are of: a metric summed over the counterparties, and no standard error.
  await expect(page.getByTestId('desk-totals')).toContainText('CVA -130,157,558');
  await expect(page.getByTestId('desk-totals')).toContainText('FVA -22,014,127');
});

test('ADR-0019 §3, §7: WHAT-IF, SIMULATED and FIRST ORDER are drawn from the server\'s marks on every row and total they apply to, and on no other', async ({ page }) => {
  await open(page);
  await newDesk(page);

  // With no hedge and no move nothing is marked: every figure is the engine's.
  await showRiskOf(page, 'zero_rate JPY-OIS');
  const drawn = (await position(page, { kind: 'zero_rate', input: 'JPY-OIS' })).risk.length;
  expect(drawn).toBeGreaterThan(5);
  await expect(rows(page, 'Risk')).toHaveCount(drawn);
  await expect(grid(page, 'Risk').getByText(/WHAT-IF/)).toHaveCount(0);
  await expect(page.getByTestId('desk-no-hedges')).toBeVisible();

  await taken(page, 'new', 'irs 5y jpy 1bn pay 1.2%');
  await settled(page, 'What-if hedges');
  // The hedge says who computed its risk, and that it is hypothetical and not the engine's.
  const hedge = row(page, 'What-if hedges', /^H1 /);
  await expect(hedge).toContainText('WHAT-IF · SIMULATED');
  await expect(hedge).toContainText('desk.synthetic-hedge-risk');
  expect((await position(page)).hedges).toEqual([{ hedge: 'H1', value: expect.any(Number), provider: 'desk.synthetic-hedge-risk', executionMode: 'Simulated', marks: ['WHAT-IF', 'SIMULATED'] }]);

  // Risk: each line of the hedge's curve carries the marks the server gives it, and the page draws those and no other.
  const jpy = (await position(page, { kind: 'zero_rate', input: 'JPY-OIS' })).risk;
  expect(jpy.length).toBe(drawn);
  expect(jpy.every((line) => line.marks.join(' · ') === 'WHAT-IF · SIMULATED')).toBe(true);
  await settled(page, 'Risk');
  await expect(rows(page, 'Risk')).toHaveCount(drawn);
  await expect(rows(page, 'Risk').filter({ hasText: 'WHAT-IF · SIMULATED' })).toHaveCount(drawn);
  // A curve the hedge has no risk on is the engine's alone, and says nothing.
  const usd = (await position(page, { kind: 'zero_rate', input: 'USD-OIS' })).risk;
  expect(usd.every((line) => line.marks.length === 0)).toBe(true);
  await showRiskOf(page, 'zero_rate USD-OIS');
  await expect(rows(page, 'Risk')).toHaveCount(usd.length);
  await expect(grid(page, 'Risk').getByText(/WHAT-IF/)).toHaveCount(0);

  // A move held: its predicted profit and loss is first order, on its row and on the total.
  await taken(page, 'set', 'move zero_rate/JPY-OIS move 10');
  await taken(page, 'set', 'move fx_spot/USDJPY move 1');
  const told = await position(page);
  expect(told.pnl.map((line) => `${line.kind}/${line.input}: ${line.marks.join(' · ')}`)).toEqual([
    'fx_spot/USDJPY: FIRST ORDER', 'zero_rate/JPY-OIS: WHAT-IF · SIMULATED · FIRST ORDER',
  ]);
  await settled(page, 'Moves');
  await expect(row(page, 'Moves', /^zero_rate JPY-OIS bp 10 /)).toContainText('WHAT-IF · SIMULATED · FIRST ORDER');
  const fx = row(page, 'Moves', /^fx_spot USDJPY quote 1 /);
  await expect(fx).toContainText('FIRST ORDER');
  await expect(fx).not.toContainText(/WHAT-IF/);
  // A row with no move predicts nothing, and is marked with nothing.
  await expect(row(page, 'Moves', /^zero_rate USD-OIS bp\b/)).not.toContainText('FIRST ORDER');
  const total = page.getByTestId('desk-pnl-total');
  expect(told.pnlTotalMarks).toEqual(['WHAT-IF', 'SIMULATED', 'FIRST ORDER']);
  for (const mark of ['WHAT-IF', 'SIMULATED', 'FIRST ORDER']) await expect(total.getByText(mark, { exact: true })).toBeVisible();
  await expect(total).toContainText('Book');
  await expect(total).toContainText('What-if');
  await expect(total).toContainText('Net');
});

test('ADR-0019 §4: with no result set the page says so, in the server\'s words, and shows no grid of positions', async ({ page, problems }) => {
  await open(page);
  await command(page, 'new desk', 'set=no-such-set');
  await expect(page.getByRole('tab', { name: 'Desk' })).toHaveAttribute('aria-selected', 'true');
  const said = page.getByRole('alert').filter({ hasText: 'No result set' });
  await expect(said).toContainText('no-such-set');
  await expect(grid(page, 'Values')).toHaveCount(0);
  await expect(grid(page, 'Risk')).toHaveCount(0);
  // The browser logs the refusal it was answered; nothing else went wrong.
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
});

test('ADR-0019 §2, ADR-0006: a what-if hedge is added, changed and removed with the keyboard alone, and a move typed in its cell', async ({ page, problems }) => {
  await open(page);
  await newDesk(page);

  await taken(page, 'new', 'irs 5y jpy 1bn pay 1.2%');
  await settled(page, 'What-if hedges');
  await expect(row(page, 'What-if hedges', /^H1 irs JPY pay 1bn 5y 1\.2% /)).toBeVisible();
  await taken(page, 'set', 'hedge H1 notional 2bn');
  await expect(row(page, 'What-if hedges', /^H1 irs JPY pay 2bn 5y 1\.2% /)).toBeVisible();
  // Undo is the workspace's (ADR-0016 §11): it takes the change back.
  await taken(page, 'undo');
  await expect(row(page, 'What-if hedges', /^H1 irs JPY pay 1bn 5y 1\.2% /)).toBeVisible();
  await taken(page, 'remove', 'hedge H1');
  await expect(page.getByTestId('desk-no-hedges')).toBeVisible();
  await expect(grid(page, 'What-if hedges')).toHaveCount(0);

  // A move is typed in its cell: the grid's own keys, from the place the line goes to.
  await taken(page, 'go', 'moves');
  await page.keyboard.press('ControlOrMeta+Home');
  for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
  await page.keyboard.type('25');
  await page.keyboard.press('Enter');
  await expect(row(page, 'Moves', /^zero_rate JPY-OIS bp 25 /)).toContainText('FIRST ORDER');
  // What cannot be read is refused at the cell, in the server's words, and nothing is written.
  await page.keyboard.press('ArrowUp');
  await page.keyboard.type('ten');
  await page.keyboard.press('Enter');
  // Said beside the editor, which stays open on what was typed, and to a screen reader.
  const refusal = 'zero_rate/JPY-OIS move is a number, with k, mm or bn for its scale, or bp, or empty; ten is not.';
  await expect(page.getByText(refusal).first()).toBeVisible();
  await expect(grid(page, 'Moves').getByRole('status')).toHaveText(refusal);
  await page.keyboard.press('Escape');
  await expect(row(page, 'Moves', /^zero_rate JPY-OIS bp 25 /)).toBeVisible();
  // The browser logs the refusal it was answered; nothing else went wrong.
  expect(problems.splice(0)).toEqual(['error: Failed to load resource: the server responded with a status of 422 (Unprocessable Entity)']);
});

test('ADR-0009, docs/design-guidelines.md §7, §9: the Desk page reads at a laptop\'s and a phone\'s width, in the light and the dark theme', async ({ page }) => {
  await open(page);
  await newDesk(page);
  await taken(page, 'new', 'irs 5y jpy 1bn pay 1.2%');
  await taken(page, 'set', 'move zero_rate/JPY-OIS move 10');
  await settled(page, 'Moves');
  const desk = page.getByTestId('desk');
  const sideways = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

  // A fresh browser is in the dark theme.
  for (const scheme of ['dark', 'light'] as const) {
    if (scheme === 'light') await command(page, 'toggle theme');
    await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await expect(page.getByTestId('desk-identity')).toBeVisible();
      await expect(page.getByTestId('desk-pnl-total')).toBeVisible();
      expect(await sideways(), `${scheme} at ${width}px: the page scrolls sideways`).toBe(0);
      expect(await desk.evaluate(textBelowContrast, 4.5), `${scheme} at ${width}px: text below 4.5:1`).toEqual([]);
    }
  }
});
