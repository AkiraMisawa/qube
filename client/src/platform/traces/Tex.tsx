import { useMemo, type MouseEvent } from 'react';
import katex from 'katex';
import 'katex/dist/katex.min.css';

// Mathematics set by KaTeX (ADR-0011 §6), pinned in package.json, its fonts bundled and served by the application.
// Strict: every non-LaTeX construct is an error, except the one extension the renderer writes itself, `\htmlData`,
// which is also the only command trusted, and only with the attributes the renderer writes. What KaTeX cannot set is
// said in words, never drawn as a plausible formula.

const ATTRIBUTES = new Set(['data-operand', 'data-state', 'data-row']);

/** TeX as HTML, or why it could not be set. A command not trusted is refused by name rather than drawn as its text. */
export function typeset(tex: string, display = false): { readonly html: string } | { readonly error: string } {
  let untrusted: string | undefined;
  try {
    const html = katex.renderToString(tex, {
      displayMode: display,
      throwOnError: true,
      output: 'htmlAndMathml',
      strict: (code) => (code === 'htmlExtension' ? 'ignore' : 'error'),
      trust: (context) => {
        const trusted = context.command === '\\htmlData' && Object.keys(context.attributes).every((a) => ATTRIBUTES.has(a));
        if (!trusted) untrusted ??= context.command;
        return trusted;
      },
    });
    return untrusted === undefined ? { html } : { error: `${untrusted} is not a command the trace view trusts.` };
  } catch (error) {
    return { error: error instanceof Error ? error.message : String(error) };
  }
}

/** One formula. A press on an operand the renderer wrapped reports its index. */
export function Tex({ tex, display = false, onOperand }: {
  readonly tex: string;
  readonly display?: boolean;
  readonly onOperand?: (index: number) => void;
}) {
  const set = useMemo(() => typeset(tex, display), [tex, display]);
  if ('error' in set) {
    return <span className="qwb-tex-error" role="alert">The formula could not be typeset: {set.error}</span>;
  }
  const press = (event: MouseEvent<HTMLElement>) => {
    const target = (event.target as Element).closest('[data-operand]');
    const index = target?.getAttribute('data-operand');
    if (index === null || index === undefined || onOperand === undefined) return;
    onOperand(Number(index));
  };
  return (
    // The operands are also offered as buttons beside the formula, so the keyboard and a screen reader reach them;
    // KaTeX's MathML is what a screen reader reads of the formula itself.
    <span className={display ? 'qwb-tex qwb-tex-display' : 'qwb-tex'} onClick={press} dangerouslySetInnerHTML={{ __html: set.html }} />
  );
}
