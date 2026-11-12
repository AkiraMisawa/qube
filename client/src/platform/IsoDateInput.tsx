import { useEffect, useState } from 'react';
import { Popover, TextInput, type TextInputProps } from '@mantine/core';
import { DatePicker } from '@mantine/dates';
import { IconCalendar } from '@tabler/icons-react';
import { isIsoDate } from './forms';

export type IsoDateInputProps = Omit<TextInputProps, 'value' | 'onChange' | 'size' | 'type' | 'rightSection' | 'rightSectionPointerEvents'> & {
  /** The field's text: a date as `yyyy-MM-dd`, `''` for none, or what has been typed so far. */
  readonly value: string;
  /** The text after each change, typed or picked. */
  readonly onChange: (text: string) => void;
  /** The Control Height (docs/design-guidelines.md §3): `xs` in a row of controls, `sm` in a dialog. The calendar follows. */
  readonly size?: 'xs' | 'sm';
};

/**
 * A date as the workbench writes it everywhere, `yyyy-MM-dd` (ISO 8601), typed, or picked from a calendar that a click
 * or a tap opens. A native `<input type="date">` draws the browser's own locale instead (`11/11/2026` in en-US,
 * `2026/11/11` in ja-JP), and a page cannot tell it otherwise (docs/design-guidelines.md §10).
 *
 * The field is its text: it shows what was typed and reports it as it now reads, so what is on screen is always what
 * its holder has. Text that is not a date that exists (`2026-02-30`, a half-typed `2026-1`) is marked invalid, never
 * rolled over or put back to an older date; its holder judges it with `isIsoDate` and says why. The calendar shows the
 * date the text names, and picking a day writes that day's text.
 */
export function IsoDateInput({ value, onChange, error, size = 'sm', onClick, onBlur, onKeyDown, ...rest }: IsoDateInputProps) {
  const [calendar, setCalendar] = useState(false);
  const date = isIsoDate(value) ? value : null;
  // The holder's own message wins; without one, text that is not a date still marks the field.
  const invalid = error === undefined || error === false ? value !== '' && date === null : error;
  return (
    <Popover opened={calendar} onChange={setCalendar} position="bottom-start" trapFocus={false} closeOnEscape={false}
      withRoles={false}>
      <Popover.Target>
        {/* Escape closes the calendar, not the dialog the field is in: a Mantine Modal hears Escape on the window, in
            the capture phase, and leaves a key whose target is marked as Mantine's own Select marks its open input.
            With the calendar shut, Escape is the dialog's. */}
        <TextInput {...rest} size={size} value={value} error={invalid} autoComplete="off"
          data-mantine-stop-propagation={calendar || undefined}
          rightSection={<IconCalendar size={16} stroke={1.75} aria-hidden />} rightSectionPointerEvents="none"
          onChange={(e) => onChange(e.currentTarget.value)}
          onClick={(e) => { setCalendar(true); onClick?.(e); }}
          onBlur={(e) => { setCalendar(false); onBlur?.(e); }}
          onKeyDown={(e) => {
            if (e.key === 'Escape' && calendar) {
              setCalendar(false);
              return;
            }
            // Enter takes the text as it reads; a calendar left open would offer a day over a date already sent.
            if (e.key === 'Enter') setCalendar(false);
            onKeyDown?.(e);
          }} />
      </Popover.Target>
      {/* A press in the calendar keeps the keyboard in the field. The calendar is drawn again for each date the text
          names, so it opens on that date's month, and follows a date as it is typed. No day is drawn as a weekend:
          Mantine draws those red, and red means an error or a negative amount here (docs/design-guidelines.md §7). */}
      <Popover.Dropdown onMouseDown={(e) => e.preventDefault()}>
        <DatePicker key={String(date)} size={size} value={date} {...(date === null ? {} : { defaultDate: date })} allowDeselect={false}
          weekendDays={[]}
          onChange={(picked) => {
            if (picked === null) throw new Error('The calendar let go of a date it was told to keep.');
            onChange(picked);
            setCalendar(false);
          }} />
      </Popover.Dropdown>
    </Popover>
  );
}

/**
 * A date in force that moves only when its field is left or Enter is pressed, as a toolbar's Value date or Valuation
 * date does. Text that is not a date moves nothing, and `onRefused` is given the sentence that says so and which date
 * stays; the field keeps the text, marked, until it is a date or the date in force is written back.
 */
export function CommittedDateInput({ value, what, onCommit, onRefused, ...rest }:
  Omit<IsoDateInputProps, 'value' | 'onChange' | 'onBlur' | 'onKeyDown'> & {
    /** The date in force, `yyyy-MM-dd`. */
    readonly value: string;
    /** What the date is, in a sentence: `value date`. */
    readonly what: string;
    readonly onCommit: (date: string) => void;
    readonly onRefused: (sentence: string) => void;
  }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => {
    if (draft === value) return;
    if (isIsoDate(draft)) onCommit(draft);
    else onRefused(`The ${what} was not moved: ${draft === '' ? 'no date was given' : `${draft} is not a date as yyyy-MM-dd`}, so it stays ${value}.`);
  };
  return <IsoDateInput {...rest} value={draft} onChange={setDraft} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />;
}
