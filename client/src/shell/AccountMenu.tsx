import type { ReactNode } from 'react';
import { Avatar, Menu, Text, UnstyledButton, useMantineColorScheme, type MantineColorScheme } from '@mantine/core';
import { IconCheck, IconDeviceDesktop, IconLogout, IconMoon, IconSettings, IconSun } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { call } from '../platform/api';
import { openSettings } from './SettingsDialog';

// The header's account menu: who the page is for, their settings, the theme, and signing out. It takes the place of the
// header's theme toggle, so that the settings are found where a user looks for their account (owner, 2026-11-11).

/** Who the page is for (`GET /api/me`): until ME-ID sign-in (C30), the host's `Qube:DemoUser`. */
interface Me {
  readonly id: string;
  /** Their desk, where configuration names one (C32). */
  readonly desk?: string;
}

/** The size of an icon inside a menu item (docs/design-guidelines.md, "Icons"). */
const ICON = 16;

const SCHEMES: readonly { readonly value: MantineColorScheme; readonly label: string; readonly icon: ReactNode }[] = [
  { value: 'dark', label: 'Dark', icon: <IconMoon size={ICON} stroke={1.75} aria-hidden /> },
  { value: 'light', label: 'Light', icon: <IconSun size={ICON} stroke={1.75} aria-hidden /> },
  { value: 'auto', label: 'As the device', icon: <IconDeviceDesktop size={ICON} stroke={1.75} aria-hidden /> },
];

/**
 * The user's initials in a circle of Control Height; the pointer resting on it, a click or a tap opens the menu. The
 * theme is chosen here among the three the setting takes (`toggle theme` stays on the line). Signing out waits for
 * sign-in, and the menu says so rather than offer a button that does nothing.
 */
export function AccountMenu() {
  const me = useQuery({ queryKey: ['me'], queryFn: () => call<Me>('/api/me'), staleTime: Infinity });
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const who = me.data;
  return (
    <Menu trigger="click-hover" openDelay={100} closeDelay={300} position="bottom-end" width={260} withinPortal
      transitionProps={{ duration: 0 }}>
      <Menu.Target>
        <UnstyledButton aria-label={who === undefined ? 'Your account' : `Your account: ${who.id}`} className="qube-account">
          {/* No name until it is read: a figure, not someone else's initials. */}
          <Avatar size={30} radius="xl" color="initials" {...(who === undefined ? {} : { name: who.id })} />
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown>
        {me.isError ? (
          <Text size="xs" c="red" px="sm" py={6} role="status">Who you are could not be read: {me.error.message}</Text>
        ) : (
          <Menu.Label data-testid="account-who">{who === undefined ? 'Reading who you are…' : who.desk === undefined ? who.id : `${who.id} · ${who.desk} desk`}</Menu.Label>
        )}
        <Menu.Item leftSection={<IconSettings size={ICON} stroke={1.75} aria-hidden />} onClick={openSettings}>Settings</Menu.Item>
        <Menu.Divider />
        <Menu.Label>Theme</Menu.Label>
        {SCHEMES.map((scheme) => (
          // Mantine's items are `menuitem`s, which take no `aria-checked`: the theme in force is the set's current one.
          <Menu.Item key={scheme.value} aria-current={colorScheme === scheme.value} leftSection={scheme.icon}
            rightSection={colorScheme === scheme.value ? <IconCheck size={ICON} stroke={1.75} aria-hidden /> : null}
            onClick={() => setColorScheme(scheme.value)}>{scheme.label}</Menu.Item>
        ))}
        <Menu.Divider />
        <Menu.Item leftSection={<IconLogout size={ICON} stroke={1.75} aria-hidden />} disabled aria-describedby="qube-account-signout">Sign out</Menu.Item>
        <Text id="qube-account-signout" size="xs" c="dimmed" px="sm" pb={6}>
          Sign-in is not set up yet: every browser of this host is {who === undefined ? 'its one user' : who.id}.
        </Text>
      </Menu.Dropdown>
    </Menu>
  );
}
