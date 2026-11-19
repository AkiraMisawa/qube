import { Fragment } from 'react';
import { Group, Kbd, Text } from '@mantine/core';

/** A key hint: the keys, each a chord of keys held together, and what they do; `or` where any one of them does it. */
export interface KeyHint {
  readonly keys: readonly (readonly string[])[];
  readonly or?: boolean;
  readonly does: string;
}

/**
 * The keys of a part of the trace, said under it where a keyboard is likely (design guidelines §8: from `sm` up). A key
 * hint answers to the device, which has a keyboard or not, and not to the panel's width as the page's layout does (§9),
 * so it is the one thing on a page shown by the window's width: a narrow area on a desktop shows it, a phone does not.
 */
export function KeyHints({ label, testId, hints }: { readonly label: string; readonly testId: string; readonly hints: readonly KeyHint[] }) {
  return (
    <Group gap="md" wrap="wrap" visibleFrom="sm" className="qube-trace-keys" data-testid={testId} aria-label={label}>
      {hints.map((hint) => (
        <Text key={hint.does} size="xs">
          {hint.keys.map((chord, i) => (
            <Fragment key={chord.join('+')}>
              {i > 0 && (hint.or === true ? ' or ' : ' ')}
              {chord.map((key, j) => <Fragment key={key}>{j > 0 && '+'}<Kbd size="xs">{key}</Kbd></Fragment>)}
            </Fragment>
          ))}
          {' '}{hint.does}
        </Text>
      ))}
    </Group>
  );
}
