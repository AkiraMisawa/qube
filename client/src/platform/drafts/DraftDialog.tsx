import { useCallback, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Alert, Badge, Button, CloseButton, Fieldset, Group, Loader, Modal, Select, SimpleGrid, Stack, Switch, Tabs, Text, TextInput, em, rem } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { actionColumn, type GridActionEvent, type GridColumn, type GridEditIntent } from '@ex-grid/core';
import { ApiError, type Api } from '../api';
import type { CommandDoor } from '../commands/commands';
import type { DraftForm, DraftRow, DraftTable, DraftTemplate, DraftTerm, FieldInput, LineDraft } from '../contracts';
import { readFlag } from '../flags';
import { useColumnWidths } from '../grid/columnWidths';
import { QubeGrid } from '../grid/QubeGrid';
import { cellLine, fieldLine, makeLine, removeLine, sectionsOf, shownOf, templateLine } from './draftLines';
import { compactGridHeight } from '../grid/metrics';

// A row's dialog (ADR-0016 §10, note 57 of 2026-11-12), the same for every table: it is drawn from the form the
// server answers for the page's draft, and every change made in it is a line the server reads in that draft. It has
// no rule of its own: a value is refused by the server's sentence, at the field it was typed in.

/** Sends a line for the draft; answers why where the draft gained nothing. */
type Send = (line: string) => Promise<string | undefined>;

/** How the form's controls reach the draft: none of it on Original, which is read-only. */
interface Sending {
  readonly send: Send;
  /** Whether the form drawn is of the draft as it now is; until it is, a control shows what the lines before left. */
  readonly drawn: boolean;
}

const REMOVE = 'remove';
const ACTIONS = 'Actions';
const MARK = 'Draft';
/** A dialog's Control Height (docs/design-guidelines.md): what a `sm` input is as high as. */
const DIALOG_CONTROL = 36;
/** How many rows a table under the row is as high as, before it scrolls. */
const ROWS_DRAWN = 8;
/** Over the marks Mantine draws in its inputs (a list's chevron), which stand above the form's own layer. */
const FOOTER_LAYER = 2;

const labelOf = (field: Pick<FieldInput, 'title' | 'unit'>, changed: boolean): ReactNode => (
  <>
    {field.unit === undefined ? field.title : `${field.title} (${field.unit})`}
    {changed && <Text span size="xs" c="blue" fw={600} data-changed> · changed</Text>}
  </>
);

/** One field of the draft's row: what it holds as the draft leaves it, and the control its form is typed with. */
function FieldBox({ field, written, changed, sending }: {
  readonly field: FieldInput;
  readonly written: string;
  readonly changed: boolean;
  /** None on Original, which is read-only. */
  readonly sending?: Sending;
}) {
  const shown = shownOf(field, written);
  const [seen, setSeen] = useState(shown);
  const [text, setText] = useState(shown);
  const [problem, setProblem] = useState<string | undefined>(undefined);
  // What the field last sent, until the form shows what the draft made of it or the server refuses it: the same text
  // is sent once, however often the keyboard leaves the field meanwhile (spine 6). And how many of its lines are
  // being read, with which of them is the latest.
  const [sent, setSent] = useState<string | undefined>(undefined);
  const [flying, setFlying] = useState(0);
  const latest = useRef(0);
  // The form is of the draft as it now is, and none of the field's lines is being read: the field shows what the
  // draft holds, and what was said of the older value is gone. A text typed on since is the user's until it is sent.
  if (sending?.drawn === true && flying === 0 && (seen !== shown || sent !== undefined)) {
    if (sent === undefined ? text === seen : text.trim() === sent) { setText(shown); setProblem(undefined); }
    setSeen(shown);
    setSent(undefined);
  }
  const label = labelOf(field, changed);
  if (sending === undefined || !field.editable) {
    return <TextInput size="sm" label={label} value={shown} readOnly variant="filled" {...(sending !== undefined ? { title: 'No command sets this field.' } : {})} />;
  }
  const commit = async (next: string) => {
    const typed = next.trim();
    if (typed === sent) return;
    if (sent === undefined && typed === shown) { setProblem(undefined); return; }
    setSent(typed);
    setFlying((n) => n + 1);
    const mine = (latest.current += 1);
    try {
      const reason = await sending.send(fieldLine(field, next));
      // An answer to an earlier text of the field says nothing of the one typed since.
      if (mine !== latest.current) return;
      setProblem(reason);
      if (reason !== undefined) setSent(undefined);
    } finally {
      setFlying((n) => n - 1);
    }
  };
  // A switch and a list show what they sent while it is read, so that a second press is of what the first left.
  const held = sent ?? shown;
  if (field.form === 'flag' && !field.optional) {
    const on = readFlag(sent ?? written);
    if (on === undefined) throw new Error(`The flag ${field.name} holds ${written}, which says neither on nor off.`);
    // On the line of the inputs beside it, which stand under their labels.
    return (
      <Switch size="sm" label={label} checked={on} onChange={(event) => void commit(event.currentTarget.checked ? 'on' : 'off')}
        style={{ alignSelf: 'end', minHeight: rem(DIALOG_CONTROL), display: 'flex', alignItems: 'center' }} {...(problem === undefined ? {} : { error: problem })} />
    );
  }
  if (field.choices !== undefined) {
    // What the row holds is drawn though the form does not list it: a list that drew nothing would say the field is empty.
    const choices = held === '' || field.choices.includes(held) ? field.choices : [held, ...field.choices];
    return (
      <Select size="sm" label={label} data={[...choices]} value={held === '' ? null : held} searchable selectFirstOptionOnChange clearable={field.optional} allowDeselect={false}
        placeholder={field.optional ? 'empty' : field.takes} onChange={(value) => void commit(value ?? '')} {...(problem === undefined ? {} : { error: problem })} />
    );
  }
  return (
    <TextInput size="sm" label={label} value={text} placeholder={field.optional ? 'empty' : field.takes} title={field.takes} aria-busy={flying > 0}
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
  // The terms are as they were sent while their line is read: the fields take nothing until it is answered.
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
              autoFocus={at === 0} readOnly={busy} onChange={(value) => set(term, value ?? '')} />
          : <TextInput key={term.name} size="sm" label={term.title} withAsterisk={term.required} value={values[term.name] ?? ''} placeholder={term.takes} title={term.takes}
              autoFocus={at === 0} readOnly={busy}
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
function UnderGrid({ table, send, leave }: { readonly table: DraftTable; readonly send?: Send; readonly leave: () => void }) {
  const [problem, setProblem] = useState<string | undefined>(undefined);
  const [adding, setAdding] = useState<DraftTemplate | undefined>(undefined);
  const declared = useMemo((): GridColumn<DraftRow>[] => [
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
      // A number stands against the right edge, as every grid draws one; which fields hold numbers is the form's to say.
      ...(field.number ? { align: 'right' as const } : {}),
      ...(send !== undefined && field.editable ? { editable: true } : {}),
    })),
    ...(send === undefined ? [] : [
      { name: MARK, header: MARK, type: 'text' as const, value: (row: DraftRow) => markWord(row.mark), width: 88 },
      actionColumn<DraftRow>(ACTIONS, [{ name: REMOVE, label: 'Remove' }], { header: '', width: 92 }),
    ]),
  ], [table.fields, table.word, send]);
  // A width dragged is kept by the table, whichever row's dialog shows it (docs/design-guidelines.md "Column widths").
  const { columns, onColumnWidthChanged } = useColumnWidths(`draft/${table.table}`, declared, { hideColumnMenu: true });
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
          <div role="group" aria-label={table.title} data-rows={table.rows.length}>
            <QubeGrid<DraftRow> columns={columns} rows={table.rows} density="compact" viewportWidth="stretch" viewportHeight={compactGridHeight(Math.min(table.rows.length, ROWS_DRAWN))} locale="en-US" hideColumnMenu
              onColumnWidthChanged={onColumnWidthChanged} onLeave={leave} {...(send === undefined ? {} : { onEdit, onAction })} />
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
function FormBody({ form, original, sending, leave }: {
  readonly form: DraftForm;
  readonly original?: NonNullable<DraftForm['original']>;
  readonly sending?: Sending;
  /** Escape pressed in a grid of the form with nothing of the grid's left to dismiss (ex-grid ADR-0070): the dialog's. */
  readonly leave: () => void;
}) {
  const send = sending?.send;
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
              return <FieldBox key={field.name} field={field} written={was ?? written} changed={original === undefined && changed} {...(sending === undefined ? {} : { sending })} />;
            })}
          </SimpleGrid>
        </Stack>
      ))}
      {under.map((table) => <UnderGrid key={table.table} table={table} leave={leave} {...(send === undefined ? {} : { send })} />)}
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
  readonly door: Pick<CommandDoor, 'enter' | 'redraft' | 'keyboard'>;
  /** Takes a pending line back from the draft the press was made on; false where that draft has changed since. */
  readonly drop: (seen: LineDraft, index: number) => boolean;
  /** Discards the draft, as `cancel` does: a draft is the page's, and one the workspace has left must still close. */
  readonly discard: () => void;
  /** One sentence the page says above the form of a table, such as what its measures do not read yet. */
  readonly note?: (table: string) => string | undefined;
}

export function DraftDialog({ draft, ...rest }: DraftDialogProps) {
  const phone = useMediaQuery(`(max-width: ${em(767.98)})`, false, { getInitialValueInEffect: false });
  // The draft the user is asked about discarding. The question is of that draft alone: every change makes another
  // (`useDraft`), so it is gone once a line is gained or taken back, and with the dialog.
  const [asked, setAsked] = useState<LineDraft | undefined>(undefined);
  const confirming = draft !== undefined && asked === draft;
  const close = () => {
    if (draft === undefined) return;
    if (draft.lines.length === 0) rest.discard();
    else setAsked(draft);
  };
  /** Escape: it asks as Cancel does, and pressed again it answers the question with no. */
  const escape = () => {
    if (confirming) setAsked(undefined);
    else close();
  };
  // Escape closes this dialog only when it is pressed in it: the line typed over it (Ctrl/⌘+K) is a dialog of its
  // own, and Escape there closes the line alone. An open list of choices takes its own Escape first, as Mantine
  // marks it, and a grid of the form its own too, handing on the one that has nothing of the grid's left to dismiss.
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape' || event.defaultPrevented || event.nativeEvent.isComposing) return;
    if (event.target instanceof Element && event.target.getAttribute('data-mantine-stop-propagation') === 'true') return;
    event.stopPropagation();
    escape();
  };
  // The dialog has closed, written or discarded: the keyboard goes back to the grid it stood over (§10: `apply`
  // "returns to the row"). The grid takes it only while nothing else has, or from this dialog, which is still drawn
  // as it leaves and may hold the keyboard on its close button.
  const frame = useRef<HTMLDivElement>(null);
  const open = draft !== undefined;
  const was = useRef(open);
  const { door } = rest;
  useEffect(() => {
    if (was.current && !open) {
      if (frame.current === null) door.keyboard();
      else door.keyboard(frame.current);
    }
    was.current = open;
  }, [open, door]);
  return (
    <Modal ref={frame} opened={draft !== undefined} onClose={close} closeOnEscape={false} onKeyDown={onKeyDown} fullScreen={phone} size="64rem"
      title={draft === undefined ? '' : <DraftTitle draft={draft} />} closeButtonProps={{ 'aria-label': 'Close the dialog' }} data-testid="draft-dialog">
      {/* Made anew for each row's dialog, so one opens with nothing said of the last. */}
      {draft !== undefined && <DraftBody key={`${draft.table}/${draft.key}`} draft={draft} confirming={confirming} keep={() => setAsked(undefined)} close={close} escape={escape} {...rest} />}
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

const countOf = (lines: number): string => (lines === 1 ? 'the pending line' : `the ${lines} pending lines`);

function DraftBody({ draft, workspaceId, revision, api, door, drop, discard, note, confirming, keep, close, escape }: Omit<DraftDialogProps, 'draft'> & {
  readonly draft: LineDraft;
  /** Whether the user is being asked about discarding this draft, and how the question is answered with no. */
  readonly confirming: boolean;
  readonly keep: () => void;
  readonly close: () => void;
  /** What Escape does in the dialog, for a grid of the form to do with the Escape it hands on. */
  readonly escape: () => void;
}) {
  const [tab, setTab] = useState<string | null>('live');
  const root = useRef<HTMLDivElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  // The question takes the keyboard as it is asked: Enter keeps the draft, and Tab reaches Discard.
  const keeping = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (confirming) keeping.current?.focus(); }, [confirming]);
  const question = useId();
  const [problem, setProblem] = useState<string | undefined>(undefined);
  const [saving, setSaving] = useState(false);
  // What the server answers for a draft at a revision never changes: it is asked once, and the form of the lines
  // before stays drawn while the next is read.
  const form = useQuery({
    queryKey: ['draft', workspaceId, revision, draft],
    queryFn: ({ signal }) => api.draft(workspaceId, draft, signal),
    placeholderData: keepPreviousData, staleTime: Infinity, retry: false,
  });
  /** Whether the form drawn is of the draft as it now is. */
  const drawn = form.data !== undefined && !form.isPlaceholderData;
  // The draft this dialog is of, as a line sent from it names it: the door reads the line on that dialog's draft as
  // its turn finds it, and refuses it once the dialog has closed (spine 6). Never the page's view at the press,
  // which is a grid's once the dialog is gone.
  const of = useRef(draft);
  of.current = draft;
  // The lines of this dialog still being read, and whether Save is: what the form sends meanwhile is not sent.
  const flights = useRef(new Set<Promise<string | undefined>>());
  const writing = useRef(false);
  const send = useCallback<Send>((line) => {
    if (writing.current) return Promise.resolve('The draft is being saved: nothing was added to it.');
    // The dialog shows what the draft gained itself: the sentence is heard here, and no notice is raised.
    const flight = door.enter(line, undefined, { table: of.current.table, draft: of.current }, () => undefined)
      .then((ended) => (ended !== undefined && 'sentence' in ended ? ended.sentence : undefined));
    flights.current.add(flight);
    void flight.then(() => flights.current.delete(flight));
    return flight;
  }, [door]);
  const save = async () => {
    if (writing.current) return;
    writing.current = true;
    setSaving(true);
    try {
      // A line still being read when Save is pressed is the draft's or it is not. Save waits for each, and writes
      // nothing where one was refused: the user has not seen that refusal yet, and it is said where it was typed.
      const refused = (await Promise.all(flights.current)).some((reason) => reason !== undefined);
      if (refused) { setProblem('A line sent just before Save was refused, so nothing was saved: why is said where it was typed.'); return; }
      const ended = await door.enter('apply', undefined, { table: of.current.table, draft: of.current });
      setProblem(ended !== undefined && 'sentence' in ended ? ended.sentence : undefined);
    } finally {
      writing.current = false;
      setSaving(false);
    }
  };
  // A line is taken back from the list as it is drawn of the draft as it is: a list of an earlier draft, or of one
  // being written, names lines that may not be where they are drawn (spine 6).
  const taking = drawn && !saving;
  const took = useRef(false);
  const takeBack = (at: number) => {
    if (!taking) return;
    took.current = true;
    if (!drop(draft, at)) setProblem('The draft changed as the line was taken back, so nothing was: press it again.');
  };
  // What had the keyboard can go with a drawing: a line taken back, the question answered. The keyboard then stays
  // in the dialog, where Escape is heard: on the line that took the place of the one taken back or the last one
  // left, on Cancel where none is left, and on the dialog otherwise.
  useEffect(() => {
    const here = root.current;
    if (here === null) return;
    if (document.activeElement === document.body) {
      const backs = here.querySelectorAll<HTMLElement>('[data-take-back]');
      const to = took.current ? backs[backs.length - 1] ?? cancel.current : here.closest<HTMLElement>('[role="dialog"]');
      to?.focus();
    }
    if (drawn) took.current = false;
  });
  // Nothing is written of a draft whose form could not be read.
  const savable = draft.lines.length > 0 && !form.isError;
  const footer = (
    <Group justify="space-between" gap="xs" pt="xs"
      style={{ position: 'sticky', bottom: 0, zIndex: FOOTER_LAYER, background: 'var(--mantine-color-body)', borderTop: '1px solid var(--mantine-color-default-border)' }}>
      {confirming
        ? (
          <Group gap="xs" role="alertdialog" aria-label="Discard the draft" aria-describedby={question}>
            <Text size="sm" id={question}>Discard {countOf(draft.lines.length)}?</Text>
            <Button ref={keeping} size="sm" variant="default" onClick={() => { keep(); cancel.current?.focus(); }}>Keep editing</Button>
            <Button size="sm" color="red" onClick={discard}>Discard</Button>
          </Group>
        )
        : <Text size="sm" c="dimmed">Nothing is written until Save.</Text>}
      <Group gap="xs">
        <Button ref={cancel} size="sm" variant="default" onClick={close}>Cancel</Button>
        <Button size="sm" onClick={() => void save()} loading={saving} disabled={!savable} {...(draft.lines.length === 0 ? { title: 'Nothing is pending.' } : {})}>Save</Button>
      </Group>
    </Group>
  );
  const refused = problem !== undefined && <Alert color="red" role="alert" data-testid="draft-refused">{problem}</Alert>;
  if (form.isError) {
    // A draft the workspace has left (note 57): said with the server's sentence, and read again only when asked.
    // Any other failure is said as it is, and the same draft is asked for again when the user says.
    const left = form.error instanceof ApiError && form.error.envelope.code === 'RevisionMoved';
    return (
      <Stack ref={root} gap="sm">
        <Alert color={left ? 'yellow' : 'red'} title={left ? 'The workspace has moved' : 'The dialog could not be read'} role="alert">{form.error.message}</Alert>
        <Group gap="xs">
          {left
            ? (
              <Button size="sm" onClick={() => void door.redraft().then((reason) => setProblem(reason?.sentence))}>
                Read {draft.lines.length === 0 ? 'it' : draft.lines.length === 1 ? 'the line' : `the ${draft.lines.length} lines`} again
              </Button>
            )
            : <Button size="sm" loading={form.isFetching} onClick={() => void form.refetch()}>Try again</Button>}
        </Group>
        {refused}
        {footer}
      </Stack>
    );
  }
  if (form.data === undefined) {
    return (
      <Stack ref={root} gap="sm">
        <Group gap="xs"><Loader size="sm" /><Text size="sm">Reading {draft.key}…</Text></Group>
        {footer}
      </Stack>
    );
  }
  const read = form.data;
  const said = note?.(read.table);
  // The lines the server did not reach: the one before them no longer reads (note 54).
  const unread = draft.lines.slice(read.lines.length);
  const back = (at: number, line: string) => (
    <CloseButton size="sm" data-take-back aria-label={`Take back: ${line}`} title="Take this line back" onClick={() => takeBack(at)} {...(taking ? {} : { 'aria-disabled': true })} />
  );
  return (
    <Stack ref={root} gap="sm">
      {said !== undefined && <Text size="sm" c="dimmed" data-testid="draft-note">{said}</Text>}
      {/* While Save is read the form takes nothing: a line typed behind it would be of a dialog that has closed. */}
      <Fieldset variant="unstyled" disabled={saving}>
        <Tabs value={tab} onChange={setTab} keepMounted={false}>
          <Tabs.List>
            <Tabs.Tab value="live">Live</Tabs.Tab>
            <Tabs.Tab value="original" disabled={read.original === undefined} {...(read.original === undefined ? { title: 'The workspace made this row: it has no original.' } : {})}>Original</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="live" pt="sm">
            <FormBody form={read} sending={{ send, drawn }} leave={escape} />
          </Tabs.Panel>
          {read.original !== undefined && (
            <Tabs.Panel value="original" pt="sm">
              <FormBody form={read} original={read.original} leave={escape} />
            </Tabs.Panel>
          )}
        </Tabs>
      </Fieldset>
      {draft.lines.length > 0 && (
        <Stack gap="xs" role="group" aria-label="Pending lines" aria-busy={!drawn} data-testid="draft-lines">
          <Text size="sm" fw={600}>Pending</Text>
          {/* By its place: the button of a line taken back is the next line's, and the keyboard stays on it. */}
          {read.lines.map((line, at) => (
            <Group key={at} gap="xs" wrap="nowrap" align="flex-start">
              {back(at, line.line)}
              <Stack gap={0}>
                <Text size="sm" ff="monospace">{line.line}</Text>
                <Text size="xs" c={line.refused === undefined ? 'dimmed' : 'red'} {...(line.refused === undefined ? {} : { role: 'alert' })}>{line.refused?.sentence ?? line.said}</Text>
              </Stack>
            </Group>
          ))}
          {unread.map((line, at) => (
            <Group key={read.lines.length + at} gap="xs" wrap="nowrap" align="flex-start">
              {back(read.lines.length + at, line)}
              <Stack gap={0}>
                <Text size="sm" ff="monospace">{line}</Text>
                <Text size="xs" c="dimmed">{drawn ? 'Not read: a line before it no longer reads.' : 'Being read.'}</Text>
              </Stack>
            </Group>
          ))}
        </Stack>
      )}
      {refused}
      {footer}
    </Stack>
  );
}
