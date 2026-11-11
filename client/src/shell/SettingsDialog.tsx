import { useState } from 'react';
import { createStore } from 'zustand/vanilla';
import { useStore } from 'zustand';
import { Badge, Button, Group, Modal, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import type { Notify } from '../platform/notices';
import { WIDTHS } from '../platform/grid/columnWidths';
import { registerAiEffect } from '../ai/effects';
import { NO_VALUE, heardChange, resetSetting, say, setSetting, useSettings, type SettingChanged, type SettingDescribed } from '../platform/settings/settings';

// The settings dialog (ADR-0017 §4): each of the user's settings, its value, and whether they set it or the registered
// value applies. A value a line types is changed here; any setting is reset. It writes through the same commands as the
// line and the AI, so what the dialog says of a change is what the command answered.

// A setting the AI set or reset for the user applies at once, as one changed on the line or here (ADR-0017 §4, ADR-0007
// §7): the theme turns, a grid's widths move. It opens nothing.
for (const capability of ['settings.set', 'settings.reset'])
  registerAiEffect(capability, (outputs) => {
    heardChange(outputs.answer as SettingChanged).catch((error: unknown) => say(`Your settings could not be read again: ${error instanceof Error ? error.message : String(error)}`));
    return undefined;
  });

const dialog = createStore<{ readonly open: boolean }>()(() => ({ open: false }));

/** Shows the settings dialog (`settings` on the line). The settings are the user's, not a workspace's, and so is the dialog. */
export function openSettings(): void {
  dialog.setState({ open: true });
}

function closeSettings(): void {
  dialog.setState({ open: false });
}

// The width of a value's input: room for the longest value a line types here (a model's id, `claude-sonnet-4.5`) and
// its menu's chevron, the same in every row so that the column reads as one.
const VALUE_WIDTH = 180;

/**
 * What a setting holds, in a few words, where its page writes it: a grid's widths, the command ranking. One with no
 * value says so; one that holds what the server checks to be an object is counted by its entries.
 */
export function summary(setting: SettingDescribed): string {
  if (setting.typed || setting.value === NO_VALUE) return setting.value;
  const value = JSON.parse(setting.value) as unknown;
  if (value === null || typeof value !== 'object') return setting.value;
  const count = Object.keys(value).length;
  if (setting.key.startsWith(WIDTHS)) return count === 1 ? '1 column dragged' : `${count} columns dragged`;
  return count === 1 ? '1 entry' : `${count} entries`;
}

function Source({ setting }: { readonly setting: SettingDescribed }) {
  if (setting.unreadable !== undefined)
    return <Text size="xs" c="red">You set {setting.stored}, which is no longer {setting.unreadable}.</Text>;
  return setting.set ? <Badge size="sm" variant="light">set by you</Badge> : <Text size="xs" c="dimmed">registered</Text>;
}

function Value({ setting, change }: { readonly setting: SettingDescribed; readonly change: (act: () => Promise<SettingChanged>) => void }) {
  const [typed, setTyped] = useState<string | undefined>(undefined);
  if (!setting.typed) return <Text size="sm">{summary(setting)}</Text>;
  if (setting.choices !== undefined)
    return (
      <Select size="sm" w={VALUE_WIDTH} data={[...setting.choices]} value={setting.value === NO_VALUE ? null : setting.value} allowDeselect={false}
        aria-label={setting.title} comboboxProps={{ withinPortal: true }}
        onChange={(value) => { if (value !== null && value !== setting.value) change(() => setSetting(setting.key, value)); }} />
    );
  const commit = () => {
    if (typed !== undefined && typed.trim() !== '' && typed !== setting.value) change(() => setSetting(setting.key, typed));
    setTyped(undefined);
  };
  return (
    <TextInput size="sm" w={VALUE_WIDTH} aria-label={setting.title} placeholder={setting.takes} value={typed ?? (setting.value === NO_VALUE ? '' : setting.value)}
      onChange={(e) => setTyped(e.currentTarget.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
  );
}

export function SettingsDialog({ notify }: { readonly notify: Notify }) {
  const opened = useStore(dialog, (s) => s.open);
  const { settings } = useSettings();
  const all = Object.values(settings);
  const change = (act: () => Promise<SettingChanged>) => {
    act().then((changed) => notify('done', changed.said), (error: unknown) => notify('refused', error instanceof Error ? error.message : String(error)));
  };
  const rows = (kind: SettingDescribed['kind']) => all.filter((s) => s.kind === kind).map((setting) => (
    <Table.Tr key={setting.key}>
      <Table.Td><Text size="sm">{setting.title}</Text>{setting.typed ? null : <Text size="xs" c="dimmed">{setting.name}</Text>}</Table.Td>
      <Table.Td><Value setting={setting} change={change} /></Table.Td>
      <Table.Td><Source setting={setting} /></Table.Td>
      <Table.Td>
        <Button size="sm" variant="subtle" disabled={!setting.set} aria-label={`Reset ${setting.name}`}
          onClick={() => change(() => resetSetting(setting.key))}>Reset</Button>
      </Table.Td>
    </Table.Tr>
  ));
  const defaults = rows('default');
  return (
    <Modal opened={opened} onClose={closeSettings} title="Settings" size="lg" trapFocus returnFocus
      transitionProps={{ duration: 0 }} closeButtonProps={{ 'aria-label': 'Close settings' }}>
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          Yours alone, on every device you sign in on. A preference changes no figure. A default is copied into a workspace
          when you make one, and changing it later leaves the workspaces you made before as they are.
        </Text>
        {/* One table, so the preferences' columns and the defaults' line up; each kind has its own heading row. */}
        <Table verticalSpacing="xs" aria-label="Your settings">
          <Table.Thead><Table.Tr><Table.Th>Preference</Table.Th><Table.Th>Value</Table.Th><Table.Th>From</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>{rows('preference')}</Table.Tbody>
          {defaults.length === 0 ? null : (
            <Table.Tbody>
              <Table.Tr><Table.Th pt="md">Default for what you make</Table.Th><Table.Th pt="md">Value</Table.Th><Table.Th pt="md">From</Table.Th><Table.Th /></Table.Tr>
              {defaults}
            </Table.Tbody>
          )}
        </Table>
        <Group justify="flex-end"><Button size="sm" variant="default" onClick={closeSettings}>Close</Button></Group>
      </Stack>
    </Modal>
  );
}
