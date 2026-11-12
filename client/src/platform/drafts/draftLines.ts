import type { DraftForm, DraftTable, DraftTemplate, FieldInput } from '../contracts';

// ADR-0016 note 57 of 2026-11-12: a dialog has no rule of its own. Every change made in it is a line the server reads
// in the draft, and these are the lines: the kernel's own verbs, said by the words the form gives. Nothing here knows a
// table or a field.

/** What a line writes for no value: an emptied field is sent as this, and the server says where a field may not be empty. */
export const EMPTY = 'empty';

const valueOf = (text: string): string => { const typed = text.trim(); return typed === '' ? EMPTY : typed; };

/** A field of the draft's own row: a line typed in a draft names no row for it. */
export const fieldLine = (field: Pick<FieldInput, 'word'>, text: string): string => `set ${field.word} ${valueOf(text)}`;

/** A cell of a row under the draft's row. */
export const cellLine = (table: Pick<DraftTable, 'word'>, key: string, field: Pick<FieldInput, 'word'>, text: string): string =>
  `set ${table.word} ${key} ${field.word} ${valueOf(text)}`;

/** A row under the draft's row, taken away. */
export const removeLine = (table: Pick<DraftTable, 'word'>, key: string): string => `remove ${table.word} ${key}`;

/** Each term filled, by its name, in the order the form asks for them; one left empty is left out, for the server to miss or default. */
const termsOf = (takes: DraftTemplate['takes'], values: Readonly<Record<string, string>>): string[] =>
  takes.flatMap((term) => { const typed = (values[term.name] ?? '').trim(); return typed === '' ? [] : [`${term.name}=${typed}`]; });

/** A template written over the draft's row. */
export const templateLine = (template: DraftTemplate, values: Readonly<Record<string, string>>): string => [`set as ${template.word}`, ...termsOf(template.takes, values)].join(' ');

/** A row made under the draft's row by a product: the row above is the draft's, and is not named. */
export const makeLine = (product: DraftTemplate, values: Readonly<Record<string, string>>): string => [`new ${product.word}`, ...termsOf(product.takes, values)].join(' ');

/** A section of a dialog: a group of the table's fields, each with its place among the form's fields. */
export interface Section {
  /** The group's name; `Other` for the fields of no group beside grouped ones; none where the table groups nothing. */
  readonly title: string | undefined;
  readonly fields: readonly (DraftForm['fields'][number] & { readonly index: number })[];
}

/** The sections a form's fields are drawn in: the groups in the order they first appear, then the fields of no group. */
export function sectionsOf(fields: DraftForm['fields']): readonly Section[] {
  const placed = fields.map((f, index) => ({ ...f, index }));
  const groups = [...new Set(placed.flatMap((f) => (f.field.group === undefined ? [] : [f.field.group])))];
  const loose = placed.filter((f) => f.field.group === undefined);
  return [
    ...groups.map((title) => ({ title, fields: placed.filter((f) => f.field.group === title) })),
    ...(loose.length === 0 ? [] : [{ title: groups.length === 0 ? undefined : 'Other', fields: loose }]),
  ];
}
