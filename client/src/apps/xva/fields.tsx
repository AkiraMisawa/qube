import { useEffect, useId, useState, type ReactNode, type Ref } from 'react';
import { Text, TextInput } from '@mantine/core';
import type { Parsed } from './editing';

// The page's form fields (docs/design-guidelines.md §4): a label beside its input, one Control Height.

/** A toolbar field: its label to the left of its input, both one Control Height (docs/design-guidelines.md §4). */
export function ToolbarField({ label, wide, children }: { readonly label: string; readonly wide?: boolean; readonly children: (id: string) => ReactNode }) {
  const id = useId();
  return (
    <div className={wide === true ? 'qwb-toolbar-field qwb-toolbar-field--wide' : 'qwb-toolbar-field'}>
      <Text component="label" htmlFor={id} size="xs" c="dimmed" className="qwb-toolbar-label">{label}</Text>
      {children(id)}
    </div>
  );
}

/**
 * A toolbar field that sends its value on Enter or when left, and only when it changed and reads correctly. A value
 * it cannot read marks the input invalid and is said once below the toolbars, so the row keeps its height.
 */
export function CommitField({ label, value, placeholder, modified, description, parse, onCommit, onIssue, inputRef }: {
  readonly label: string;
  readonly value: string;
  readonly placeholder?: string;
  readonly modified?: boolean;
  /** What a modified value differs from, in words (ADR-0010 §2). */
  readonly description?: string | undefined;
  readonly parse: (text: string) => Parsed;
  readonly onCommit: (value: unknown) => void;
  readonly onIssue: (message: string | undefined) => void;
  /** The input, for a command that puts the keyboard on it. */
  readonly inputRef?: Ref<HTMLInputElement>;
}) {
  const [draft, setDraft] = useState(value);
  const [invalid, setInvalid] = useState(false);
  useEffect(() => setDraft(value), [value]);
  const settle = (message: string | undefined) => { setInvalid(message !== undefined); onIssue(message === undefined ? undefined : `${label}: ${message}`); };
  const commit = () => {
    if (draft === value) { if (invalid) settle(undefined); return; }
    const parsed = parse(draft);
    if (!parsed.ok) { settle(parsed.message); return; }
    settle(undefined);
    onCommit(parsed.value);
  };
  return (
    <ToolbarField label={label}>
      {(id) => (
        <TextInput id={id} size="xs" className="qwb-toolbar-input qwb-toolbar-input--text" value={draft} error={invalid}
          {...(placeholder === undefined ? {} : { placeholder })} {...(inputRef === undefined ? {} : { ref: inputRef })}
          {...(modified === true ? { 'data-modified': true, title: description ?? 'Set by you', classNames: { input: 'qwb-modified' } } : {})}
          onChange={(e) => setDraft(e.currentTarget.value)} onBlur={commit}
          onKeyDown={(e) => { if (e.key === 'Enter') commit(); if (e.key === 'Escape') { setDraft(value); if (invalid) settle(undefined); } }} />
      )}
    </ToolbarField>
  );
}
