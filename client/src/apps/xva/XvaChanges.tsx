import { useEffect, useState, type ReactNode, type Ref } from 'react';
import { Button, Group, Table, Text, TextInput } from '@mantine/core';
import { IconX } from '@tabler/icons-react';
import type { CaseView, ChangeItem, ChangesList, Iteration } from './contracts';

// The Changes input tab (ADR-0010 §3's note of 2026-11-08): every Common change, then each case with its own, where a
// change is found without scanning a wide grid, its value typed again or removed, and a case removed whole. The server
// reads and checks a value typed here as the cell reads it; nothing here decides what a value means.

/** What the tab asks the page to do; each goes after the edits already sent (spine 6). */
export interface ChangeActions {
  /** Sets a change's value as written, in the Base or in the case it is a change of. */
  readonly set: (change: ChangeItem, text: string) => void;
  /** Removes a change: the field follows the Base again in a case, the source in the Base. */
  readonly remove: (change: ChangeItem) => void;
  readonly removeCase: (caseId: string, title: string) => void;
  readonly clearCases: () => void;
  /** Leaves one trade out of what a case of `add each` adds, or of what it takes out of After (ADR-0016 note 37). */
  readonly leaveOut: (caseId: string, title: string, list: keyof Iteration, trade: string) => void;
}

export function XvaChanges({ list, actions, focusRef }: {
  readonly list: ChangesList | undefined;
  readonly actions: ChangeActions;
  /** The tab's body, for `open changes` to put the keyboard in. */
  readonly focusRef: Ref<HTMLDivElement>;
}) {
  if (list === undefined) return <Text size="sm" tabIndex={-1} ref={focusRef}>Reading the changes…</Text>;
  if (list.count === 0 && list.cases.length === 0)
    return <Text size="sm" tabIndex={-1} ref={focusRef}>No change and no case: the workspace is its source. A value typed in, or vary, adds one here.</Text>;
  return (
    <div className="qube-xva-scroll" tabIndex={-1} ref={focusRef} aria-label="Changes">
      <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="sm" fz="sm" aria-label="Changes" className="qube-xva-changes">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Change</Table.Th><Table.Th>Value</Table.Th><Table.Th>Original</Table.Th><Table.Th aria-label="Actions" />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          <Section title="Common" note={list.common.length === 0 ? 'No Common change: every case reads the source.' : undefined} />
          {list.common.map((c) => <Change key={`${c.entity}.${c.field}`} change={c} scope="Common" actions={actions} />)}
          {list.cases.map(({ case: c, changes }) => [
            <Section key={c.id} title={c.title} kind={c.kind} note={caseNote(c.note, changes.length)}
              action={<Button size="compact-xs" variant="default" onClick={() => actions.removeCase(c.id, c.title)}
                aria-label={`Remove case ${c.title}`}>Remove case</Button>} />,
            ...(c.iteration === undefined ? [] : [<Trades key={`${c.id}.trades`} of={c} iteration={c.iteration} actions={actions} />]),
            ...changes.map((change) => <Change key={`${c.id}.${change.entity}.${change.field}`} change={change} scope={c.title} actions={actions}
              whole={c.kind === 'add'} />),
          ])}
        </Table.Tbody>
      </Table>
      {list.cases.length > 0 && (
        <Button size="compact-xs" variant="subtle" mt={4} onClick={actions.clearCases}>Remove every case</Button>
      )}
    </div>
  );
}

/** A section's row: Common, or a case with its kind, and what the section holds when it holds no change. */
function Section({ title, kind, note, action }: { readonly title: string; readonly kind?: string; readonly note?: string | undefined; readonly action?: ReactNode }) {
  return (
    <Table.Tr className="qube-xva-changes-section">
      <Table.Th scope="rowgroup" colSpan={3}>
        {title}{kind === undefined ? '' : <Text span size="xs" c="dimmed">{` · ${kind}`}</Text>}
        {note !== undefined && <Text span size="xs" c="dimmed">{` · ${note}`}</Text>}
      </Table.Th>
      <Table.Td>{action}</Table.Td>
    </Table.Tr>
  );
}

/**
 * What a case of `add each` adds together and takes out of After (ADR-0016 note 37), each trade a button that leaves it
 * out. The one trade a case adds is said and has no button: a case that adds nothing is no case, so it is removed whole.
 */
function Trades({ of, iteration, actions }: { readonly of: CaseView; readonly iteration: Iteration; readonly actions: ChangeActions }) {
  const lone = iteration.add.length === 1;
  const trade = (list: keyof Iteration, id: string) => (
    <Button key={`${list}.${id}`} size="compact-xs" variant="default" rightSection={<IconX size={14} stroke={1.75} aria-hidden />}
      aria-label={`Leave ${id} out of ${of.title}`} title={list === 'add' ? `${of.title} adds ${id}. Press to leave it out.` : `${of.title} takes ${id} out of After. Press to leave it in.`}
      onClick={() => actions.leaveOut(of.id, of.title, list, id)}>{id}</Button>
  );
  return (
    <Table.Tr className="qube-xva-changes-trades">
      <Table.Td colSpan={4}>
        <Group gap="xs" wrap="wrap">
          <Text size="sm" span>Adds</Text>
          {lone ? <Text size="sm" span fw={600}>{iteration.add[0]}</Text> : iteration.add.map((id) => trade('add', id))}
          {iteration.without.length > 0 && <Text size="sm" span>Takes out of After</Text>}
          {iteration.without.map((id) => trade('without', id))}
        </Group>
      </Table.Td>
    </Table.Tr>
  );
}

/** What a case's row says beside its title: the common change a case of `add each` carries, and that it has no change. */
function caseNote(common: string | undefined, changes: number): string | undefined {
  const notes = [...(common === undefined ? [] : [common]), ...(changes === 0 ? ['No change of its own: it prices as the Base.'] : [])];
  return notes.length === 0 ? undefined : notes.join(' · ');
}

const HELD = { conflict: 'held: its source moved, resolve it above the grids', lost: 'lost: its row is not in this version' } as const;

/**
 * A change: its value typed again where it is a cell's and set, or said with why it is held. A case of `add each` is
 * its membership and is removed whole (ADR-0010 §3's note of 2026-11-08), so its changes offer no removal of their own.
 */
function Change({ change, scope, actions, whole = false }: { readonly change: ChangeItem; readonly scope: string; readonly actions: ChangeActions; readonly whole?: boolean }) {
  const label = `${change.title} in ${scope}`;
  return (
    <Table.Tr data-state={change.state}>
      <Table.Td>{change.title}</Table.Td>
      <Table.Td>
        {change.state === 'set' && change.editable
          ? <ChangeValue label={label} text={change.text} onCommit={(text) => actions.set(change, text)} />
          : <Text size="sm" span>{change.text}{change.state === 'set' ? '' : ` · ${HELD[change.state]}`}</Text>}
      </Table.Td>
      <Table.Td className="qube-number-text">{change.originalText}</Table.Td>
      <Table.Td>
        {/* A conflict is resolved where it is shown, by keeping it or taking the source; it is no plain removal. */}
        {change.state !== 'conflict' && !whole && (
          <Button size="compact-xs" variant="default" onClick={() => actions.remove(change)} aria-label={`Remove ${label}`}>Remove</Button>
        )}
      </Table.Td>
    </Table.Tr>
  );
}

/** A change's value, sent as written on Enter or when left, and only when it changed; Escape puts it back. */
function ChangeValue({ label, text, onCommit }: { readonly label: string; readonly text: string; readonly onCommit: (text: string) => void }) {
  const [draft, setDraft] = useState(text);
  useEffect(() => setDraft(text), [text]);
  const commit = () => { if (draft.trim() !== text && draft.trim() !== '') onCommit(draft.trim()); };
  return (
    <TextInput size="xs" aria-label={label} value={draft} className="qube-xva-changes-value"
      onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit}
      onKeyDown={(e) => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') setDraft(text); }} />
  );
}
