import { useState } from 'react';
import { Badge, Button, Group, Modal, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import type { Notify } from '../platform/notices';
import { closeSettings, resetSetting, setSetting, useSettings, useSettingsOpen, type SettingChanged, type SettingDescribed } from '../platform/settings/settings';

// The settings dialog (ADR-0017 §4): each of the user's settings, its value, and whether they set it or the registered
// value applies. A value a line types is changed here; any setting is reset. It writes through the same commands as the
// line and the AI, so what the dialog says of a change is what the command answered.

/** What a setting holds, in a few words, where its page writes it: a grid's widths, the command ranking. */
export function summary(setting: SettingDescribed): string {
  if (setting.typed) return setting.value;
  const value = JSON.parse(setting.value) as unknown;
  const count = value !== null && typeof value === 'object' ? Object.keys(value).length : 0;
  if (setting.key.startsWith('grid.widths.')) return count === 1 ? '1 column dragged' : `${count} columns dragged`;
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
      <Select size="xs" w={140} data={[...setting.choices]} value={setting.value === 'none' ? null : setting.value} allowDeselect={false}
        aria-label={setting.title} comboboxProps={{ withinPortal: true }}
        onChange={(value) => { if (value !== null && value !== setting.value) change(() => setSetting(setting.key, value)); }} />
    );
  const commit = () => {
    if (typed !== undefined && typed.trim() !== '' && typed !== setting.value) change(() => setSetting(setting.key, typed));
    setTyped(undefined);
  };
  return (
    <TextInput size="xs" w={140} aria-label={setting.title} placeholder={setting.takes} value={typed ?? (setting.value === 'none' ? '' : setting.value)}
      onChange={(e) => setTyped(e.currentTarget.value)} onBlur={commit} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />
  );
}

export function SettingsDialog({ notify }: { readonly notify: Notify }) {
  const opened = useSettingsOpen();
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
        <Button size="xs" variant="subtle" disabled={!setting.set} aria-label={`Reset ${setting.name}`}
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
        <Table verticalSpacing={4} aria-label="Preferences">
          <Table.Thead><Table.Tr><Table.Th>Preference</Table.Th><Table.Th>Value</Table.Th><Table.Th>From</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>{rows('preference')}</Table.Tbody>
        </Table>
        {defaults.length === 0 ? null : (
          <Table verticalSpacing={4} aria-label="Defaults">
            <Table.Thead><Table.Tr><Table.Th>Default for what you make</Table.Th><Table.Th>Value</Table.Th><Table.Th>From</Table.Th><Table.Th /></Table.Tr></Table.Thead>
            <Table.Tbody>{defaults}</Table.Tbody>
          </Table>
        )}
        <Group justify="flex-end"><Button size="sm" variant="default" onClick={closeSettings}>Close</Button></Group>
      </Stack>
    </Modal>
  );
}
