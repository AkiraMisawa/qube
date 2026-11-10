import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Anchor, Badge, Breadcrumbs, Button, Code, CopyButton, Group, SegmentedControl, Stack, Text, UnstyledButton, em } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useQueries, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { useStore } from 'zustand';
import { firstPageQuery, readTerm, refusalText as describe, termKey, TERM_PAGE, type ChildOrder, type TraceApi } from './api';
import type { Side, TermKind, TermPage, TermRead, TraceAddress, TraceAnswer, TraceHeader } from './contracts';
import { operandTargets, stateText } from './formula';
import { EntityRows } from './EntityRows';
import { belowViews, entitySetOf, splits } from './entities';
import { LeafRows } from './LeafRows';
import { DEFAULT_TRACE_VIEW, explainLine, setTraceView, termRequests, traceViews } from './open';
import { TermDetail } from './TermDetail';
import { TraceTree } from './TraceTree';
import { opened, treeRows, type ChildrenRead, type TreeRow } from './tree';
import { SIDE_TITLE, governsMark, leadText, providerOf } from './words';
import './trace.css';

// One view draws every trace (ADR-0011 §6): on desktop the tree of every level beside the formula of the selected
// term, the leaf rows below the tree; below `sm`, drilling in one level at a time, the breadcrumb, the formula and the
// selected term's children as a list. The view reads a term at a time (§5) and holds only the terms on the paths
// opened. A case priced on one side is shown on that side alone, said once (§4).

const ROOT = '\u0000root';

/** Keeps a row object while nothing it draws changed, so the grid paints only what did (ADR-0003). */
function stable(next: readonly TreeRow[], previous: ReadonlyMap<string, TreeRow>): readonly TreeRow[] {
  return next.map((row) => {
    const held = previous.get(row.key);
    return held !== undefined && held.term === row.term && held.depth === row.depth && held.open === row.open && held.opens === row.opens
      && held.kind === row.kind && held.note === row.note ? held : row;
  });
}

export function TraceView({ panelId, address, api, registerFocus, root }: {
  /** Whose term requests and view settings the view answers (`termRequests`, `traceViews`): a trace panel's, or a page's trace tab's. */
  readonly panelId: string;
  readonly address: TraceAddress;
  readonly api: TraceApi;
  /** Gives the panel's "focus grid" command the tree's keyboard, while the tree is drawn. */
  readonly registerFocus: (focus: (() => void) | undefined) => void;
  /** The one root the tree is drawn from, as a trace tab's card chooses it (ADR-0012 §7); every root where none is. */
  readonly root?: string | undefined;
}) {
  const queryClient = useQueryClient();
  // Which component mounts, not how one is styled (design guidelines §9): a tree beside the formula, or drilling in.
  const phone = useMediaQuery(`(max-width: ${em(767.98)})`, false, { getInitialValueInEffect: false });
  // How it draws, kept per view, which its controls and the trace commands set alike (ADR-0006).
  const settings = useStore(traceViews, (s) => s.views[panelId]) ?? DEFAULT_TRACE_VIEW;
  const { order, below, above } = settings;
  const [selected, setSelected] = useState<string | undefined>(undefined);
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set());
  const [pagesAsked, setPagesAsked] = useState<Readonly<Record<string, number>>>({});
  const [place, setPlace] = useState(0);
  // The term asked for by a Metrics cell or `explain`: it opens as well as the terms above it, once it is read.
  const reveal = useRef<string | undefined>(ROOT);

  const request = useStore(termRequests, (s) => s.requests[panelId]);
  useEffect(() => {
    if (request === undefined) return;
    setSelected(request.path);
    reveal.current = request.path ?? ROOT;
    setPlace((p) => p + 1);
  }, [request]);

  const key = useCallback((path: string | undefined, offset: number) => termKey(address, path, order, offset), [address, order]);
  const read = useCallback((path: string | undefined, offset: number) => readTerm(api, address, path, order, offset), [api, address, order]);
  // Another order reads the children afresh, from their first page.
  useEffect(() => setPagesAsked({}), [order]);

  // The trace's header and its first root; the root's page is the same answer, so it is not read twice.
  const first = useQuery(firstPageQuery(queryClient, api, address, order));
  const header = first.data?.trace;
  const rootIds = useMemo(() => (header?.roots ?? []).map((r) => r.id).filter((id) => root === undefined || id === root), [header, root]);
  // A trace tab that draws another root asks for it as a term, so the selection follows it (`requestTerm`).
  const selectedId = selected ?? rootIds[0];

  const catalog = useQuery({
    queryKey: ['trace-catalog', header?.method.id, header?.method.version],
    queryFn: () => api.catalog(header!.method.id, header!.method.version),
    enabled: header !== undefined,
    staleTime: Infinity,
  });
  const kinds = useMemo(() => new Map<string, TermKind>((catalog.data?.kinds ?? []).map((k) => [k.kind, k])), [catalog.data]);

  // Every page the view holds: the roots, each open term's pages of children, the selected term's, and the terms its
  // formula names that are not its children.
  const wanted = new Map<string, { readonly path: string; readonly offset: number }>();
  const want = (path: string, offset: number) => wanted.set(`${path}\u0000${offset}`, { path, offset });
  const pagesOf = (id: string) => Array.from({ length: pagesAsked[id] ?? 1 }, (_, i) => i * TERM_PAGE);
  for (const id of rootIds) want(id, 0);
  for (const id of open) for (const offset of pagesOf(id)) want(id, offset);
  if (selectedId !== undefined) for (const offset of pagesOf(selectedId)) want(selectedId, offset);
  const selectedCached = selectedId === undefined ? undefined : queryClient.getQueryData<TraceAnswer>(key(selectedId, 0));
  const selectedKind = selectedCached === undefined ? undefined : kinds.get(selectedCached.term.term.kind);
  if (selectedCached !== undefined && selectedKind !== undefined) {
    const children = new Set(selectedCached.term.children.map((c) => c.id));
    for (const target of operandTargets(selectedKind, selectedCached.term.term)) if (!children.has(target)) want(target, 0);
  }
  const specs = [...wanted.values()];
  const results = useQueries({
    queries: specs.map((s) => ({ queryKey: key(s.path, s.offset), queryFn: () => read(s.path, s.offset), enabled: header !== undefined })),
  });
  const resultOf = (path: string, offset: number): UseQueryResult<TraceAnswer> | undefined => {
    const at = specs.findIndex((s) => s.path === path && s.offset === offset);
    return at < 0 ? undefined : results[at];
  };
  const pageOf = (id: string): TermPage | undefined => resultOf(id, 0)?.data?.term;
  const selectedResult = selectedId === undefined ? undefined : resultOf(selectedId, 0);
  const selectedPage = selectedResult?.data?.term;

  const childrenOf = (id: string): ChildrenRead | undefined => {
    const reads = pagesOf(id).map((offset) => resultOf(id, offset));
    const failed = reads.find((r) => r?.isError === true);
    if (failed !== undefined) return { kind: 'failed', reason: describe(failed.error) };
    const pages = reads.flatMap((r) => (r?.data === undefined ? [] : [r.data.term]));
    return pages.length === 0 ? { kind: 'loading' } : { kind: 'pages', pages };
  };

  /** A term the selected formula names: among the selected term's children, or read on its own. */
  const operand = (id: string): TermRead | undefined =>
    selectedPage?.children.find((c) => c.id === id) ?? pageOf(id)?.term;

  // Once the selected term is read, the terms above it open so that it shows in the tree; a term asked for opens too.
  useEffect(() => {
    if (selectedPage === undefined) return;
    const asked = reveal.current === selectedPage.term.id || (reveal.current === ROOT && selectedPage.path.length === 1);
    if (asked) reveal.current = undefined;
    setOpen((current) => {
      const next = new Set(opened(current, selectedPage.path));
      if (asked && selectedPage.term.childCount > 0) next.add(selectedPage.term.id);
      return next.size === current.size ? current : next;
    });
  }, [selectedPage]);

  const rootTerms = rootIds.flatMap((id) => pageOf(id)?.term ?? []);
  const held = useRef<ReadonlyMap<string, TreeRow>>(new Map());
  const fresh = header === undefined ? [] : treeRows(rootTerms, open, childrenOf);
  const rows = stable(fresh, held.current);
  const previous = useRef<readonly TreeRow[]>([]);
  const same = rows.length === previous.current.length && rows.every((r, i) => r === previous.current[i]);
  const shown = same ? previous.current : rows;
  previous.current = shown;
  held.current = new Map(shown.map((r) => [r.key, r]));

  /** Selects a term from outside the grid (breadcrumb, operand, step, list), and puts the grid's Focus on it. */
  const select = (id: string) => { setSelected(id); setPlace((p) => p + 1); };
  const toggle = (id: string, isOpen: boolean) => {
    setOpen((current) => { const next = new Set(current); if (isOpen) next.add(id); else next.delete(id); return next; });
    select(id);
  };
  const more = (id: string) => setPagesAsked((p) => ({ ...p, [id]: (p[id] ?? 1) + 1 }));
  const reorder = (next: ChildOrder) => setTraceView(panelId, { order: next });

  if (first.isPending) return <Text size="sm" data-testid="trace-loading">Reading the trace…</Text>;
  if (first.isError) return <Alert color="red" variant="light" title="The trace could not be read" data-testid="trace-refused">{describe(first.error)}</Alert>;
  const trace = first.data.trace;
  const leaves = selectedPage === undefined ? undefined : leavesOf(selectedPage.term, trace);
  const leafSet = leaves === undefined ? undefined : catalog.data?.leafSets.find((s) => s.leafSet === leaves.leafSet);

  const detail = selectedResult?.isError === true
    ? <Alert color="red" variant="light" title="The term could not be read" data-testid="trace-term-refused">{describe(selectedResult.error)}</Alert>
    : catalog.isError
      ? <Alert color="red" variant="light" title="The catalogue could not be read" data-testid="trace-catalog-refused">{describe(catalog.error)}</Alert>
      : selectedPage === undefined || catalog.data === undefined
        ? <Text size="sm">Reading the term…</Text>
        : <TermDetail header={trace} page={selectedPage} kinds={kinds} operand={operand} onSelect={select}
          above={above} onAbove={(next) => setTraceView(panelId, { above: next })} />;
  // At a term with a share to split, its entities by default, or every row at any depth below it; elsewhere its own
  // rows (ADR-0012 §6 with its note of 2026-11-08). Rows a term names in a leaf set the catalogue does not register are
  // said to be unreadable, never left out.
  const entitySet = entitySetOf(catalog.data);
  const views = selectedPage === undefined || catalog.data === undefined ? []
    : belowViews(entitySet, splits(selectedPage.term, trace.sides), leaves === undefined ? undefined : leafSet);
  const view = views.find((v) => v.value === below) ?? views[0];
  const unregistered = leaves !== undefined && catalog.data !== undefined && leafSet === undefined;
  const ownCount = (term: TermRead) => {
    if (leaves === undefined) throw new Error(`${term.id} has no rows of its own to count.`);
    return leaves.rowCount;
  };
  const shownBelow = selectedPage === undefined ? undefined
    : unregistered
      ? <Alert color="red" variant="light" title="Leaf set not registered">
          The catalogue of {trace.method.id} {trace.method.version} has no leaf set {leaves.leafSet}, so the rows below {selectedPage.term.label} cannot be shown.
        </Alert>
      : view === undefined ? undefined
        : view.value === 'entities'
          ? <EntityRows key={selectedPage.term.id} panelId={panelId} api={api} address={address} header={trace} path={selectedPage.term.id} set={view.set} />
          : <LeafRows key={`${selectedPage.term.id}:${String(view.below)}`} panelId={panelId} api={api} address={address} header={trace} path={selectedPage.term.id} set={view.set}
              rows={view.below ? { below: true } : { below: false, count: ownCount(selectedPage.term) }} />;
  const leafRows = shownBelow === undefined ? undefined : (
    <Stack gap="xs" data-testid="trace-below">
      {views.length > 1 && view !== undefined && (
        <SegmentedControl size="xs" value={view.value} onChange={(v) => setTraceView(panelId, { below: v === 'rows' ? 'rows' : 'entities' })} aria-label="Rows below the term"
          data-testid="trace-below-view" data={views.map((v) => ({ value: v.value, label: v.label }))} />
      )}
      {shownBelow}
    </Stack>
  );
  const explainText = selectedPage === undefined ? undefined : explainLine(address.measure, selectedPage.term.id);

  return (
    <Stack gap="xs" className="qwb-trace" data-testid="trace-view" data-layout={phone ? 'drill' : 'tree'}>
      <TraceHeaderBar header={trace} title={trace.roots.find((r) => r.id === rootIds[0])?.label ?? trace.measure} order={order} onOrder={reorder} />
      {selectedPage !== undefined && (
        <Breadcrumbs separator="›" separatorMargin="xs" className="qwb-trace-path" aria-label="Path from the root" data-testid="trace-path">
          {selectedPage.path.map((crumb, i) => (i === selectedPage.path.length - 1
            ? <Text key={crumb.id} size="xs" fw={600} aria-current="page">{crumb.label}</Text>
            : <Anchor key={crumb.id} component="button" size="xs" onClick={() => select(crumb.id)}>{crumb.label}</Anchor>))}
        </Breadcrumbs>
      )}
      {explainText !== undefined && <ExplainLine text={explainText} />}
      {phone ? (
        <Stack gap="sm">
          {detail}
          {selectedId !== undefined && <ChildrenList header={trace} read={childrenOf(selectedId)} onSelect={select} onMore={() => more(selectedId)} />}
          {leafRows}
        </Stack>
      ) : (
        <div className="qwb-trace-body">
          <Stack gap="xs" className="qwb-trace-tree-part">
            <TraceTree panelId={panelId} header={trace} rows={shown} selected={selectedId} onSelect={setSelected} onGoTo={select} onToggle={toggle} onMore={more}
              place={place} registerFocus={registerFocus} />
            {leafRows}
          </Stack>
          <div className="qwb-trace-detail-part">{detail}</div>
        </div>
      )}
    </Stack>
  );
}

/** The leaf set below a term and the row count of each side that wrote rows below it; a side that wrote none has none. */
function leavesOf(term: TermRead, header: TraceHeader): { readonly leafSet: string; readonly rowCount: Partial<Record<Side, number>> } | undefined {
  let leafSet: string | undefined;
  const rowCount: Partial<Record<Side, number>> = {};
  for (const side of header.sides) {
    const leaves = term[side]?.leaves;
    if (leaves === undefined || leaves === null) continue;
    leafSet ??= leaves.leafSet;
    rowCount[side] = leaves.rowCount;
  }
  return leafSet === undefined ? undefined : { leafSet, rowCount };
}

/**
 * The command that comes back to the selected term (ADR-0011 §7): typed in Commands, or handed to the AI, it opens the
 * same term of the same trace. Copied whole, so a path with spaces stays one path.
 */
function ExplainLine({ text }: { readonly text: string }) {
  return (
    <Group gap="xs" wrap="nowrap" className="qwb-trace-explain" data-testid="trace-explain">
      <Code className="qwb-trace-explain-text" title="Type this in Commands, or give it to the AI, to come back to this term">{text}</Code>
      <CopyButton value={text}>
        {({ copied, copy }) => (
          <Button size="compact-xs" variant="default" onClick={copy} data-testid="trace-explain-copy" aria-label={`Copy ${text}`}>{copied ? 'Copied' : 'Copy'}</Button>
        )}
      </CopyButton>
    </Group>
  );
}

/** What the trace is of and how it was written, always on screen (ADR-0011 §6): simulated said so, the provider and its depth. */
function TraceHeaderBar({ header, title, order, onOrder }: {
  readonly header: TraceHeader; readonly title: string; readonly order: ChildOrder; readonly onOrder: (order: ChildOrder) => void;
}) {
  const origin = header.origins.find((o) => o.side === header.sides.at(-1)) ?? header.origins[0];
  const simulated = header.origins.some((o) => o.provenance?.executionMode === 'simulated');
  const engines = [...new Set(header.origins.flatMap((o) => (o.engine ? [`${o.engine.name} ${o.engine.version}`] : [])))];
  const only = header.sides.length === 1 ? header.sides[0]! : undefined;
  return (
    <Stack gap="xs">
      <div className="qwb-toolbar" role="toolbar" aria-label="Trace">
        <Text size="sm" fw={600} data-testid="trace-title">{title}</Text>
        {origin?.subject && <Text size="sm">{origin.subject.id}{origin.subject.label ? ` · ${origin.subject.label}` : ''}</Text>}
        <SegmentedControl size="xs" value={order} onChange={(v) => onOrder(v as ChildOrder)} aria-label="Order of the children"
          data={[{ value: 'formula', label: 'Formula order' }, { value: 'size', label: header.sides.length === 2 ? 'Largest change first' : 'Largest first' }]} />
      </div>
      <Group gap="xs" wrap="wrap" data-testid="trace-provenance">
        {simulated && <Badge variant="light" color="yellow" data-testid="trace-simulated" title="A simulated provider wrote this trace. Its figures are not a model of anything.">SIMULATED</Badge>}
        <Text size="xs">{[
          `${header.method.id} ${header.method.version}${header.method.regulation ? `, ${header.method.regulation}` : ''}, to ${header.depth} level`,
          ...engines,
          `Run ${header.runId}, case ${header.case}, scenario ${header.scenario}`,
          ...(origin?.valuationDate ? [`valued ${origin.valuationDate}`] : []),
          ...(origin?.reportingCurrency ? [`in ${origin.reportingCurrency}`] : []),
        ].join(' · ')}</Text>
      </Group>
      {only !== undefined && (
        <Text size="xs" data-testid="trace-one-side">
          {SIDE_TITLE[only]} only: {SIDE_TITLE[only === 'after' ? 'before' : 'after']} was not priced.
        </Text>
      )}
    </Stack>
  );
}

/**
 * Below `sm`: the selected term's children, each with its Diff or value and its state, and, as an operand of a max,
 * whether it governs, each opening the next level.
 */
function ChildrenList({ header, read, onSelect, onMore }: {
  readonly header: TraceHeader; readonly read: ChildrenRead | undefined; readonly onSelect: (id: string) => void; readonly onMore: () => void;
}) {
  if (read === undefined || read.kind === 'loading') return <Text size="xs">Reading its terms…</Text>;
  if (read.kind === 'failed') return <Text size="xs" c="red">{read.reason}</Text>;
  const children = read.pages.flatMap((p) => p.children);
  const total = read.pages.at(-1)?.childTotal ?? 0;
  if (total === 0) return null;
  return (
    <Stack gap={0} className="qwb-trace-children" data-testid="trace-children" aria-label="Terms below">
      {children.map((child) => {
        const states = header.sides.flatMap((side) => {
          const value = child[side];
          return value === undefined || value === null || value.state === 'value' ? [] : [`${header.sides.length === 2 ? `${SIDE_TITLE[side]} ` : ''}${stateText(value.state, null, providerOf(header, side))}`];
        });
        const mark = governsMark(child, header);
        return (
          <UnstyledButton key={child.id} className="qwb-trace-child" onClick={() => onSelect(child.id)} data-state={states.length > 0 ? 'other' : 'value'}
            {...(mark === undefined ? {} : { 'data-governs': mark.governs })}>
            <span className="qwb-trace-child-label">
              {child.label}
              {mark !== undefined && <Badge size="sm" variant="light" {...(mark.governs ? {} : { color: 'gray' })} ml="xs" data-testid="trace-governs">{mark.text}</Badge>}
            </span>
            <span className="qwb-trace-child-lead">{leadText(child, header)}</span>
            {states.length > 0 && <span className="qwb-trace-child-state">{states.join(' · ')}</span>}
          </UnstyledButton>
        );
      })}
      {children.length < total && <Button size="xs" variant="subtle" onClick={onMore}>Read {total - children.length} more of {total}</Button>}
    </Stack>
  );
}
