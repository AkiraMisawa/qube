import { useId, type ComponentPropsWithoutRef, type ReactNode } from 'react';
import { Menu, Text } from '@mantine/core';

/**
 * A menu item that cannot act now, with why under it, where every pointer, key and touch screen reads it. Not Mantine's
 * `disabled`: that takes the item out of the arrows' reach, where WAI-ARIA's menu pattern keeps a disabled item, and
 * Mantine's item writes `aria-disabled` only with it, so the root is rendered here to say it. Choosing it does nothing
 * and leaves the menu open: the reason is already in sight.
 */
export function UnavailableMenuItem({ why, leftSection, rightSection, children }: {
  readonly why: string;
  readonly leftSection?: ReactNode;
  readonly rightSection?: ReactNode;
  readonly children: ReactNode;
}) {
  const reason = useId();
  return (
    <>
      <Menu.Item leftSection={leftSection} rightSection={rightSection} closeMenuOnClick={false} c="dimmed" aria-describedby={reason}
        renderRoot={(props: ComponentPropsWithoutRef<'button'>) => <button type="button" {...props} aria-disabled />}>
        {children}
      </Menu.Item>
      <Text id={reason} size="xs" c="dimmed" px="sm" pb="xs">{why}</Text>
    </>
  );
}
