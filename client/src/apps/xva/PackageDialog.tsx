import { useState } from 'react';
import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import type { Iteration, PackageView } from './contracts';
import { packagesOf } from './packages';

// *Make package from the selection…* (ADR-0010 §3, ADR-0016 note 94 of 2026-11-14): adds one package to the trades
// the two grids' selections name, the new trades it adds together and the existing trades it takes out of After, and
// makes add each's cases anew from the Package column, in one command. A trade stays in any package it is in already
// (note 95 of 2026-11-15). A grid once clicked always has a row selected,
// so the press opens this rather than writing a package on a row the user only looked at: it lists what the two
// selections name, any trade may be left out, and *Make package* is the change. The server checks every trade named
// and says which it refuses; nothing here decides what a package may hold.

/** *Make package from the selection…*'s id on a Context Menu of the trade grids (ex-grid ADR-0036). */
export const PACKAGE_COMMAND = 'xva.package.selection';

/**
 * What the two trade grids' selections name, as the server read them and said what a package would do with each: the
 * trades it would add, the New grid's out of Before, and those it would take out of After, the Existing grid's and the
 * New grid's in Before, which are read as existing trades (ADR-0016 note 96). `revision` is the one the server said
 * it at, which the press writes at, so a workspace moved since refuses it rather than do otherwise than listed.
 */
export type Selected = Pick<Iteration, 'add' | 'without'> & { readonly revision: number };

export function PackageDialog({ selection, packages, next, make, onClose }: {
  /** What the two selections name; none while the dialog is closed. */
  readonly selection: Selected | undefined;
  /** The packages the Package cells write now, for where each trade listed is. */
  readonly packages: readonly PackageView[];
  /** The name the server gives a package where none is, which the dialog offers. */
  readonly next: string;
  /** Writes the package on the trades at the revision they were listed at, and makes the cases. Refused, it answers the server's reason, and nothing was written. */
  readonly make: (name: string, trades: readonly string[], revision: number) => Promise<string | undefined>;
  readonly onClose: () => void;
}) {
  return (
    <Modal opened={selection !== undefined} onClose={onClose} title="Make package from the selection" size="md">
      {/* Made anew each time the dialog opens, so it opens with the next free name, no trade left out and nothing refused. */}
      {selection !== undefined && <PackageForm selection={selection} packages={packages} next={next} make={make} onClose={onClose} />}
    </Modal>
  );
}

function PackageForm({ selection, packages, next, make, onClose }: {
  readonly selection: Selected;
  readonly packages: readonly PackageView[];
  readonly next: string;
  readonly make: (name: string, trades: readonly string[], revision: number) => Promise<string | undefined>;
  readonly onClose: () => void;
}) {
  const [name, setName] = useState(next);
  // The trades left out of the package, of either list.
  const [out, setOut] = useState<ReadonlySet<string>>(new Set());
  const [refused, setRefused] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const kept = [...selection.add, ...selection.without].filter((t) => !out.has(t));
  const typed = name.trim();
  const none = selection.add.length + selection.without.length === 0
    ? 'No trade is selected in either grid. Select the rows to put in the package, then open this again.'
    : kept.length === 0 ? 'A package takes one trade at least: keep one.'
      : typed === '' ? 'Type a name for the package, such as P1.' : undefined;
  const said = none ?? refused;
  const toggle = (trade: string) => {
    setRefused(undefined);
    setOut((before) => { const left = new Set(before); if (!left.delete(trade)) left.add(trade); return left; });
  };
  const submit = async () => {
    if (none !== undefined || busy) return;
    setBusy(true);
    try {
      const reason = await make(typed, kept, selection.revision);
      if (reason === undefined) onClose();
      else setRefused(`No package was made: ${reason}`);
    } finally {
      setBusy(false);
    }
  };
  // A trade in other packages stays in them, and says which before the press (ADR-0016 note 95).
  const othersOf = (trade: string) => packagesOf(packages, trade).filter((p) => p !== typed);
  const list = (label: string, trades: readonly string[], empty: string, says: (trade: string, on: boolean) => string) => (
    <Stack gap="xs">
      <Text size="sm" fw={600}>{label}</Text>
      {trades.length === 0
        ? <Text size="sm">{empty}</Text>
        : (
          <Group gap="xs" wrap="wrap" role="group" aria-label={label}>
            {trades.map((trade) => {
              const on = !out.has(trade);
              const others = othersOf(trade);
              return (
                <Button key={trade} size="sm" variant={on ? 'light' : 'default'} aria-pressed={on} disabled={busy}
                  title={says(trade, on) + (others.length === 0 ? '' : ` It is in ${others.join(', ')} too, and stays there.`)} onClick={() => toggle(trade)}>
                  {/* The button lays its label out as a row, where a space draws nothing: the margin is the gap, the space the name's. */}
                  {trade}{others.length > 0 && <>{' '}<Text span size="xs" fw={400} ml="sm">{`also in ${others.join(', ')}`}</Text></>}
                </Button>
              );
            })}
          </Group>
        )}
    </Stack>
  );
  return (
    <Stack gap="sm">
      <Text size="sm">
        Adds the package to the Package column of the trades kept, which stay in any package they are in, then makes add
        each&apos;s cases anew: the new trades of a package are added together in one case, and its existing trades taken out
        of After there. A new trade in Before is taken out, as an existing one is. Press a trade to leave it out.
      </Text>
      <TextInput size="sm" label="Package" value={name} onChange={(e) => { setRefused(undefined); setName(e.currentTarget.value); }}
        onKeyDown={(e) => { if (e.key === 'Enter') void submit(); }}
        description="The next free name, or a package's name to add the trades to it." disabled={busy} />
      {list('Adds together', selection.add, 'No new trade out of Before is selected: the package adds none.',
        (trade, on) => (on ? `The package adds ${trade}. Press to leave it out.` : `${trade} is left out. Press to add it.`))}
      {list('Takes out of After', selection.without, 'No existing trade, nor new trade in Before, is selected: the package takes none out.',
        (trade, on) => (on ? `The package takes ${trade} out of After. Press to leave it in.` : `${trade} stays in After. Press to take it out.`))}
      {said !== undefined && <Text size="sm" c="red" role="alert" data-testid="package-refused">{said}</Text>}
      <Group justify="flex-end" gap="xs">
        <Button size="sm" variant="default" onClick={onClose}>Close</Button>
        <Button size="sm" data-autofocus onClick={() => void submit()} loading={busy} disabled={none !== undefined}
          {...(none === undefined ? {} : { title: none })}>Make package</Button>
      </Group>
    </Stack>
  );
}
