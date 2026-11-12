import { lazy, Suspense, useCallback, useState, type ReactNode } from 'react';
import { Button, Group, Modal, NumberInput, Select, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { CommandRefused, type Command } from '../platform/commands/commands';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { panelLabel, type Panel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { PricingDbPanel } from './history/PricingDbPanel';
import { httpRatesApi, type RatesApi } from './rates/api';
import { RatesWorkspacePanel } from './rates/RatesWorkspacePanel';
import { httpXvaApi, type XvaApi } from './xva/api';
import { measureNamed, xvaPageOf } from './xva/commands';
import { XvaWorkspacePanel } from './xva/XvaWorkspacePanel';
import { registerAiContext, registerAiEffect } from '../ai/effects';
import { registerGridFocus } from '../shell/places';
import { isIsoDate } from '../platform/forms';
import { IsoDateInput } from '../platform/IsoDateInput';
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
  return workspace.openPanelBehind(RATES_WORKSPACE, `Rates ${made.valuationDate}`, { workspaceId: made.workspaceId });
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
  return workspace.openPanelBehind(RATES_WORKSPACE, valuationDate === undefined ? 'Rates' : `Rates ${valuationDate}`, { workspaceId: entity.workspaceId });
});
// What the AI pane knows of XVA (ADR-0010 §9): the workspace open is the one `vary`, `add each` and the kernel's `set`,
// `reset`, `undo` and `redo` act on (ADR-0016 §3, §11), and what they change is read again where the page shows it. The
// page itself takes up a revision someone else made.
registerAiContext(XVA_WORKSPACE, (panel) => panel.params.workspaceId === undefined ? undefined : {
  description: 'The XVA workspace open in the active panel',
  value: JSON.stringify({ kind: 'xva.workspace', workspaceId: panel.params.workspaceId }),
});
// The scenario commands (ADR-0010 §4) likewise, and `price`, `refresh` and `pv01 retry` (ADR-0016 note 33): the page
// shows the Run the AI priced, and reads its grids again from the first row where a refresh moved the source.
const XVA_EDITS = ['xva.vary', 'xva.trades.add', 'xva.cds', 'xva.measures', 'xva.case.remove', 'xva.cases.clear', 'xva.scenario',
  'xva.scenario.include', 'xva.scenario.exclude', 'xva.scenario.remove', 'xva.scenario.duplicate', 'xva.scenarios.clear', 'xva.scenario.ladder',
  'xva.scenario.cross', 'xva.scenario.load', 'xva.workspace.price', 'xva.refresh', 'xva.pv01.retry'];
for (const capability of XVA_EDITS)
  registerAiEffect(capability, (outputs, { queryClient }) => {
    const answer = outputs.answer as { readonly workspaceId: string };
    void queryClient.invalidateQueries({ queryKey: ['xva', answer.workspaceId] });
    return undefined;
  });
// The kernel's verbs over a registered table, and undo and redo (ADR-0016 §3, §11), know no app: what they changed is
// whatever reads the workspace they answer for, on whichever page it is open.
for (const capability of ['entity.set', 'entity.reset', 'entity.remove', 'workspace.undo', 'workspace.redo'])
  registerAiEffect(capability, (outputs, { queryClient }) => {
    const answer = outputs.answer as { readonly workspaceId: string };
    void queryClient.invalidateQueries({ predicate: (query) => query.queryKey.includes(answer.workspaceId) });
    return undefined;
  });
for (const capability of ['rates.live.start', 'rates.live.stop'])
  registerAiEffect(capability, (outputs, { queryClient }) => {
    const subscription = outputs.subscription as { readonly workspaceId: string };
    void queryClient.invalidateQueries({ queryKey: ['rates', subscription.workspaceId] });
    return undefined;
  });
// Rates' other commands (ADR-0016 note 36) answer where they left the workspace: an empty line added, and the Runs
// `price` and `capture` make, which the page lists and the Pricing DB keeps.
for (const capability of ['rates.line.add', 'rates.workspace.price', 'rates.frame.capture'])
  registerAiEffect(capability, (outputs, { queryClient }) => {
    const answer = outputs.answer as { readonly workspaceId: string };
    void queryClient.invalidateQueries({ queryKey: ['rates', answer.workspaceId] });
    if (capability !== 'rates.line.add') void queryClient.invalidateQueries({ queryKey: ['history'] });
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
  if (found === undefined) throw new CommandRefused(`${words} needs one of ${options.join(', ')}.`);
  return found;
}

/** What a command of the page in this panel says of itself: what it acts on, and that the empty line lists it first
 * (ADR-0006's note 3 of 2026-11-11). */
const onPage = (panel: Panel): Pick<Command, 'target' | 'page'> => ({ target: panelLabel(panel), page: true });

export function appCommands(state: Pick<WorkspaceState, 'showApp' | 'openAppDialog' | 'panels' | 'activePanelId'>): Command[] {
  const active = state.panels.find((p) => p.id === state.activePanelId);
  // Looked up when the command runs: the page registers its commands once it has mounted, after this list is made.
  const xvaPanel = active?.kind === 'app' && active.app === XVA_WORKSPACE ? active : undefined;
  const ratesPanel = active?.kind === 'app' && active.app === RATES_WORKSPACE ? active : undefined;
  const xva = xvaPanel?.id;
  // What an XVA command acts on, or why it cannot: every XVA command but `new xva` needs the page.
  // A command of the page in the active panel says so: the empty line lists those first (ADR-0006's note 3 of 2026-11-11).
  const onXva = xvaPanel === undefined ? { unavailable: 'Open an XVA workspace first.' } : onPage(xvaPanel);
  const trace = active?.kind === 'app' && active.app === TRACE_VIEW ? active : undefined;
  // `explain` acts on the XVA page's Run or on the trace in view.
  const explainOn = xvaPanel ?? trace;
  const xvaPage = () => {
    const registered = xvaPageOf(xva);
    if (registered === undefined) throw new Error(`The XVA workspace in panel ${xva ?? '(none)'} registered no commands.`);
    return registered;
  };
  const onTraceView = trace !== undefined ? onPage(trace) : xvaPanel !== undefined ? onPage(xvaPanel)
    : { unavailable: 'Open a trace, or an XVA workspace whose result dock shows one, first.' };
  /** The trace view a command acts on, when it runs: the trace panel in view, or the XVA dock's trace tab. */
  const traceView = (): string => {
    if (trace !== undefined) return trace.id;
    const shown = xvaPage().traceInView();
    if (shown === undefined) throw new CommandRefused('The result dock shows no trace. Open a measure\'s own tab, such as with open result, first.');
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
    // `price` on the pricer's page and on a Rates page is the server's command (`xva.workspace.price`,
    // `rates.workspace.price`; ADR-0016 notes 33 and 36), listed there by the catalog. With neither page open this one
    // says where to price: it never runs.
    ...(xvaPanel !== undefined || ratesPanel !== undefined ? [] : [{
      id: 'workspace.price',
      words: 'price',
      title: 'Price the workspace as it stands',
      group: 'Pricing',
      unavailable: 'Open an XVA or Rates workspace to price it.',
      run: () => { throw new Error('price ran with no workspace open to price.'); },
    }]),
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
      ...(explainOn === undefined ? { unavailable: 'Open an XVA workspace that has been priced, or a trace, first.' } : onPage(explainOn)),
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
        if (a.tab === undefined) throw new CommandRefused('open result needs the tab to show, such as metrics.');
        xvaPage().openResult(a.tab);
      },
    },
    // Widening the dock over the input tabs and the grids, and putting it back (owner, 2026-11-08).
    ...([['full', true, 'Widen the result dock over the input tabs and the trade grids'], ['restore', false, 'Put the result dock back to the height its handle sets']] as const)
      .map(([word, full, title]): Command => ({ id: `xva.dock.${word}`, words: `dock ${word}`, title, group: 'XVA', ...onXva, run: () => xvaPage().setDockFull(full) })),
    {
      id: 'rates.new',
      words: 'new rates',
      title: 'New Rates workspace (IRS and CCS lines)',
      group: 'Rates',
      run: () => state.openAppDialog(NEW_RATES),
    },
    // Every other Rates command is the server's (ADR-0016 notes 34 to 36): `new irs`, `add line`, `price`, `live
    // start`, `live stop`, `capture`, and the kernel's `set`, `reset` and `remove` over its lines and defaults.
    {
      id: 'history.open',
      words: 'open pricing db',
      title: 'Open the Pricing DB of past Runs',
      group: 'History',
      run: () => state.showApp(PRICING_DB, 'Pricing DB', {}),
    },
  ];
}

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
  const [valueDateError, setValueDateError] = useState<string | undefined>(undefined);
  const [size, setSize] = useState<number | string>(1000);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);

  const submit = async () => {
    if (registration === null) { setError('Choose a counterparty.'); return; }
    if (!isIsoDate(valueDate)) { setValueDateError('A date as yyyy-MM-dd is required.'); return; }
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
        <IsoDateInput label="Value date" value={valueDate} error={valueDateError}
          onChange={(text) => { setValueDate(text); setValueDateError(undefined); }} />
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
    if (!isIsoDate(valuationDate)) { setError('A date as yyyy-MM-dd is required.'); return; }
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
        <IsoDateInput label="Valuation date" data-autofocus value={valuationDate} error={error}
          onChange={(text) => { setValuationDate(text); setError(undefined); }} />
        <Group justify="flex-end"><Button type="submit" loading={busy}>Create</Button></Group>
      </Stack>
    </form>
  );
}
