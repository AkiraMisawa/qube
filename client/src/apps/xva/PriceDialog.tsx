import { Button, Group, List, Modal, ScrollArea, Stack, Text } from '@mantine/core';
import type { PricePlan } from './contracts';
import { pricedAs } from './metrics';

// Price's confirmation (ADR-0010 §3's note of 2026-11-19; the owner, the same day: "a list of the case titles before
// Price starts gives confidence, since not everyone knows the specification"). The Price button reads what Price would
// price from the server, which builds it as Price builds its input, and this lists it: every case as the Run will
// title it, with the common change it carries, the markets, the side priced alone, and what is refused already.
// Enter prices the revision listed and no other; Escape goes back. The line's `price` says the same in its reading,
// and Enter there is its confirmation.

export function PriceDialog({ plan, price, onClose }: {
  /** What Price would price, as the server read it; none while the dialog is closed. */
  readonly plan: PricePlan | undefined;
  /** Prices the revision the plan was read at. */
  readonly price: (plan: PricePlan) => void;
  readonly onClose: () => void;
}) {
  return (
    <Modal opened={plan !== undefined} onClose={onClose} title="Price" size="lg" trapFocus transitionProps={{ duration: 0 }}>
      {plan !== undefined && (
        <Stack gap="sm">
          {/* Said as the Price button says it, of what the server read. */}
          <Text size="sm" fw={600} data-testid="price-plan-summary">{pricedAs(plan.measures, plan.cases.length, plan, plan.markets.length)}</Text>
          <Text size="sm">Price makes one Run of these cases{plan.markets.length > 1 ? ', each on every market' : ''}, and nothing else:</Text>
          <ScrollArea.Autosize mah="50vh" type="auto" offsetScrollbars>
            <List type="ordered" size="sm" spacing={4} aria-label="Cases" data-testid="price-plan-cases">
              {plan.cases.map((c, i) => (
                // Cases are listed in the order Price prices them; their place is who they are here.
                <List.Item key={i}>
                  <Text span size="sm">{c.title}</Text>
                  {c.note !== undefined && c.note !== null && <Text size="xs" c="dimmed">{c.note}</Text>}
                </List.Item>
              ))}
            </List>
          </ScrollArea.Autosize>
          {plan.markets.length > 1 && (
            <Text size="sm" data-testid="price-plan-markets">Markets: {plan.markets.join(', ')}.</Text>
          )}
          {plan.refused.length > 0 && (
            <Stack gap={2} role="alert" data-testid="price-plan-refused">
              <Text size="sm" c="red">{plan.refused.length} {plan.refused.length === 1 ? 'figure' : 'figures'} will be refused, and the rest priced:</Text>
              {plan.refused.map((r) => <Text key={`${r.measure}:${r.case}`} size="xs" c="red">{r.measure} of {r.case}: {r.message}</Text>)}
            </Stack>
          )}
          <Group justify="flex-end" gap="xs">
            <Button size="sm" variant="default" onClick={onClose}>Back</Button>
            <Button size="sm" data-autofocus data-testid="price-confirm" onClick={() => price(plan)}>
              Price {plan.cases.length} {plan.cases.length === 1 ? 'case' : 'cases'}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
