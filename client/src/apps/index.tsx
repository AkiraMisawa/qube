import { lazy, Suspense, useCallback, useState, type ReactNode } from 'react';
import { Button, Group, Modal, NumberInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { Command } from '../platform/commands/commands';
import { termCompletions, termParts, type ProductDescriptor } from '../platform/products';
import { parserOf } from './rates/lines';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import type { Panel, WorkspaceState } from '../platform/workspace/workspaceStore';
import { PricingDbPanel } from './history/PricingDbPanel';
import { httpRatesApi, type RatesApi } from './rates/api';
import { ratesCommandsOf } from './rates/commands';
import { RatesWorkspacePanel } from './rates/RatesWorkspacePanel';
import { httpXvaApi, type XvaApi } from './xva/api';
import { measureNamed, measuresTyped, setTyped, varyTyped, xvaPageOf } from './xva/commands';
import { generatorParts, scenarioParts, scenarioTyped } from './xva/scenarios';
import { XvaWorkspacePanel } from './xva/XvaWorkspacePanel';
import { registerAiContext, registerAiEffect } from '../ai/effects';
import { registerGridFocus } from '../shell/focus';
import { httpTraceApi, type TraceApi } from '../platform/traces/api';
import { TRACE_VIEW, explainTyped, openTrace, requestTerm, setTraceView, traceAddressOf } from '../platform/traces/open';

// The trace view brings KaTeX and its fonts, read only once a trace is opened.
const TraceView = lazy(() => import('../platform/traces/TraceView').then((m) => ({ default: m.TraceView })));

// The fixed applications (docs/01): pages a domain offers beside the composed workflows. Each is reached
// through commands, so the command line and a button cannot differ (ADR-0006).

export const XVA_WORKSPACE = 'xva.workspace';
export const PRICING_DB = 'history.pricingDb';
export const RATES_WORKSPACE = 'rates.workspace';
const NEW_XVA = 'xva.new';
const NEW_RATES = 'rates.new';

let xvaApi: XvaApi = httpXvaApi;
/** For tests: the XVA endpoints a page talks to. */
export function setXvaApi(api: XvaApi): void {
  xvaApi = api;
}

let traceApi: TraceApi = httpTraceApi;
/** For tests: the trace endpoints a trace panel reads. */
export function setTraceApi(api: TraceApi): void {
  traceApi = api;
}

let ratesApi: RatesApi = httpRatesApi;
/** For tests: the Rates endpoints a page talks to. */
export function setRatesApi(api: RatesApi): void {
  ratesApi = api;
}

// What the AI pane knows of Rates (ADR-0007 §7, §8): an open workspace is the context a request acts on, a line the AI
// creates opens its workspace behind the active panel, and what the AI changes is fetched again where it is shown.
registerAiContext(RATES_WORKSPACE, (panel) => panel.params.workspaceId === undefined ? undefined : {
  description: 'The Rates workspace open in the active panel',
  value: JSON.stringify({ kind: 'rates.workspace', workspaceId: panel.params.workspaceId }),
});
registerAiEffect('rates.workspace.create', (outputs, { workspace }) => {
  const made = outputs.workspace as { readonly workspaceId: string; readonly valuationDate: string };
  const title = `Rates ${made.valuationDate}`;
  return { panelId: workspace.openPanelBehind(RATES_WORKSPACE, title, { workspaceId: made.workspaceId }), title };
});
// `entity.create` is the kernel's (ADR-0008 §5); a Rates product's line is fetched again where its workspace is shown,
// and its workspace opened behind the panel in use when it is not open yet.
registerAiEffect('entity.create', (outputs, { workspace, queryClient }) => {
  const entity = outputs.entity as { readonly workspaceId: string; readonly product: string };
  if (!entity.product.startsWith('rates.')) return undefined;
  void queryClient.invalidateQueries({ queryKey: ['rates', entity.workspaceId] });
  const open = workspace.panels.some((p) => p.kind === 'app' && p.app === RATES_WORKSPACE && p.params.workspaceId === entity.workspaceId);
  if (open) return undefined;
  const valuationDate = queryClient.getQueryData<{ readonly valuationDate: string }>(['rates', entity.workspaceId])?.valuationDate;
  const title = valuationDate === undefined ? 'Rates' : `Rates ${valuationDate}`;
  return { panelId: workspace.openPanelBehind(RATES_WORKSPACE, title, { workspaceId: entity.workspaceId }), title };
});
// What the AI pane knows of XVA (ADR-0010 §9): the workspace open is the one `vary`, `add trades` and `set` act on, and
// what they change is read again where the page shows it. The page itself takes up a revision someone else made.
registerAiContext(XVA_WORKSPACE, (panel) => panel.params.workspaceId === undefined ? undefined : {
  description: 'The XVA workspace open in the active panel',
  value: JSON.stringify({ kind: 'xva.workspace', workspaceId: panel.params.workspaceId }),
});
for (const capability of ['xva.vary', 'xva.trades.add', 'xva.set'])
  registerAiEffect(capability, (outputs, { queryClient }) => {
    const answer = outputs.answer as { readonly workspaceId: string };
    void queryClient.invalidateQueries({ queryKey: ['xva', answer.workspaceId] });
    return undefined;
  });
for (const capability of ['rates.live.start', 'rates.live.stop'])
  registerAiEffect(capability, (outputs, { queryClient }) => {
    const subscription = outputs.subscription as { readonly workspaceId: string };
    void queryClient.invalidateQueries({ queryKey: ['rates', subscription.workspaceId] });
    return undefined;
  });

export function AppPanel({ panel }: { readonly panel: Extract<Panel, { kind: 'app' }> }): ReactNode {
  switch (panel.app) {
    case XVA_WORKSPACE: {
      const id = panel.params.workspaceId;
      if (id === undefined) throw new Error('An XVA workspace panel names its workspace.');
      return <XvaWorkspacePanel panelId={panel.id} workspaceId={id} api={xvaApi} traceApi={traceApi} />;
    }
    case RATES_WORKSPACE: {
      const id = panel.params.workspaceId;
      if (id === undefined) throw new Error('A Rates workspace panel names its workspace.');
      return <RatesWorkspacePanel panelId={panel.id} workspaceId={id} api={ratesApi} />;
    }
    case PRICING_DB:
      return <PricingDbPanel panelId={panel.id} />;
    case TRACE_VIEW:
      return <TracePanel panel={panel} />;
    default:
      throw new Error(`No app draws ${panel.app}.`);
  }
}

function TracePanel({ panel }: { readonly panel: Extract<Panel, { kind: 'app' }> }) {
  const registerFocus = useCallback((focus: (() => void) | undefined) => registerGridFocus(panel.id, focus), [panel.id]);
  return (
    <Suspense fallback={<Text size="sm">Reading the trace…</Text>}>
      <TraceView panelId={panel.id} address={traceAddressOf(panel.params)} api={traceApi} registerFocus={registerFocus} />
    </Suspense>
  );
}

/** A command's word that must be one of a few, refused by name otherwise. */
function oneOf<T extends string>(value: string | undefined, options: readonly T[], words: string): T {
  const found = options.find((o) => o === value);
  if (found === undefined) throw new Error(`${words} needs one of ${options.join(', ')}.`);
  return found;
}

export function appCommands(state: Pick<WorkspaceState, 'showApp' | 'openAppDialog' | 'panels' | 'activePanelId'>): Command[] {
  const active = state.panels.find((p) => p.id === state.activePanelId);
  // Looked up when the command runs: the page registers its commands once it has mounted, after this list is made.
  const xvaPanel = active?.kind === 'app' && active.app === XVA_WORKSPACE ? active : undefined;
  const ratesPanel = active?.kind === 'app' && active.app === RATES_WORKSPACE ? active : undefined;
  const xva = xvaPanel?.id;
  const rates = ratesPanel?.id;
  // What an XVA command acts on, or why it cannot: every XVA command but `new xva` needs the page.
  const onXva = xvaPanel === undefined ? { unavailable: 'Open an XVA workspace first.' } : { target: xvaPanel.title };
  const trace = active?.kind === 'app' && active.app === TRACE_VIEW ? active : undefined;
  // `explain` acts on the XVA page's Run or on the trace in view.
  const explainOn = xvaPanel ?? trace;
  const page = () => {
    const commands = ratesCommandsOf(rates);
    if (commands === undefined) throw new Error(`The Rates workspace in panel ${rates ?? '(none)'} registered no commands.`);
    return commands;
  };
  const onRates = ratesPanel === undefined ? { unavailable: 'Open a Rates workspace first.' } : { target: ratesPanel.title };
  const pricedPanel = xvaPanel ?? ratesPanel;
  const priceTarget = pricedPanel === undefined ? { unavailable: 'Open an XVA or Rates workspace to price it.' } : { target: pricedPanel.title };
  const xvaPage = () => {
    const registered = xvaPageOf(xva);
    if (registered === undefined) throw new Error(`The XVA workspace in panel ${xva ?? '(none)'} registered no commands.`);
    return registered;
  };
  /** The workspace's scenarios the command line offers, read when the line asks, with their shift counts. */
  const scenarioNames = (keep: (s: { readonly included: boolean }) => boolean = () => true) =>
    (xvaPageOf(xva)?.scenarios() ?? []).filter(keep).map((s) => ({ value: s.name, detail: `${s.shifts} shift(s)${s.included ? '' : ', excluded'}` }));
  /** A scenario's name as typed; none is refused here by name, the server refuses one it does not hold. */
  const scenarioNamed = (name: string | undefined, words: string): string => {
    if (name === undefined || name.trim() === '') throw new Error(`${words} needs the scenario's name.`);
    return name.trim();
  };
  const onTraceView = trace !== undefined ? { target: trace.title } : xvaPanel !== undefined ? { target: xvaPanel.title }
    : { unavailable: 'Open a trace, or an XVA workspace whose result dock shows one, first.' };
  /** The trace view a command acts on, when it runs: the trace panel in view, or the XVA dock's trace tab. */
  const traceView = (): string => {
    if (trace !== undefined) return trace.id;
    const shown = xvaPage().traceInView();
    if (shown === undefined) throw new Error('The result dock shows no trace. Open a measure\'s own tab, such as with open result, first.');
    return shown;
  };
  const traceViewCommands = (): Command[] => [
    {
      id: 'trace.order',
      words: 'trace order',
      title: 'Order the children of each term in the trace: as the formula has them, or the largest change first',
      group: 'Traces',
      ...onTraceView,
      args: [{ name: 'order', required: true, complete: () => [{ value: 'formula', detail: 'as the formula has them' }, { value: 'size', detail: 'the largest change first' }],
        check: (v) => (v === 'formula' || v === 'size' ? undefined : 'formula or size') }],
      run: (a) => setTraceView(traceView(), { order: oneOf(a.order, ['formula', 'size'] as const, 'trace order') }),
    },
    {
      id: 'trace.below',
      words: 'trace rows',
      title: 'Show what is below the selected term by entity, or row by row',
      group: 'Traces',
      ...onTraceView,
      args: [{ name: 'by', required: true, complete: () => [{ value: 'entities', detail: 'each entity\'s share' }, { value: 'rows', detail: 'every row' }],
        check: (v) => (v === 'entities' || v === 'rows' ? undefined : 'entities or rows') }],
      run: (a) => setTraceView(traceView(), { below: oneOf(a.by, ['entities', 'rows'] as const, 'trace rows') }),
    },
    {
      id: 'trace.parameters',
      words: 'trace parameters',
      title: 'Show or fold the parameters of the terms above the selected one',
      group: 'Traces',
      ...onTraceView,
      args: [{ name: 'above', required: true, complete: () => [{ value: 'show' }, { value: 'hide' }],
        check: (v) => (v === 'show' || v === 'hide' ? undefined : 'show or hide') }],
      run: (a) => setTraceView(traceView(), { above: oneOf(a.above, ['show', 'hide'] as const, 'trace parameters') === 'show' }),
    },
  ];
  return [
    {
      id: 'xva.new',
      words: 'new xva',
      title: 'New XVA workspace for a counterparty',
      group: 'XVA',
      run: () => state.openAppDialog(NEW_XVA),
    },
    {
      id: 'workspace.price',
      words: 'price',
      title: 'Price the workspace as it stands',
      group: 'Pricing',
      ...priceTarget,
      run: () => {
        if (rates !== undefined) { page().price(); return; }
        xvaPage().price();
      },
    },
    {
      id: 'xva.measures',
      words: 'measures',
      title: 'Choose the measures Price prices, such as cva fva',
      group: 'XVA',
      ...onXva,
      args: [{
        name: 'measures', required: true, rest: true, words: true,
        hint: 'the measures to price, in any order',
        // The register is the page's, read when the line asks: the page registers once it has mounted.
        complete: (given) => {
          const registered = xvaPageOf(xva)?.measures() ?? [];
          const typed = new Set(given.measures === undefined ? [] : measuresTyped(given.measures, registered.map((m) => m.id)));
          return registered.filter((m) => !typed.has(m.id)).map((m) => ({ value: m.id, detail: m.title }));
        },
      }],
      run: (a) => {
        if (a.measures === undefined) throw new Error('measures needs the measures to price, such as cva fva.');
        xvaPage().setMeasures(measuresTyped(a.measures, xvaPage().measures().map((m) => m.id)));
      },
    },
    // Cases (ADR-0010 §3, §9): one per value of a cell, priced beside the Base by one Price.
    {
      id: 'xva.vary',
      words: 'vary',
      title: 'Make a case per value of a cell, such as vary CSA-A threshold 10mm, 5mm, 0',
      group: 'XVA',
      ...onXva,
      args: [{ name: 'cell', required: true, rest: true, hint: 'a row and a column, then the values separated by commas: CSA-A threshold 10mm, 5mm, 0' }],
      run: (a) => {
        const typed = varyTyped(a.cell ?? '');
        xvaPage().vary(typed.cell, typed.values);
      },
    },
    {
      id: 'xva.set',
      words: 'set',
      title: 'Set one value of a cell, on one row or several, such as set CSA-A threshold 5mm',
      group: 'XVA',
      ...onXva,
      args: [{ name: 'cell', required: true, rest: true, hint: 'the rows separated by commas, a column and the value: T-1001, T-1002 csa CSA-B' }],
      run: (a) => {
        const typed = setTyped(a.cell ?? '');
        xvaPage().set(typed.rows, typed.column, typed.text);
      },
    },
    {
      id: 'xva.trades.add',
      words: 'add trades',
      title: 'Make a case per new trade with A ticked, each priced alone on top of the existing trades',
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().addTrades(),
    },
    {
      id: 'xva.case.remove',
      words: 'case remove',
      title: 'Remove a case with its changes, by its title',
      group: 'XVA',
      ...onXva,
      args: [{ name: 'case', required: true, rest: true, hint: 'the case\'s title, such as CSA-A threshold 5mm' }],
      run: (a) => {
        if (a.case === undefined || a.case.trim() === '') throw new Error('case remove needs the case to remove, by its title.');
        xvaPage().removeCase(a.case.trim());
      },
    },
    {
      id: 'xva.cases.clear',
      words: 'cases clear',
      title: 'Remove every case; the Base stays',
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().clearCases(),
    },
    // Scenarios (ADR-0010 §4): a named set of market-data shifts, priced under every case by the same Price.
    {
      id: 'xva.scenario',
      words: 'scenario',
      title: 'Define a scenario with all its shifts, such as scenario s1 jpy_ois +10bp; usdjpy -5%',
      group: 'XVA',
      ...onXva,
      args: [{
        name: 'scenario', required: true, rest: true, parts: scenarioParts,
        hint: 'a name, then the shifts separated by semicolons: s1 jpy_ois +10bp; usdjpy -5%; jpy_ois 5y +3bp',
      }],
      run: (a) => {
        const typed = scenarioTyped(a.scenario ?? '');
        xvaPage().defineScenario(typed.name, typed.shifts);
      },
    },
    // A scenario by name (ADR-0010 §4): what its include box, Duplicate and Remove do on the Scenarios tab.
    ...([['include', 'Price the scenario named beside the base market', true], ['exclude', 'Leave the scenario named out of Price; its shifts stay', false]] as const)
      .map(([verb, title, included]): Command => ({
        id: `xva.scenario.${verb}`,
        words: `scenario ${verb}`,
        title,
        group: 'XVA',
        ...onXva,
        args: [{ name: 'scenario', required: true, hint: 'the scenario\'s name', complete: () => scenarioNames((s) => s.included !== included) }],
        run: (a) => xvaPage().includeScenario(scenarioNamed(a.scenario, `scenario ${verb}`), included),
      })),
    {
      id: 'xva.scenario.remove',
      words: 'scenario remove',
      title: 'Remove a scenario with all its shifts',
      group: 'XVA',
      ...onXva,
      args: [{ name: 'scenario', required: true, hint: 'the scenario\'s name', complete: () => scenarioNames() }],
      run: (a) => xvaPage().removeScenario(scenarioNamed(a.scenario, 'scenario remove')),
    },
    {
      id: 'xva.scenario.duplicate',
      words: 'scenario duplicate',
      title: 'Copy a scenario\'s shifts under a new name, to be edited',
      group: 'XVA',
      ...onXva,
      args: [
        { name: 'scenario', required: true, hint: 'the scenario to copy', complete: () => scenarioNames() },
        { name: 'copy', required: true, hint: 'the new scenario\'s name' },
      ],
      run: (a) => xvaPage().duplicateScenario(scenarioNamed(a.scenario, 'scenario duplicate'), scenarioNamed(a.copy, 'scenario duplicate')),
    },
    // The generators (ADR-0010 §4): a set at once, the line saying how many it makes and the pairs left against the cap.
    ...([['ladder', 'Make a scenario per size of one factor, such as scenario ladder jpy_ois -50bp..+50bp step 10bp',
      'a factor, a range and a step: jpy_ois -50bp..+50bp step 10bp, or jpy_ois 5y -10bp..+10bp step 5bp'],
    ['cross', 'Make a scenario per combination of several factors\' sizes, such as scenario cross jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%',
      'lists separated by x, each a factor and its sizes: jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%']] as const)
      .map(([generator, title, hint]): Command => ({
        id: `xva.scenario.${generator}`,
        words: `scenario ${generator}`,
        title,
        group: 'XVA',
        ...onXva,
        args: [{
          name: generator, required: true, rest: true, hint,
          // The page's cases and scenarios, read as the line is typed: the preview counts against them.
          parts: (text) => {
            const page = xvaPageOf(xva);
            const pricing = page?.pricing();
            return generatorParts(generator, text, page === undefined || pricing === undefined ? undefined : { ...pricing, scenarios: page.scenarios() });
          },
        }],
        run: (a) => {
          if (a[generator] === undefined || a[generator].trim() === '') throw new Error(`scenario ${generator} needs ${hint}.`);
          xvaPage().generateScenarios(generator, a[generator]);
        },
      })),
    {
      id: 'xva.scenarios.clear',
      words: 'scenarios clear',
      title: 'Remove every scenario; Price prices the base market alone',
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().clearScenarios(),
    },
    {
      id: 'xva.run.download',
      words: 'download run',
      title: 'Save the Run the page shows as a zip of its inputs, results and lineage',
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().downloadRun(),
    },
    {
      id: 'xva.quote.copy',
      words: 'copy quote',
      title: 'Copy the CVA Charge and PV each case adds, from the Run the page shows',
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().copyQuote(),
    },
    {
      id: 'trace.explain',
      words: 'explain',
      title: 'Open the calculation trace of a measure, at a term if one is named',
      group: 'Traces',
      ...(explainOn === undefined ? { unavailable: 'Open an XVA workspace that has been priced, or a trace, first.' } : { target: explainOn.title }),
      args: [{
        name: 'measure', required: true, rest: true,
        hint: 'a measure that keeps a trace, or its module, then a term if wanted: simm collect',
        complete: () => (xvaPageOf(xva)?.measures() ?? []).filter((m) => m.traced).map((m) => ({ value: m.id, detail: m.title })),
      }],
      // ADR-0011 §7: the trace of the case and scenario in view, at the term named, or its root.
      run: (a) => {
        const typed = explainTyped(a.measure ?? '');
        const path = typed.path;
        if (trace === undefined) { xvaPage().explain(typed.measure, path); return; }
        // A measure named by its module, or in another case, is the XVA page's register's to read, where one is open.
        const measure = measureNamed(typed.measure, xvaPageOf(xva)?.measures() ?? [])?.id ?? typed.measure;
        const here = traceAddressOf(trace.params);
        if (measure === here.measure) requestTerm(trace.id, path);
        else openTrace(state, { ...here, measure }, `Trace ${measure}`, path);
      },
    },
    // How the trace in view draws (ADR-0006, ADR-0011 §6), which its own controls set too: a trace panel, or the trace a
    // measure's own tab of the XVA result dock shows.
    ...traceViewCommands(),
    {
      id: 'xva.pv01.retry',
      words: 'pv01 retry',
      title: 'Ask again for every PV01 that failed',
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().retry(),
    },
    {
      id: 'xva.refresh',
      words: 'refresh',
      title: 'Acquire the trades again for the value date; your edits carry or are held',
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().refresh(),
    },
    {
      id: 'xva.currency',
      words: 'currency',
      title: 'Show the figures in a currency, such as USD',
      group: 'XVA',
      ...onXva,
      args: [{
        name: 'currency', required: true, hint: 'a currency the page offers, such as USD',
        complete: () => (xvaPageOf(xva)?.currencies() ?? []).map((c) => ({ value: c })),
      }],
      // The server refuses a currency it does not offer, by name; the line does not guess one.
      run: (a) => {
        if (a.currency === undefined) throw new Error('currency needs the currency to show the figures in.');
        xvaPage().setCurrency(a.currency.toUpperCase());
      },
    },
    // One command per input tab (ADR-0010 §9), which shows the inputs if they were collapsed. IM is spot IM's (ADR-0012 §3),
    // and the page says so when it is not selected.
    ...([['netting', 'Netting'], ['csa', 'CSA'], ['im', 'IM'], ['scenarios', 'Scenarios'], ['changes', 'Changes']] as const).map(([tab, title]): Command => ({
      id: `xva.open.${tab}`,
      words: `open ${tab}`,
      title: `Open the ${title} input tab`,
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().openTab(tab),
    })),
    // The dock's tabs (ADR-0010 §7): Metrics, or a tab a priced measure registers, such as spot IM's SIMM (ADR-0012 §7).
    {
      id: 'xva.result.open',
      words: 'open result',
      title: 'Show a tab of the result dock: Metrics, or a measure\'s own such as SIMM',
      group: 'XVA',
      ...onXva,
      args: [{
        name: 'tab', required: true,
        hint: 'the tab to show: metrics, or a priced measure\'s own',
        complete: () => (xvaPageOf(xva)?.resultTabs() ?? []).map((t) => ({ value: t.id, detail: t.title })),
      }],
      run: (a) => {
        if (a.tab === undefined) throw new Error('open result needs the tab to show, such as metrics.');
        xvaPage().openResult(a.tab);
      },
    },
    // Widening the dock over the input tabs and the grids, and putting it back (owner, 2026-11-08).
    ...([['full', true, 'Widen the result dock over the input tabs and the trade grids'], ['restore', false, 'Put the result dock back to the height its handle sets']] as const)
      .map(([word, full, title]): Command => ({ id: `xva.dock.${word}`, words: `dock ${word}`, title, group: 'XVA', ...onXva, run: () => xvaPage().setDockFull(full) })),
    // B or A for every row of a trade grid, as one edit (ADR-0010 §6): the grid the keyboard was last in, or the one named.
    ...(['before', 'after'] as const).map((side): Command => ({
      id: `xva.${side}.all`,
      words: side,
      title: `Tick or clear ${side === 'before' ? 'Before' : 'After'} on every trade of a grid`,
      group: 'XVA',
      ...onXva,
      args: [
        { name: 'which', required: true, complete: () => [{ value: 'all', detail: 'tick every row' }, { value: 'none', detail: 'clear every row' }],
          check: (v) => (v === 'all' || v === 'none' ? undefined : 'all or none') },
        { name: 'grid', required: false, hint: 'new or existing; the grid you were last in when left out',
          complete: () => [{ value: 'new' }, { value: 'existing' }], check: (v) => (v === 'new' || v === 'existing' ? undefined : 'new or existing') },
      ],
      run: (a) => xvaPage().setSide(side, a.which === 'all', a.grid === undefined ? undefined : a.grid as 'new' | 'existing'),
    })),
    {
      id: 'rates.new',
      words: 'new rates',
      title: 'New Rates workspace (IRS and CCS lines)',
      group: 'Rates',
      run: () => state.openAppDialog(NEW_RATES),
    },
    ...productCommands(rates === undefined ? [] : ratesCommandsOf(rates)?.products() ?? [], onRates, (product, terms) => page().newLine(product, terms)),
    {
      id: 'rates.defaults',
      words: 'set defaults',
      title: 'Set the workspace defaults for new lines (currency, notional, direction)',
      group: 'Rates',
      ...onRates,
      args: [
        { name: 'currency', required: false, hint: 'a currency such as JPY; none clears it' },
        { name: 'notional', required: false, hint: 'in millions (1000 is a billion), or 3bn, 500mm; none clears it', check: (v) => (v === 'none' ? undefined : parsed('leg1.notional')(v)) },
        { name: 'direction', required: false, complete: () => [{ value: 'pay' }, { value: 'receive' }, { value: 'none' }], check: (v) => (v === 'none' ? undefined : parsed('direction')(v)) },
      ],
      run: (a) => page().setDefaults({
        ...(a.currency === undefined ? {} : { currency: a.currency === 'none' ? null : a.currency.toUpperCase() }),
        ...(a.notional === undefined ? {} : { notional: a.notional === 'none' ? null : parsedValue('leg1.notional', a.notional) as number }),
        ...(a.direction === undefined ? {} : { direction: a.direction === 'none' ? null : parsedValue('direction', a.direction) as string }),
      }),
    },
    {
      id: 'rates.line.add',
      words: 'add line',
      title: 'Add an empty line to fill in the grid',
      group: 'Rates',
      ...onRates,
      args: [{ name: 'product', required: true, complete: () => [{ value: 'irs', detail: 'interest rate swap' }, { value: 'ccs', detail: 'cross-currency swap' }],
        check: (v) => (v === 'irs' || v === 'ccs' ? undefined : 'irs or ccs') }],
      run: (a) => page().addLine(a.product as 'irs' | 'ccs'),
    },
    { id: 'rates.live.start', words: 'live start', title: 'Start ticking the workspace', group: 'Rates', ...onRates, run: () => page().startLive() },
    { id: 'rates.live.stop', words: 'live stop', title: 'Stop ticking the workspace, for everyone watching', group: 'Rates', ...onRates, run: () => page().stopLive() },
    { id: 'rates.capture', words: 'capture', title: 'Save the live frame on screen as a Run', group: 'Rates', ...onRates, run: () => page().capture() },
    {
      id: 'history.open',
      words: 'open pricing db',
      title: 'Open the Pricing DB of past Runs',
      group: 'History',
      run: () => state.showApp(PRICING_DB, 'Pricing DB', {}),
    },
  ];
}

const parsed = (field: string) => (value: string): string | undefined => {
  const result = parserOf(field)!(value);
  return result.ok ? undefined : result.message;
};

function parsedValue(field: string, text: string): unknown {
  const result = parserOf(field)!(text);
  if (!result.ok) throw new Error(`${field} was checked on the line and is refused now: ${result.message}`);
  return result.value;
}

/**
 * `new <product> <terms…>` for each product the server describes (ADR-0008 §1, §5): one generic command with its
 * product named. The words are read in any order; the line completes and shows them from the description, and the
 * server reads them again, fills what its defaults supply and refuses what is missing by name.
 */
function productCommands(products: readonly ProductDescriptor[], on: Partial<Pick<Command, 'unavailable' | 'target'>>, create: (product: string, terms: string) => void): Command[] {
  return products.map((product) => ({
    id: `entity.create/${product.id}`,
    words: `new ${product.words}`,
    title: `Add ${article(product.title)} from short terms, in any order`,
    group: 'Rates',
    ...on,
    args: [{
      name: 'terms', required: true, rest: true, words: true,
      hint: `${product.fields.filter((f) => f.required).map((f) => f.name).join(', ')}; any order, name=value for the rest`,
      complete: (given) => termCompletions(product, wordsOf(given.terms)),
      parts: (text) => termParts(product, wordsOf(text)),
    }],
    run: (a) => create(product.id, a.terms ?? ''),
  }));
}

const wordsOf = (text: string | undefined) => (text ?? '').split(/\s+/).filter((w) => w !== '');
const article = (title: string) => `${/^[aeiou]/i.test(title) ? 'an' : 'a'} ${title.toLowerCase()}`;

/** The dialogs apps open. Enter submits; Escape closes. */
export function AppDialogs() {
  const dialog = useWorkspace((s) => s.appDialog);
  const close = useWorkspace((s) => s.openAppDialog);
  return (
    <>
      <Modal opened={dialog === NEW_XVA} onClose={() => close(undefined)} title="New XVA workspace" trapFocus returnFocus transitionProps={{ duration: 0 }}>
        {dialog === NEW_XVA && <NewXvaWorkspace onDone={() => close(undefined)} />}
      </Modal>
      <Modal opened={dialog === NEW_RATES} onClose={() => close(undefined)} title="New Rates workspace" trapFocus returnFocus transitionProps={{ duration: 0 }}>
        {dialog === NEW_RATES && <NewRatesWorkspace onDone={() => close(undefined)} />}
      </Modal>
    </>
  );
}

function NewXvaWorkspace({ onDone }: { readonly onDone: () => void }) {
  const workspace = useWorkspaceContext();
  const registrations = useQuery({ queryKey: ['xva', 'registrations'], queryFn: () => xvaApi.registrations(), staleTime: Infinity });
  const [registration, setRegistration] = useState<string | null>(null);
  const [valueDate, setValueDate] = useState('2026-10-26');
  const [size, setSize] = useState<number | string>(1000);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const submit = async () => {
    if (registration === null) { setError('Choose a counterparty.'); return; }
    if (typeof size !== 'number' || !Number.isInteger(size) || size < 0) { setError('The portfolio size is a whole number.'); return; }
    setBusy(true);
    try {
      const created = await xvaApi.create(registration, valueDate, size);
      workspace.store.getState().showApp(XVA_WORKSPACE, `XVA ${created.registration.key}`, { workspaceId: created.workspaceId });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <Stack gap="sm">
        <Select label="Counterparty" data-autofocus searchable value={registration} onChange={setRegistration} error={error}
          data={(registrations.data ?? []).map((r) => ({ value: r.key, label: `${r.key} · ${r.name}` }))} />
        <TextInput label="Value date" type="date" value={valueDate} onChange={(e) => setValueDate(e.currentTarget.value)} />
        <NumberInput label="Existing trades (synthetic)" min={0} max={100000} allowDecimal={false} value={size} onChange={setSize} />
        <Group justify="flex-end"><Button type="submit" loading={busy}>Create</Button></Group>
      </Stack>
    </form>
  );
}

function NewRatesWorkspace({ onDone }: { readonly onDone: () => void }) {
  const workspace = useWorkspaceContext();
  const [valuationDate, setValuationDate] = useState('2026-10-27');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const submit = async () => {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(valuationDate)) { setError('Choose a valuation date.'); return; }
    setBusy(true);
    try {
      const created = await ratesApi.create(valuationDate);
      workspace.store.getState().showApp(RATES_WORKSPACE, `Rates ${created.valuationDate}`, { workspaceId: created.workspaceId });
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <Stack gap="sm">
        <TextInput label="Valuation date" type="date" data-autofocus value={valuationDate} error={error} onChange={(e) => setValuationDate(e.currentTarget.value)} />
        <Group justify="flex-end"><Button type="submit" loading={busy}>Create</Button></Group>
      </Stack>
    </form>
  );
}
