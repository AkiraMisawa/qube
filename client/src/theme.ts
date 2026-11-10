import { createTheme, type CSSVariablesResolver, type MantineColorsTuple } from '@mantine/core';
import { mantineDefaultFont } from '@ex-grid/mantine';

// Qube's own look: dense, dark-first, with a signal amber for focus and primary actions and
// tabular figures everywhere numbers line up. Mantine supplies the components; this supplies the system.
// docs/design-guidelines.md says how to use it, and ADR-0009 why: one Control Height (Mantine `xs`, 30px) for
// every control in a toolbar, spacing on a 4px grid, the type ramp below, and colour only from the theme.

const signal: MantineColorsTuple = [
  '#fff6e0', '#ffeccb', '#ffd79a', '#ffc164', '#ffae36', '#ffa218', '#ff9c03', '#e38800', '#ca7800', '#af6700',
];

const ink: MantineColorsTuple = [
  '#eef1f6', '#d9dee7', '#b3bccc', '#8b98b0', '#6a7a98', '#566789', '#4b5e83', '#3c4d6f', '#2a3550', '#141a28',
];

export const theme = createTheme({
  primaryColor: 'signal',
  primaryShade: { light: 7, dark: 5 },
  colors: { signal, dark: ['#c9d1e0', '#a6b0c3', '#7d889e', '#5a6478', '#3a4256', '#2a3142', '#1d2331', '#151a25', '#10141d', '#0b0e15'], ink },
  // The grid's cells are drawn in Mantine's default stack, the face ExGrid has widths for (ex-grid ADR-0390 §9). The
  // rest of the workbench takes the same stack, so a figure reads in one face in a grid and beside it. Inter was named
  // here before ADR-0009 and never loaded, so each platform drew its own fallback.
  fontFamily: mantineDefaultFont.family,
  fontFamilyMonospace: '"JetBrains Mono", "SFMono-Regular", Consolas, monospace',
  defaultRadius: 'xs',
  fontSizes: { xs: '11px', sm: '12px', md: '13px', lg: '15px', xl: '18px' },
  spacing: { xs: '4px', sm: '8px', md: '12px', lg: '16px', xl: '24px' },
  headings: { fontWeight: '600' },
  // The ring shows on :focus-visible: always for the keyboard, never as a leftover of a mouse click or a tap
  // (ADR-0009). ExGrid draws its own Focus inside the grid.
  focusRing: 'auto',
  cursorType: 'pointer',
  // A filled control's label is dark or light by its fill (docs/design-guidelines.md §7, 4.5:1): white on the dark
  // theme's amber Price read at 2.1:1.
  autoContrast: true,
});

/**
 * Text in a colour reads at 4.5:1 or more on its surface in either scheme (docs/design-guidelines.md §7). Mantine's
 * light scheme writes coloured text in the palette's ninth shade, or its filled shade, on white or on the colour's
 * lightest tint, and dimmed text in grey 6: measured on the XVA page (2026-11-08) a yellow `SIMULATED` read at
 * 2.7:1, the pressed measures at 3.8:1 and every field label at 3.3:1. In the light scheme a colour's text is that
 * ninth shade darkened by a third, which keeps its hue, and dimmed is grey 7. The dark scheme already holds.
 */
export const cssVariablesResolver: CSSVariablesResolver = (resolved) => ({
  variables: {},
  light: {
    '--mantine-color-dimmed': 'var(--mantine-color-gray-7)',
    // A link is text in the primary colour, so it takes that colour's ink too: a trace link in the Metrics read at 2.7:1
    // in amber on white (measured 2026-11-08, the spot IM page's browser test).
    '--mantine-color-anchor': `var(--mantine-color-${resolved.primaryColor}-text)`,
    ...Object.fromEntries(Object.keys(resolved.colors).flatMap((name) => {
      const ink = `color-mix(in srgb, var(--mantine-color-${name}-9) 70%, black)`;
      return [[`--mantine-color-${name}-text`, ink], [`--mantine-color-${name}-light-color`, ink]];
    })),
  },
  dark: {},
});
