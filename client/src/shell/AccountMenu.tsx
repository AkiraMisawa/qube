import { useEffect, useRef, useState, type PointerEvent } from 'react';
import { Avatar, Menu, Text, UnstyledButton, useMantineColorScheme, type MantineColorScheme } from '@mantine/core';
import { IconLogout, IconSettings, IconUserExclamation } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import { call } from '../platform/api';
import { ICON, STROKE } from './icons';
import { openSettings } from './SettingsDialog';
import { UnavailableMenuItem } from './UnavailableMenuItem';

// The header's account menu: who the page is for, their settings, the theme, and signing out. It takes the place of the
// header's theme button, so that the settings are found where a user looks for their account (owner, 2026-11-11).

/** Who the page is for (`GET /api/me`): until ME-ID sign-in (C30), the host's `Qube:DemoUser`. */
interface Me {
  readonly id: string;
  /** Their desk, where configuration names one (C32). */
  readonly desk?: string;
}

/** Room for the sign-out reason on two lines: geometry the theme has no name for. */
const MENU_WIDTH = 260;
/** How long the pointer rests on the avatar before the menu opens, so that a pointer passing over it opens nothing. */
const OPEN_DELAY = 100;
/** How long the pointer may be off the avatar and the menu, crossing the gap between them, before the menu closes. */
const CLOSE_DELAY = 300;

const SCHEMES: readonly { readonly value: MantineColorScheme; readonly label: string }[] = [
  { value: 'dark', label: 'Dark' },
  { value: 'light', label: 'Light' },
  { value: 'auto', label: 'As the device' },
];

function isScheme(value: string): value is MantineColorScheme {
  return SCHEMES.some((scheme) => scheme.value === value);
}

/** What the header shows of the user while they are read, once read, and when they cannot be: never a stand-in name. */
function shown(me: { readonly data: Me | undefined; readonly error: Error | null }) {
  if (me.error !== null)
    return {
      label: 'Your account: who you are could not be read',
      avatar: <Avatar size={30} radius="xl" color="red"><IconUserExclamation size={ICON} stroke={STROKE} aria-hidden /></Avatar>,
      who: <Text size="xs" c="red" px="sm" py="xs">Who you are could not be read: {me.error.message}</Text>,
    };
  if (me.data === undefined)
    return { label: 'Your account (being read)', avatar: <Avatar size={30} radius="xl" />, who: <Menu.Label>Reading who you are…</Menu.Label> };
  const { id, desk } = me.data;
  return {
    label: `Your account: ${id}`,
    avatar: <Avatar size={30} radius="xl" color="initials" name={id} />,
    who: <Menu.Label data-testid="account-who">{desk === undefined ? id : `${id} · ${desk} desk`}</Menu.Label>,
  };
}

/**
 * Closed; open because the mouse rests on it, which leaves the keyboard where it was and closes when the mouse goes; or
 * held open by a click, a tap or a key, with the keyboard in it, until a choice, Escape, a press outside or another click.
 */
type Opened = 'closed' | 'resting' | 'held';

/**
 * Opens the menu on the mouse resting on its target, and holds it on a click. Mantine's own `click-hover` is not used: a
 * delay it starts outlives the close that an item's click makes, so an item chosen within the open delay of the pointer
 * entering the menu opened it again over what the item opened (a tap on Settings did so every time on a phone, where the
 * tap's compatibility mouse events enter the menu). Every change here clears the delay before it, and only a mouse
 * opens by resting, so the outcome of a press never depends on how long the pointer was there.
 */
function useRestingMenu() {
  const [opened, setOpened] = useState<Opened>('closed');
  const delay = useRef<number | undefined>(undefined);
  /** Set by a click on the target, whose own handler decides, for Mantine's toggle that follows it in the same click. */
  const clicked = useRef(false);
  useEffect(() => () => window.clearTimeout(delay.current), []);
  const become = (next: Opened) => {
    window.clearTimeout(delay.current);
    setOpened(next);
  };
  const after = (ms: number, next: Opened) => {
    window.clearTimeout(delay.current);
    delay.current = window.setTimeout(() => setOpened(next), ms);
  };
  const mouse = (event: PointerEvent) => event.pointerType === 'mouse';
  /** The mouse comes onto the avatar or the menu: a closed menu opens after the delay, a resting one stays. */
  const enter = (event: PointerEvent) => {
    if (!mouse(event)) return;
    if (opened === 'closed') after(OPEN_DELAY, 'resting');
    else if (opened === 'resting') window.clearTimeout(delay.current);
  };
  /** The mouse leaves them: a menu still waiting to open does not, and a resting one closes after the delay. */
  const leave = (event: PointerEvent) => {
    if (!mouse(event)) return;
    if (opened === 'closed') window.clearTimeout(delay.current);
    else if (opened === 'resting') after(CLOSE_DELAY, 'closed');
  };
  return {
    menu: {
      opened: opened !== 'closed',
      trapFocus: opened === 'held',
      onChange: (next: boolean) => {
        if (clicked.current) {
          clicked.current = false;
          return;
        }
        become(next ? 'held' : 'closed');
      },
    },
    target: {
      onClick: () => {
        clicked.current = true;
        become(opened === 'held' ? 'closed' : 'held');
      },
      onPointerEnter: enter,
      onPointerLeave: leave,
    },
    dropdown: { onPointerEnter: enter, onPointerLeave: leave },
  };
}

/**
 * The user's initials in a circle of Control Height. The mouse resting on it opens the menu and leaves the keyboard
 * where it was, so it takes nothing from a line or a grid being typed in; a click, a tap or Enter opens it with the
 * keyboard in it, and a second click closes it. It has no Tooltip (design guidelines §5's SHOULD): resting the pointer
 * on it opens the menu, which names it. Signing out waits for sign-in, and the menu says so on an item the keyboard and
 * a screen reader reach.
 */
export function AccountMenu() {
  const me = useQuery({ queryKey: ['me'], queryFn: () => call<Me>('/api/me'), staleTime: Infinity });
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const resting = useRestingMenu();
  const { label, avatar, who } = shown(me);
  return (
    <Menu {...resting.menu} position="bottom-end" width={MENU_WIDTH} withinPortal transitionProps={{ duration: 0 }}>
      <Menu.Target>
        <UnstyledButton aria-label={label} className="qube-account" {...resting.target}>{avatar}</UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown {...resting.dropdown}>
        {who}
        <Menu.Item leftSection={<IconSettings size={ICON} stroke={STROKE} aria-hidden />} rightSection={<Text size="xs" c="dimmed">settings</Text>}
          onClick={openSettings}>Settings</Menu.Item>
        <Menu.Divider />
        <Menu.Label>Theme</Menu.Label>
        <Menu.RadioGroup value={colorScheme} onChange={(value) => {
          if (!isScheme(value)) throw new Error(`${value} is not a theme.`);
          setColorScheme(value);
        }}>
          {SCHEMES.map((scheme) => <Menu.RadioItem key={scheme.value} value={scheme.value}>{scheme.label}</Menu.RadioItem>)}
        </Menu.RadioGroup>
        <Menu.Divider />
        <UnavailableMenuItem leftSection={<IconLogout size={ICON} stroke={STROKE} aria-hidden />}
          why="Sign-in is not set up yet, so every browser of this host is the same user.">
          Sign out
        </UnavailableMenuItem>
      </Menu.Dropdown>
    </Menu>
  );
}
