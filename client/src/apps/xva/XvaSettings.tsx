import { useEffect, useRef, useState, type Ref } from 'react';
import { Stack, Switch, Table, TextInput } from '@mantine/core';
import type { ModuleSettings, RegisteredMeasure, SettingView } from './contracts';
import type { PushEdit, SetText } from './editing';
import { registered } from './runRows';

// The Settings tab (ADR-0010 §8's note of 2026-11-09): a section per selected module, each setting a selected measure
// needs, with its default and the measures it serves. A setting is an input of the workspace, frozen by Price and read
// by the provider; the server checks its form and bounds, so the tab sends what was typed, as written.

/** The entity a module's settings are edits of. */
export const settingsEntity = (module: string) => `settings:${module}`;

/** What a default is, in words: its value as written, or what empty means. */
const defaultWords = (s: SettingView) => (s.default == null ? `empty${s.empty == null ? '' : ` (${s.empty})`}` : s.defaultText);

export function XvaSettings({ settings, measures, push, setText, focusRef }: {
  readonly settings: readonly ModuleSettings[];
  /** The registered measures, for the titles of those a setting serves. */
  readonly measures: readonly RegisteredMeasure[];
  /** A flag's value, as a switch sets it. */
  readonly push: PushEdit;
  /** A value as written (`12%`, `20d`, `empty`), for the server to read and check; the page says why it is refused. */
  readonly setText: SetText;
  /** The first setting, where `open settings` puts the keyboard. */
  readonly focusRef: Ref<HTMLInputElement>;
}) {
  const title = (id: string) => registered(measures, id).title;
  return (
    <Stack gap="xs" className="qube-xva-settings">
      {settings.map((m, mi) => (
        <div key={m.module} className="qube-xva-scroll">
          <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="sm" fz="sm" aria-label={`${m.title} settings`}>
            <Table.Thead>
              <Table.Tr><Table.Th>{m.title}</Table.Th><Table.Th>Value</Table.Th><Table.Th>Default</Table.Th><Table.Th>For</Table.Th></Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {m.settings.map((s, si) => (
                <Table.Tr key={s.id}>
                  <Table.Th scope="row" fw={400}>{s.title}</Table.Th>
                  <Table.Td>
                    <SettingInput setting={s} module={m.module} push={push} setText={setText}
                      {...(mi === 0 && si === 0 ? { inputRef: focusRef } : {})} />
                  </Table.Td>
                  <Table.Td>{defaultWords(s)}</Table.Td>
                  <Table.Td>{s.measures.map(title).join(', ')}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </div>
      ))}
    </Stack>
  );
}

/**
 * One setting's input: a switch for a flag, sent as it moves; otherwise a text field sent on Enter or when left, once it
 * changed, as written (an empty one as `empty`). One the server refuses stays as typed and marked, with the server's
 * reason said by the page, until it is typed again or Escape puts the value back. One that differs from its default is
 * marked and says the default.
 */
function SettingInput({ setting, module, push, setText, inputRef }: {
  readonly setting: SettingView;
  readonly module: string;
  readonly push: PushEdit;
  readonly setText: SetText;
  readonly inputRef?: Ref<HTMLInputElement>;
}) {
  const entity = settingsEntity(module);
  const value = setting.value == null ? '' : setting.text;
  const [draft, setDraft] = useState(value);
  const [invalid, setInvalid] = useState(false);
  // What was last sent of this typing: Enter, then the blur that follows it, sends it once, and a refused value is not
  // sent again until it is typed again (spine 6).
  const sent = useRef<string | undefined>(undefined);
  useEffect(() => { setDraft(value); setInvalid(false); sent.current = undefined; }, [value]);
  const marked = setting.changed
    ? { 'data-modified': true, title: `Default ${defaultWords(setting)} · now ${setting.value == null ? 'empty' : setting.text}`, classNames: { input: 'qube-modified' } } : {};

  if (setting.form === 'flag') {
    return (
      <Switch size="xs" aria-label={setting.title} checked={setting.value === true} {...(inputRef === undefined ? {} : { ref: inputRef })}
        {...(setting.changed ? { 'data-modified': true, title: `Default ${defaultWords(setting)}` } : {})}
        onChange={(e) => push(entity, setting.id, e.currentTarget.checked, setting.title)} label={setting.value === true ? 'On' : 'Off'} />
    );
  }
  const commit = async () => {
    if (draft === value) { setInvalid(false); return; }
    if (draft === sent.current) return;
    sent.current = draft;
    setInvalid((await setText(entity, setting.id, draft.trim() === '' ? 'empty' : draft.trim(), setting.title)) !== undefined);
  };
  return (
    <TextInput size="xs" className="qube-toolbar-input qube-toolbar-input--text" aria-label={setting.title} value={draft} error={invalid} {...marked}
      {...(setting.empty == null ? {} : { placeholder: `empty: ${setting.empty}` })} {...(inputRef === undefined ? {} : { ref: inputRef })}
      onChange={(e) => { setDraft(e.currentTarget.value); sent.current = undefined; }} onBlur={() => void commit()}
      onKeyDown={(e) => { if (e.key === 'Enter') void commit(); if (e.key === 'Escape') { setDraft(value); setInvalid(false); sent.current = undefined; } }} />
  );
}
