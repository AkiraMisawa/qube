// The command line's editing keys (ADR-0006 note 11 of 2026-11-14): the Emacs keys that cost the line nothing a user
// relies on there, chosen per platform. A Mac's text field (and an iPad's, with a keyboard) already moves and deletes
// with Ctrl+A, E, F, B, D, H, K, T and Y, Ctrl+K feeding the system's own kill buffer that Ctrl+Y yanks from: those
// are left to it, and only kept from the shell's chords, since Mantine's `mod+K` takes Ctrl as well as ⌘. Windows and
// Linux have none of them, so the line does them itself, and leaves Ctrl+Y to its redo. Every deletion is the browser's
// own edit, so Ctrl+Z (⌘Z) takes it back. The keys are the line's alone: a grid, a dialog and the AI chat keep theirs.

/** The parts of a key press the line reads. */
export interface LineKeyPress {
  readonly key: string;
  readonly ctrlKey: boolean;
  readonly altKey: boolean;
  readonly metaKey: boolean;
  readonly shiftKey: boolean;
}

/** The line's text and its selection; `start` equals `end` where nothing is selected and the caret is there. */
export interface LineText {
  readonly value: string;
  readonly start: number;
  readonly end: number;
}

/** What a key of the line's editing does. */
export type LineEdit =
  /** The caret goes here, any selection collapsed. */
  | { readonly kind: 'move'; readonly to: number }
  /** What lies between the two is deleted, as the browser's own edit; nothing where they are equal. */
  | { readonly kind: 'delete'; readonly from: number; readonly to: number }
  /** The platform's text field does this key itself: it is left to it, and no chord of the shell acts on it. */
  | { readonly kind: 'native' };

/** The Ctrl keys a Mac's text field already edits with (ADR-0006 note 11). */
const APPLE_NATIVE = new Set(['a', 'e', 'f', 'b', 'd', 'h', 'k', 't', 'y']);

const graphemes = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
const words = new Intl.Segmenter(undefined, { granularity: 'word' });

/** Where the character after `at` ends: a character is what the reader sees as one, an emoji or a letter and its mark. */
function nextCharacter(value: string, at: number): number {
  const segment = at >= value.length ? undefined : graphemes.segment(value).containing(at);
  return segment === undefined ? value.length : segment.index + segment.segment.length;
}

/** Where the character before `at` starts. */
function previousCharacter(value: string, at: number): number {
  const segment = at <= 0 ? undefined : graphemes.segment(value).containing(at - 1);
  return segment === undefined ? 0 : segment.index;
}

/** Where the word after `at` ends, past what separates it (`CSA-A` is two words, `1mm` and `nocsa_MA1` one). */
function nextWordEnd(value: string, at: number): number {
  for (const s of words.segment(value)) {
    if (s.isWordLike === true && s.index + s.segment.length > at) return s.index + s.segment.length;
  }
  return value.length;
}

/** Where the word before `at` starts, past what separates it. */
function previousWordStart(value: string, at: number): number {
  let start = 0;
  for (const s of words.segment(value)) {
    if (s.index >= at) break;
    if (s.isWordLike === true) start = s.index;
  }
  return start;
}

/**
 * What a key does to the line's text, where it is one of the line's editing keys; `apple` says the keyboard is a
 * Mac's or an iPad's. A key with a selection acts as the arrows and Delete do: a move collapses it to the side it moves
 * to, and a deletion takes it with what the key reaches beyond it.
 */
export function lineEdit(press: LineKeyPress, text: LineText, apple: boolean): LineEdit | undefined {
  const key = press.key.toLowerCase();
  const { value, start, end } = text;
  const selected = start !== end;
  if (press.ctrlKey && !press.altKey && !press.metaKey && !press.shiftKey) {
    // Ctrl+U is the one a Mac's field lacks; what it deletes goes to no kill buffer, so Ctrl+Y does not yank it there.
    if (key === 'u') return { kind: 'delete', from: 0, to: end };
    if (apple) return APPLE_NATIVE.has(key) ? { kind: 'native' } : undefined;
    switch (key) {
      case 'a': return { kind: 'move', to: 0 };
      case 'e': return { kind: 'move', to: value.length };
      case 'f': return { kind: 'move', to: selected ? end : nextCharacter(value, end) };
      case 'b': return { kind: 'move', to: selected ? start : previousCharacter(value, start) };
      case 'd': return { kind: 'delete', from: start, to: selected ? end : nextCharacter(value, start) };
      case 'h': return { kind: 'delete', from: selected ? start : previousCharacter(value, start), to: end };
      case 'k': return { kind: 'delete', from: start, to: value.length };
      default: return undefined;
    }
  }
  // Alt is Meta on Windows and Linux. A Mac's Option types characters (ƒ, ∫, ∂), and moves by words with the arrows.
  if (press.altKey && !press.ctrlKey && !press.metaKey && !press.shiftKey && !apple) {
    switch (key) {
      case 'f': return { kind: 'move', to: nextWordEnd(value, end) };
      case 'b': return { kind: 'move', to: previousWordStart(value, start) };
      case 'd': return { kind: 'delete', from: start, to: nextWordEnd(value, end) };
      default: return undefined;
    }
  }
  return undefined;
}

/** Whether the keyboard is a Mac's or an iPad's, whose text fields edit with the Ctrl keys themselves. */
export function appleKeyboard(): boolean {
  return /^(Mac|iPhone|iPad|iPod)/.test(navigator.platform);
}

/** The line's text and selection as the field holds them. */
export function lineText(field: HTMLInputElement): LineText {
  const { value, selectionStart, selectionEnd } = field;
  if (selectionStart === null || selectionEnd === null) throw new Error('The command line is a text field, which always has a caret.');
  return { value, start: selectionStart, end: selectionEnd };
}

/**
 * Does an editing key's work on the line's field, the key taken from the browser and from the shell's chords. A
 * deletion is the browser's own edit, so the field's undo takes it back and the line hears it as typing; a read-only
 * field (a line that is running its command) deletes nothing.
 */
export function editLine(field: HTMLInputElement, edit: LineEdit, event: { preventDefault(): void; stopPropagation(): void }): void {
  event.stopPropagation();
  if (edit.kind === 'native') return;
  event.preventDefault();
  if (edit.kind === 'move') {
    field.setSelectionRange(edit.to, edit.to);
    return;
  }
  if (edit.from >= edit.to || field.readOnly) return;
  field.setSelectionRange(edit.from, edit.to);
  // `execCommand` is the one way a page edits a field that the field's own undo records; every target browser has it.
  if (!document.execCommand('delete')) throw new Error('The browser did not delete in the command line.');
}
