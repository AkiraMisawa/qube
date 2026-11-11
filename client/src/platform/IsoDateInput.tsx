import { DateInput, type DateInputProps } from '@mantine/dates';
import { IconCalendar } from '@tabler/icons-react';
import { isIsoDate } from './forms';

type Shared = Omit<DateInputProps, 'value' | 'defaultValue' | 'onChange' | 'valueFormat' | 'dateParser' | 'clearable' | 'allowDeselect'> & {
  /** The date as `yyyy-MM-dd`. Anything else is shown as no date, and whoever holds it says what is wrong with it. */
  readonly value: string | undefined;
};

/**
 * A date as the workbench writes it everywhere, `yyyy-MM-dd` (ISO 8601), typed or picked from a calendar. A native
 * `<input type="date">` shows the browser's own locale instead (`11/11/2026` in en-US, `2026/11/11` in ja-JP), and a
 * page cannot tell it otherwise (docs/design-guidelines.md §10).
 *
 * Only a date that exists, typed in full, is taken: `2026-02-30` or `2026-1-5` is never rolled over or completed into
 * another date (Mantine's own parser would make `2026-03-02` of the first). Text that is not one is put back to the
 * date in force when the field is left. A date that cannot be cleared never reports no date; one that can (`clearable`)
 * reports `undefined` when its text is emptied or its clear button pressed. The calendar glyph says, as the native
 * input's did, that a click or a tap opens the calendar.
 */
export function IsoDateInput(props: Shared & ({ readonly clearable?: false; readonly onChange: (date: string) => void }
  | { readonly clearable: true; readonly onChange: (date: string | undefined) => void })) {
  const { value, onChange, clearable, ...rest } = props;
  return (
    <DateInput {...rest} value={value !== undefined && isIsoDate(value) ? value : null} valueFormat="YYYY-MM-DD"
      dateParser={(text) => (isIsoDate(text) ? text : null)} clearable={clearable === true}
      // A press on the glyph reaches the input beneath it; a clear button beside it must take its own.
      rightSection={<IconCalendar size={16} stroke={1.75} aria-hidden />} rightSectionPointerEvents={clearable === true ? 'all' : 'none'}
      onChange={(date) => {
        if (date !== null) onChange(date);
        else if (props.clearable === true) props.onChange(undefined);
        else throw new Error('A date that cannot be cleared reported no date.');
      }} />
  );
}
