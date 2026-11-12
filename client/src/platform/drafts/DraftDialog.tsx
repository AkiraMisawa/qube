import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Alert, Badge, Button, CloseButton, Group, Loader, Modal, Select, SimpleGrid, Stack, Switch, Tabs, Text, TextInput, em, rem } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { actionColumn, type GridActionEvent, type GridColumn, type GridEditIntent } from '@ex-grid/core';
import { ApiError, type Api } from '../api';
import type { CommandDoor } from '../commands/commands';
import type { DraftForm, DraftRow, DraftTable, DraftTemplate, DraftTerm, FieldInput, LineDraft } from '../contracts';
import { QubeGrid } from '../grid/QubeGrid';
import { cellLine, fieldLine, makeLine, removeLine, sectionsOf, shownOf, templateLine } from './draftLines';
import { compactGridHeight } from '../grid/metrics';

// A row's dialog (ADR-0016 §10, note 57 of 2026-11-12), the same for every table: it is drawn from the form the server
// answers for the page's draft, and every change made in it is a line the server reads in that draft. It has no rule
// of its own: a value is refused by the server's sentence, at the field it was typed in.

/** Sends a line for the draft; answers why where the draft gained nothing. */
type Send = (line: string) => Promise<string | undefined>;

const REMOVE = 'remove';
const ACTIONS = 'Actions';
const MARK = 'Draft';
/** A dialog's Control Height (docs/design-guidelines.md): what a `sm` input is as high as. */
const DIALOG_CONTROL = 36;
/** How many rows a table under the row is as high as, before it scrolls. */
const ROWS_DRAWN = 8;
/** The forms whose values are figures, drawn against the right edge as every grid draws them. */
const FIGURES = new Set(['amount', 'signedAmount', 'number', 'percent', 'rate', 'spread', 'days']);

const labelOf = (field: Pick<FieldInput, 'title' | 'unit'>, changed: boolean): ReactNode => (
  <>
    {field.unit === undefined ? field.title : `${field.title} (${field.unit})`}
    {changed && <Text span size="xs" c="blue" fw={600} data-changed> · changed</Text>}
  </>
);

/** One field of the draft's row: what it holds as the draft leaves it, and the control its form is typed with. */
function FieldBox({ field, written, changed, send }: {
  readonly field: FieldInput;
  readonly written: string;
  readonly changed: boolean;
  /** None on Original, which is read-only. */
  readonly send?: Send;
}) {
  const shown = shownOf(field, written);
  const [seen, setSeen] = useState(shown);
  const [text, setText] = useState(shown);
  const [problem, setProblem] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  // What the draft's lines leave has moved: the field shows it, and what was said of the older value is gone.
  if (seen !== shown) { setSeen(shown); setText(shown); setProblem(undefined); }
  const label = labelOf(field, changed);
  if (send === undefined || !field.editable) {
    return <TextInput size="sm" label={label} value={shown} readOnly variant="filled" {...(send !== undefined ? { title: 'No command sets this field.' } : {})} />;
  }
  const commit = async (next: string) => {
    if (busy) return;
    if (next.trim() === shown) { setProblem(undefined); return; }
    setBusy(true);
    try {
      setProblem(await send(fieldLine(field, next)));
    } finally {
      setBusy(false);
    }
  };
  if (field.form === 'flag' && !field.optional) {
    // On the line of the inputs beside it, which stand under their labels.
    return (
      <Switch size="sm" label={label} checked={written === 'on'} onChange={(event) => void commit(event.currentTarget.checked ? 'on' : 'off')}
        style={{ alignSelf: 'end', minHeight: rem(DIALOG_CONTROL), display: 'flex', alignItems: 'center' }} {...(problem === undefined ? {} : { error: problem })} />
    );
  }
  if (field.choices !== undefined) {
    return (
      <Select size="sm" label={label} data={[...field.choices]} value={shown === '' ? null : shown} searchable selectFirstOptionOnChange clearable={field.optional} allowDeselect={false}
        placeholder={field.optional ? 'empty' : field.takes} onChange={(value) => void commit(value ?? '')} {...(problem === undefined ? {} : { error: problem })} />
    );
  }
  return (
    <TextInput size="sm" label={label} value={text} placeholder={field.optional ? 'empty' : field.takes} title={field.takes} aria-busy={busy}
      onChange={(event) => setText(event.currentTarget.value)} onBlur={() => void commit(text)}
      onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) void commit(text); }}
      {...(problem === undefined ? {} : { error: problem })} />
  );
}

/** A template or a product asked for its terms: each is typed, and the line the dialog writes of them is sent. */
function TermsForm({ what, verb, line, send, onDone }: {
  readonly what: DraftTemplate;
  /** What the button that sends them says: `Fill` for a template, `Add` for a row. */
  readonly verb: string;
  readonly line: (values: Readonly<Record<string, string>>) => string;
  readonly send: Send;
  readonly onDone: () => void;
}) {
  const [values, setValues] = useState<Readonly<Record<string, string>>>({});
  const [problem, setProblem] = useState<string | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const set = (term: DraftTerm, value: string) => setValues((before) => ({ ...before, [term.name]: value }));
  const submit = async () => {
    if (busy) return;
    setBusy(true);
    try {
      const reason = await send(line(values));
      if (reason === undefined) onDone();
      else setProblem(reason);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Stack gap="xs" role="group" aria-label={what.title} data-testid={`terms-${what.word}`}>
      <SimpleGrid cols={{ base: 1, xs: 2, md: 3 }} spacing="sm">
        {/* The terms were asked for, so the first takes the keyboard. */}
        {what.takes.map((term, at) => (term.choices !== undefined
          ? <Select key={term.name} size="sm" label={term.title} withAsterisk={term.required} data={[...term.choices]} value={values[term.name] ?? null} searchable selectFirstOptionOnChange
              autoFocus={at === 0} onChange={(value) => set(term, value ?? '')} />
          : <TextInput key={term.name} size="sm" label={term.title} withAsterisk={term.required} value={values[term.name] ?? ''} placeholder={term.takes} title={term.takes}
              autoFocus={at === 0}
              onChange={(event) => set(term, event.currentTarget.value)}
              onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) void submit(); }} />))}
      </SimpleGrid>
      {problem !== undefined && <Text size="sm" c="red" role="alert">{problem}</Text>}
      <Group gap="xs">
        <Button size="sm" onClick={() => void submit()} loading={busy}>{verb} {what.title}</Button>
        <Button size="sm" variant="default" onClick={onDone}>Close</Button>
      </Group>
    </Stack>
  );
}

const markWord = (mark: DraftRow['mark']): string => (mark === 'made' ? 'new' : mark ?? '');

/** A table under the draft's row: its rows as the draft leaves them, in a grid. Read-only on Original, with no `send`. */
function UnderGrid({ table, send }: { readonly table: DraftTable; readonly send?: Send }) {
  const [problem, setProblem] = useState<string | undefined>(undefined);
  const [adding, setAdding] = useState<DraftTemplate | undefined>(undefined);
  const columns = useMemo((): GridColumn<DraftRow>[] => [
    ...table.fields.map((field, at): GridColumn<DraftRow> => ({
      name: field.name,
      header: field.unit === undefined ? field.title : `${field.title} (${field.unit})`,
      type: 'text',
      value: (row) => {
        const written = row.written[at];
        if (written === undefined) throw new Error(`The row ${row.key} of ${table.word} holds no value of ${field.name}, its field at ${at}.`);
        const shown = shownOf(field, written);
        return shown === '' ? undefined : shown;
      },
      ...(FIGURES.has(field.form) ? { align: 'right' as const } : {}),
      ...(send !== undefined && field.editable ? { editable: true } : {}),
    })),
    ...(send === undefined ? [] : [
      { name: MARK, header: MARK, type: 'text' as const, value: (row: DraftRow) => markWord(row.mark), width: 88 },
      actionColumn<DraftRow>(ACTIONS, [{ name: REMOVE, label: 'Remove' }], { header: '', width: 92 }),
    ]),
  ], [table.fields, send]);
  const onEdit = useCallback(async (intent: GridEditIntent<DraftRow>) => {
    if (send === undefined) { intent.refuse('Original is read-only.'); return; }
    // Enter on a cell as it stood writes nothing.
    if (intent.value === intent.seenText) return;
    const field = table.fields.find((f) => f.name === intent.column);
    if (field === undefined) throw new Error(`The grid of ${table.word} reported an edit of a column it was not given: ${intent.column}.`);
    const reason = await send(cellLine(table, intent.row.key, field, intent.value));
    if (reason !== undefined && !intent.signal.aborted) intent.refuse(reason);
  }, [send, table]);
  const onAction = useCallback((event: GridActionEvent<DraftRow>) => {
    if (send === undefined || event.action !== REMOVE) throw new Error(`The grid of ${table.word} reported an action it was not given: ${event.action}.`);
    void send(removeLine(table, event.row.key)).then(setProblem);
  }, [send, table]);
  return (
    <Stack gap="xs" data-testid={`under-${table.table}`}>
      <Text size="sm" fw={600}>{table.title}</Text>
      {table.rows.length === 0
        ? <Text size="sm" c="dimmed">No rows.</Text>
        : (
          <div aria-label={table.title} data-rows={table.rows.length}>
            <QubeGrid<DraftRow> columns={columns} rows={table.rows} density="compact" viewportWidth="stretch" viewportHeight={compactGridHeight(Math.min(table.rows.length, ROWS_DRAWN))} locale="en-US" hideColumnMenu
              {...(send === undefined ? {} : { onEdit, onAction })} />
          </div>
        )}
      {problem !== undefined && <Text size="sm" c="red" role="alert">{problem}</Text>}
      {send !== undefined && table.makes.length > 0 && (adding === undefined
        ? <Group gap="xs">{table.makes.map((product) => <Button key={product.word} size="sm" variant="default" onClick={() => { setProblem(undefined); setAdding(product); }}>Add {product.title}…</Button>)}</Group>
        : <TermsForm what={adding} verb="Add" line={(values) => makeLine(adding, values)} send={send} onDone={() => setAdding(undefined)} />)}
    </Stack>
  );
}

/** The form's fields by section and the tables under the row: the draft on Live, and the source's row on Original. */
function FormBody({ form, original, send }: { readonly form: DraftForm; readonly original?: NonNullable<DraftForm['original']>; readonly send?: Send }) {
  const [filling, setFilling] = useState<DraftTemplate | undefined>(undefined);
  const [problem, setProblem] = useState<string | undefined>(undefined);
  const under = original === undefined ? form.under : original.under;
  const fill = (template: DraftTemplate) => {
    setProblem(undefined);
    // A template that takes nothing has nothing to ask.
    if (template.takes.length > 0 || send === undefined) { setFilling(template); return; }
    void send(templateLine(template, {})).then(setProblem);
  };
  return (
    <Stack gap="md">
      {send !== undefined && form.templates.length > 0 && (
        <Stack gap="xs">
          <Group gap="xs" role="group" aria-label="Templates">
            <Text size="sm" fw={600}>Fill from a template</Text>
            {form.templates.map((template) => <Button key={template.word} size="sm" variant="default" title={template.title} onClick={() => fill(template)}>{template.word}</Button>)}
          </Group>
          {filling !== undefined && <TermsForm what={filling} verb="Fill as" line={(values) => templateLine(filling, values)} send={send} onDone={() => setFilling(undefined)} />}
          {problem !== undefined && <Text size="sm" c="red" role="alert">{problem}</Text>}
        </Stack>
      )}
      {sectionsOf(form.fields).map((section) => (
        <Stack key={section.title ?? ''} gap="xs" role="group" {...(section.title === undefined ? {} : { 'aria-label': section.title })}>
          {section.title !== undefined && <Text size="sm" fw={600}>{section.title}</Text>}
          <SimpleGrid cols={{ base: 1, xs: 2, md: 3 }} spacing="sm" verticalSpacing="xs">
            {section.fields.map(({ field, written, changed, index }) => {
              const was = original?.written[index];
              if (original !== undefined && was === undefined) throw new Error(`Original holds no value of ${field.name}, the field at ${index}.`);
              return <FieldBox key={field.name} field={field} written={was ?? written} changed={original === undefined && changed} {...(send === undefined ? {} : { send })} />;
            })}
          </SimpleGrid>
        </Stack>
      ))}
      {under.map((table) => <UnderGrid key={table.table} table={table} {...(send === undefined ? {} : { send })} />)}
    </Stack>
  );
}

export interface DraftDialogProps {
  /** The page's draft; none while no dialog is open. */
  readonly draft: LineDraft | undefined;
  readonly workspaceId: string;
  /** The revision the page shows: a form is read again once it moves, and the server then says the draft was left. */
  readonly revision: number;
  readonly api: Pick<Api, 'draft'>;
  readonly door: Pick<CommandDoor, 'enter' | 'view' | 'redraft'>;
  /** Takes a pending line back. */
  readonly drop: (index: number) => void;
  /** Discards the draft, as `cancel` does: a draft is the page's, and one the workspace has left must still close. */
  readonly discard: () => void;
  /** One sentence the page says above the form of a table, such as what its measures do not read yet. */
  readonly note?: (table: string) => string | undefined;
}

export function DraftDialog({ draft, ...rest }: DraftDialogProps) {
  const phone = useMediaQuery(`(max-width: ${em(767.98)})`, false, { getInitialValueInEffect: false });
  const [confirming, setConfirming] = useState(false);
  const close = () => {
    if (draft === undefined) return;
    if (draft.lines.length === 0) rest.discard();
    else setConfirming(true);
  };
  // Escape closes this dialog only when it is pressed in it: the line typed over it (Ctrl/⌘+K) is a dialog of its own,
  // and Escape there closes the line alone. An open list of choices takes its own Escape first, as Mantine marks it.
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape' || event.defaultPrevented || event.nativeEvent.isComposing) return;
    if (event.target instanceof Element && event.target.getAttribute('data-mantine-stop-propagation') === 'true') return;
    event.stopPropagation();
    if (confirming) setConfirming(false);
    else close();
  };
  return (
    <Modal opened={draft !== undefined} onClose={close} closeOnEscape={false} onKeyDown={onKeyDown} fullScreen={phone} size="64rem"
      title={draft === undefined ? '' : <DraftTitle draft={draft} />} data-testid="draft-dialog">
      {/* Made anew for each row's dialog, so one opens with nothing said of the last. */}
      {draft !== undefined && <DraftBody key={`${draft.table}/${draft.key}`} draft={draft} confirming={confirming} setConfirming={setConfirming} close={close} {...rest} />}
    </Modal>
  );
}

function DraftTitle({ draft }: { readonly draft: LineDraft }) {
  return (
    <Group gap="xs" wrap="nowrap">
      <Text fw={600} truncate>{draft.key}</Text>
      <Badge variant="light" color={draft.lines.length === 0 ? 'gray' : 'blue'} data-testid="draft-pending">{draft.lines.length === 0 ? 'nothing pending' : `${draft.lines.length} pending`}</Badge>
    </Group>
  );
}

function DraftBody({ draft, workspaceId, revision, api, door, drop, discard, note, confirming, setConfirming, close }: Omit<DraftDialogProps, 'draft'> & {
  readonly draft: LineDraft;
  readonly confirming: boolean;
  readonly setConfirming: (confirming: boolean) => void;
  readonly close: () => void;
}) {
  const [tab, setTab] = useState<string | null>('live');
  // The question takes the keyboard as it is asked: Enter keeps the draft, and Tab reaches Discard.
  const keep = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (confirming) keep.current?.focus(); }, [confirming]);
  const [problem, setProblem] = useState<string | undefined>(undefined);
  const [saving, setSaving] = useState(false);
  // What the server answers for a draft at a revision never changes: it is asked once, and the form of the lines
  // before stays drawn while the next is read.
  const form = useQuery({
    queryKey: ['draft', workspaceId, revision, draft],
    queryFn: ({ signal }) => api.draft(workspaceId, draft, signal),
    placeholderData: keepPreviousData, staleTime: Infinity, retry: false,
  });
  // The line the draft last gained from this dialog, and whether the form drawn is of the draft as it now is. Until it
  // is, a field still shows what the lines before left, so leaving it would send its line a second time: the same line
  // is sent once, whenever the keyboard leaves the field (spine 6).
  const gained = useRef<string | undefined>(undefined);
  const drawn = useRef(true);
  drawn.current = !form.isPlaceholderData && !form.isFetching;
  const send = useCallback<Send>(async (line) => {
    if (!drawn.current && line === gained.current) return undefined;
    // The dialog shows what the draft gained itself: the sentence is heard here, and no notice is raised.
    const ended = await door.enter(line, undefined, door.view(), () => undefined);
    if (ended !== undefined && 'sentence' in ended) return ended.sentence;
    gained.current = line;
    return undefined;
  }, [door]);
  const save = async () => {
    if (saving) return;
    setSaving(true);
    try {
      const ended = await door.enter('apply', undefined, door.view());
      setProblem(ended !== undefined && 'sentence' in ended ? ended.sentence : undefined);
    } finally {
      setSaving(false);
    }
  };
  const footer = (
    <Group justify="space-between" gap="xs" pt="xs" style={{ position: 'sticky', bottom: 0, background: 'var(--mantine-color-body)', borderTop: '1px solid var(--mantine-color-default-border)' }}>
      {confirming
        ? (
          <Group gap="xs" role="alertdialog" aria-label="Discard the draft">
            <Text size="sm">Discard {draft.lines.length === 1 ? 'the pending line' : `the ${draft.lines.length} pending lines`}?</Text>
            <Button ref={keep} size="sm" variant="default" onClick={() => setConfirming(false)}>Keep editing</Button>
            <Button size="sm" color="red" onClick={discard}>Discard</Button>
          </Group>
        )
        : <Text size="sm" c="dimmed">Nothing is written until Save.</Text>}
      <Group gap="xs">
        <Button size="sm" variant="default" onClick={close}>Cancel</Button>
        <Button size="sm" onClick={() => void save()} loading={saving} disabled={draft.lines.length === 0} {...(draft.lines.length === 0 ? { title: 'Nothing is pending.' } : {})}>Save</Button>
      </Group>
    </Group>
  );
  if (form.isError) {
    // A draft the workspace has left (note 57): said with the server's sentence, and read again only when asked.
    const left = form.error instanceof ApiError && form.error.envelope.code === 'RevisionMoved';
    return (
      <Stack gap="sm">
        <Alert color={left ? 'yellow' : 'red'} title={left ? 'The workspace has moved' : 'The dialog could not be read'} role="alert">{form.error.message}</Alert>
        {problem !== undefined && <Text size="sm" c="red" role="alert">{problem}</Text>}
        <Group justify="flex-end" gap="xs">
          <Button size="sm" variant="default" onClick={discard}>Discard</Button>
          {left && (
            <Button size="sm" onClick={() => void door.redraft().then((reason) => setProblem(reason?.sentence))}>
              Read {draft.lines.length === 0 ? 'it' : draft.lines.length === 1 ? 'the line' : `the ${draft.lines.length} lines`} again
            </Button>
          )}
        </Group>
      </Stack>
    );
  }
  if (form.data === undefined) return <Group gap="xs"><Loader size="sm" /><Text size="sm">Reading {draft.key}…</Text></Group>;
  const read = form.data;
  const said = note?.(read.table);
  // The lines the server did not reach: the one before them no longer reads (note 54).
  const unread = draft.lines.slice(read.lines.length);
  return (
    <Stack gap="sm">
      {said !== undefined && <Text size="sm" c="dimmed" data-testid="draft-note">{said}</Text>}
      <Tabs value={tab} onChange={setTab} keepMounted={false}>
        <Tabs.List>
          <Tabs.Tab value="live">Live</Tabs.Tab>
          <Tabs.Tab value="original" disabled={read.original === undefined} {...(read.original === undefined ? { title: 'The workspace made this row: it has no original.' } : {})}>Original</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="live" pt="sm">
          <FormBody form={read} send={send} />
        </Tabs.Panel>
        {read.original !== undefined && (
          <Tabs.Panel value="original" pt="sm">
            <FormBody form={read} original={read.original} />
          </Tabs.Panel>
        )}
      </Tabs>
      {draft.lines.length > 0 && (
        <Stack gap={4} role="group" aria-label="Pending lines" data-testid="draft-lines">
          <Text size="sm" fw={600}>Pending</Text>
          {read.lines.map((line, at) => (
            <Group key={`${at}:${line.line}`} gap="xs" wrap="nowrap" align="flex-start">
              <CloseButton size="sm" aria-label={`Take back: ${line.line}`} title="Take this line back" onClick={() => drop(at)} />
              <Stack gap={0}>
                <Text size="sm" ff="monospace">{line.line}</Text>
                <Text size="xs" c={line.refused === undefined ? 'dimmed' : 'red'} {...(line.refused === undefined ? {} : { role: 'alert' })}>{line.refused?.sentence ?? line.said}</Text>
              </Stack>
            </Group>
          ))}
          {unread.map((line, at) => (
            <Group key={`unread:${at}:${line}`} gap="xs" wrap="nowrap" align="flex-start">
              <CloseButton size="sm" aria-label={`Take back: ${line}`} title="Take this line back" onClick={() => drop(read.lines.length + at)} />
              <Stack gap={0}>
                <Text size="sm" ff="monospace">{line}</Text>
                <Text size="xs" c="dimmed">{form.isPlaceholderData ? 'Being read.' : 'Not read: a line before it no longer reads.'}</Text>
              </Stack>
            </Group>
          ))}
        </Stack>
      )}
      {problem !== undefined && <Alert color="red" role="alert" data-testid="draft-refused">{problem}</Alert>}
      {footer}
    </Stack>
  );
}
