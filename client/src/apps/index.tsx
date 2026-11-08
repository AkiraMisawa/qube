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
import { measuresTyped, xvaPageOf } from './xva/commands';
import { XvaWorkspacePanel } from './xva/XvaWorkspacePanel';
import { registerAiContext, registerAiEffect } from '../ai/effects';
import { registerGridFocus } from '../shell/focus';
import { httpTraceApi, type TraceApi } from '../platform/traces/api';
import { TRACE_VIEW, explainTyped, openTrace, requestTerm, traceAddressOf } from '../platform/traces/open';

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
      return <XvaWorkspacePanel panelId={panel.id} workspaceId={id} api={xvaApi} />;
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
        hint: 'a measure that keeps a trace, then a term if wanted: ccr.spotCapital rwa',
        complete: () => (xvaPageOf(xva)?.measures() ?? []).filter((m) => m.traced).map((m) => ({ value: m.id, detail: m.title })),
      }],
      // ADR-0011 §7: the trace of the case and scenario in view, at the term named, or its root.
      run: (a) => {
        const { measure, path } = explainTyped(a.measure ?? '');
        if (trace === undefined) { xvaPage().explain(measure, path); return; }
        const here = traceAddressOf(trace.params);
        if (measure === here.measure) requestTerm(trace.id, path);
        else openTrace(state, { ...here, measure }, `Trace ${measure}`, path);
      },
    },
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
    ...([['netting', 'Netting'], ['csa', 'CSA'], ['im', 'IM']] as const).map(([tab, title]): Command => ({
      id: `xva.open.${tab}`,
      words: `open ${tab}`,
      title: `Open the ${title} input tab`,
      group: 'XVA',
      ...onXva,
      run: () => xvaPage().openTab(tab),
    })),
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
