import { useEffect, useMemo, useState } from 'react';
import { Alert, Badge, Group, SimpleGrid, Stack, Text, UnstyledButton } from '@mantine/core';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useStore } from 'zustand';
import { firstPageQuery, readTerm, refusalText as describe, termKey, type TraceApi } from './api';
import { rootCard, rootOf, type RootCard } from './cards';
import type { TraceAddress } from './contracts';
import { requestTerm, termRequests } from './open';
import { TraceView } from './TraceView';
import './trace.css';

// A measure's trace as a result tab of a page (ADR-0010 §7, §10; ADR-0012 §7): a card per root of the trace, then the
// drill-down of the root the card chose, under a caption the page gives, such as the rate its figures were converted
// at. The cards read the same pages the drill-down reads, under the same keys, so the trace is read once. A term asked
// for by a Metrics cell or `explain` chooses its root's card.

export function TraceTab({ tabId, address, api, registerFocus, caption }: {
  /** Whose term requests the tab answers: the page asks for a term by this id (`requestTerm`). */
  readonly tabId: string;
  readonly address: TraceAddress;
  readonly api: TraceApi;
  readonly registerFocus: (focus: (() => void) | undefined) => void;
  /** Said once above the cards, where the page has something to say of every figure below (ADR-0012 §7: the FX). */
  readonly caption?: string | undefined;
}) {
  const queryClient = useQueryClient();
  // The cards are of the roots in the formula's order, whatever order the drill-down's children are in.
  const first = useQuery(firstPageQuery(queryClient, api, address, 'formula'));
  const roots = useMemo(() => first.data?.trace.roots.map((r) => r.id) ?? [], [first.data]);
  const pages = useQueries({
    queries: roots.map((id) => ({ queryKey: termKey(address, id, 'formula', 0), queryFn: () => readTerm(api, address, id, 'formula', 0) })),
  });

  const request = useStore(termRequests, (s) => s.requests[tabId]);
  const [chosen, setChosen] = useState<string | undefined>(undefined);
  useEffect(() => {
    const under = rootOf(roots, request?.path);
    if (under !== undefined) setChosen(under);
  }, [request, roots]);
  const root = chosen !== undefined && roots.includes(chosen) ? chosen : roots[0];

  if (first.isPending) return <Text size="sm" data-testid="trace-loading">Reading the trace…</Text>;
  if (first.isError) return <Alert color="red" variant="light" title="The trace could not be read" data-testid="trace-refused">{describe(first.error)}</Alert>;
  const header = first.data.trace;
  const choose = (id: string) => { setChosen(id); requestTerm(tabId, id); };

  return (
    <Stack gap="sm" className="qwb-trace-tab" data-testid="trace-tab">
      {caption !== undefined && <Text size="xs" data-testid="trace-tab-caption">{caption}</Text>}
      {roots.length > 1 && (
        <SimpleGrid cols={{ base: 1, sm: Math.min(roots.length, 4) }} spacing="xs" role="group" aria-label="Roots">
          {roots.map((id, i) => {
            const page = pages[i];
            if (page?.isError === true) {
              return <Alert key={id} color="red" variant="light" title={header.roots[i]?.label ?? id}>{describe(page.error)}</Alert>;
            }
            const card = page?.data === undefined ? undefined : rootCard(page.data.term, page.data.trace);
            return <Card key={id} id={id} label={header.roots[i]?.label ?? id} card={card} chosen={id === root} onChoose={() => choose(id)} />;
          })}
        </SimpleGrid>
      )}
      <TraceView key={root} panelId={tabId} address={address} api={api} registerFocus={registerFocus} root={root} />
    </Stack>
  );
}

/** One root's card: what it is, what governed it, and its figures; pressing it draws that root below. */
function Card({ id, label, card, chosen, onChoose }: {
  readonly id: string; readonly label: string; readonly card: RootCard | undefined; readonly chosen: boolean; readonly onChoose: () => void;
}) {
  return (
    <UnstyledButton className="qwb-trace-card" data-chosen={chosen} aria-pressed={chosen} onClick={onChoose} data-testid="trace-card" data-root={id}>
      <Group gap="xs" wrap="wrap">
        <Text size="sm" fw={600}>{label}</Text>
        {card?.detail !== undefined && <Text size="sm">{card.detail}</Text>}
        {card?.governing !== undefined && <Badge variant="light" data-testid="trace-card-governing">{card.governing}</Badge>}
      </Group>
      {card === undefined
        ? <Text size="xs">Reading…</Text>
        : (
          <dl className="qwb-trace-card-figures">
            {card.figures.map((f) => (
              <div key={f.key}>
                <dt>{f.title}</dt>
                <dd className="qwb-number-text">{f.text}</dd>
              </div>
            ))}
          </dl>
        )}
    </UnstyledButton>
  );
}
