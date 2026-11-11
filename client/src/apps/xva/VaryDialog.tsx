import { useState } from 'react';
import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { ambiguousComma } from './commands';

// *Vary values…* (ADR-0010 §3): the cell's Context Menu opens this for a list of values, and the cases made are those
// `vary` makes for the cell. The server reads and checks each value as the cell would typed; a value it refuses is
// said here, and no case is made. What was made is said in the server's sentence, the one the command line's notice has.

/** *Vary values…*'s id on a Context Menu (ex-grid ADR-0036), on the trade grids and the CSA grid alike. */
export const VARY_COMMAND = 'xva.vary';

/** The cell a dialog varies: its entity and field, as the server names it, and its title, as the grid shows it. */
export interface VaryTarget {
  readonly entity: string;
  readonly field: string;
  readonly title: string;
}

/** The values as typed, separated by commas; an empty one is refused here, never sent as a guess. */
export function valuesTyped(text: string): { readonly ok: true; readonly values: readonly string[] } | { readonly ok: false; readonly message: string } {
  const ambiguous = ambiguousComma(text);
  if (ambiguous !== undefined) return { ok: false, message: ambiguous };
  const values = text.split(',').map((v) => v.trim());
  if (values.length === 0 || values.every((v) => v === '')) return { ok: false, message: 'Type one value or more, separated by commas.' };
  if (values.some((v) => v === '')) return { ok: false, message: 'A value between two commas is empty.' };
  return { ok: true, values };
}

export function VaryDialog({ target, vary, onClose }: {
  readonly target: VaryTarget | undefined;
  /** Makes the cases and answers what the server said it made and skipped; refused, it throws with the server's reason. */
  readonly vary: (target: VaryTarget, values: readonly string[]) => Promise<string>;
  readonly onClose: () => void;
}) {
  const [text, setText] = useState('');
  const [said, setSaid] = useState<{ readonly text: string; readonly failed: boolean } | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const close = () => { setText(''); setSaid(undefined); onClose(); };
  const submit = async () => {
    if (target === undefined || busy) return;
    const typed = valuesTyped(text);
    if (!typed.ok) { setSaid({ text: typed.message, failed: true }); return; }
    setBusy(true);
    try {
      setSaid({ text: await vary(target, typed.values), failed: false });
      setText('');
    } catch (error) {
      setSaid({ text: `vary made no case: ${error instanceof Error ? error.message : String(error)}`, failed: true });
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal opened={target !== undefined} onClose={close} title={target === undefined ? 'Vary values' : `Vary ${target.title}`} size="md">
      <Stack gap="xs">
        <Text size="sm">A case per value, priced beside the Base by the next Price. Separate the values with commas.</Text>
        <TextInput label="Values" placeholder="10mm, 5mm, 0" value={text} data-autofocus error={said?.failed === true}
          onChange={(e) => setText(e.currentTarget.value)} onKeyDown={(e) => { if (e.key === 'Enter') void submit(); }} />
        {said !== undefined && <Text size="sm" role="status" data-testid="vary-said" {...(said.failed ? { c: 'red' } : {})}>{said.text}</Text>}
        <Group justify="flex-end" gap="xs">
          <Button size="xs" variant="default" onClick={close}>Close</Button>
          <Button size="xs" onClick={() => void submit()} loading={busy}>Make cases</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
