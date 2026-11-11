import { useState } from 'react';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import type { Iteration } from './contracts';

// *One case from the selection…* (ADR-0010 §3, ADR-0016 note 37 of 2026-11-11): one case of `add each` that adds the
// new trades selected together and takes the existing trades selected out of After. A grid once clicked always has a
// row selected, so the press opens this rather than making a case of a row the user only looked at: it lists what the
// two selections name, any trade may be left out, and *Make case* is the change. The server checks every trade named
// and says which it refuses; nothing here decides what a case may hold.

/** *One case from the selection…*'s id on a Context Menu of the trade grids (ex-grid ADR-0036). */
export const CASE_COMMAND = 'xva.case.selection';

export function CaseDialog({ selection, make, onClose }: {
  /** What the two trade grids' selections name, as the server read them; none while the dialog is closed. */
  readonly selection: Iteration | undefined;
  /** Makes the case beside the cases already made. Refused, it answers the server's reason, and no case was made. */
  readonly make: (iteration: Iteration) => Promise<string | undefined>;
  readonly onClose: () => void;
}) {
  return (
    <Modal opened={selection !== undefined} onClose={onClose} title="One case from the selection" size="md">
      {/* Made anew each time the dialog opens, so it opens with no trade left out and nothing refused. */}
      {selection !== undefined && <CaseForm selection={selection} make={make} onClose={onClose} />}
    </Modal>
  );
}

function CaseForm({ selection, make, onClose }: {
  readonly selection: Iteration;
  readonly make: (iteration: Iteration) => Promise<string | undefined>;
  readonly onClose: () => void;
}) {
  // The trades left out of the case, of either list.
  const [out, setOut] = useState<ReadonlySet<string>>(new Set());
  const [refused, setRefused] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const add = selection.add.filter((t) => !out.has(t));
  const without = selection.without.filter((t) => !out.has(t));
  const none = selection.add.length === 0
    ? 'No row of New trades is selected, and a case adds one new trade at least. Select the rows to add, then open this again.'
    : add.length === 0 ? 'A case adds one new trade at least: keep one.' : undefined;
  const said = none ?? refused;
  const toggle = (trade: string) => {
    setRefused(undefined);
    setOut((before) => { const next = new Set(before); if (!next.delete(trade)) next.add(trade); return next; });
  };
  const submit = async () => {
    if (none !== undefined || busy) return;
    setBusy(true);
    try {
      const reason = await make({ add, without });
      if (reason === undefined) onClose();
      else setRefused(`No case was made: ${reason}`);
    } finally {
      setBusy(false);
    }
  };
  const list = (label: string, trades: readonly string[], empty: string, says: (trade: string, on: boolean) => string) => (
    <Stack gap={4}>
      <Text size="sm" fw={600}>{label}</Text>
      {trades.length === 0
        ? <Text size="sm">{empty}</Text>
        : (
          <Group gap="xs" wrap="wrap" role="group" aria-label={label}>
            {trades.map((trade) => {
              const on = !out.has(trade);
              return (
                <Button key={trade} size="sm" variant={on ? 'light' : 'default'} aria-pressed={on} disabled={busy} title={says(trade, on)}
                  onClick={() => toggle(trade)}>{trade}</Button>
              );
            })}
          </Group>
        )}
    </Stack>
  );
  return (
    <Stack gap="sm">
      <Text size="sm">One case of add each, priced beside the Base by the next Price. The cases already made stay. Press a trade to leave it out.</Text>
      {list('Adds together', selection.add, 'No row of New trades is selected.',
        (trade, on) => (on ? `The case adds ${trade}. Press to leave it out.` : `${trade} is left out. Press to add it.`))}
      {list('Takes out of After', selection.without, 'No row of Existing trades is selected: the case takes none out.',
        (trade, on) => (on ? `The case takes ${trade} out of After. Press to leave it in.` : `${trade} stays in After. Press to take it out.`))}
      {said !== undefined && <Text size="sm" c="red" role="alert" data-testid="case-refused">{said}</Text>}
      <Group justify="flex-end" gap="xs">
        <Button size="sm" variant="default" onClick={onClose}>Close</Button>
        <Button size="sm" data-autofocus onClick={() => void submit()} loading={busy} disabled={none !== undefined}
          {...(none === undefined ? {} : { title: none })}>Make case</Button>
      </Group>
    </Stack>
  );
}
