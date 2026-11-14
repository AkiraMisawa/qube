// The command line's editing keys (ADR-0006 note 11 of 2026-11-14): the Emacs keys that cost the line nothing a user
// relies on there, chosen per platform with the owner. A Mac's text field already moves and deletes with Ctrl+A, E,
// F, B, D, H, K, T and Y, Ctrl+K feeding the system's own kill buffer that Ctrl+Y yanks from: those are left to it, and
// only kept from the shell's chords, since Mantine's `mod+K` takes Ctrl as well as ⌘. Windows and Linux have none of
// them, so the line does them itself, except Ctrl+A and Ctrl+Y, which stay select all and redo there (the owner,
// 2026-11-14). Every deletion is the browser's own edit, so Ctrl+Z (⌘Z) takes it back. The keys are the line's alone:
// a grid, a dialog and the AI chat keep theirs.

/** The parts of a key press the line reads. */
export interface LineKeyPress {
  readonly key: string;
  readonly code: string;
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

/**
 * The letter of a key pressed with one modifier alone, lower case, or nothing. The letter is the layout's where it is
 * a Latin one (Dvorak's), and the key's place where the layout types another script, as the browser's own shortcuts
 * read it: Ctrl+A on a Russian layout says `ф`, and is Ctrl+A.
 */
export function letterWith(modifier: 'ctrl' | 'alt', press: LineKeyPress): string | undefined {
  const held = { ctrl: press.ctrlKey, alt: press.altKey };
  if (!held[modifier] || held[modifier === 'ctrl' ? 'alt' : 'ctrl'] || press.metaKey || press.shiftKey) return undefined;
  if (/^[a-z]$/i.test(press.key)) return press.key.toLowerCase();
  return /^Key[A-Z]$/.test(press.code) ? press.code.slice(3).toLowerCase() : undefined;
}

/** The Ctrl keys a Mac's text field already edits with (ADR-0006 note 11). */
const MAC_NATIVE = new Set(['a', 'e', 'f', 'b', 'd', 'h', 'k', 't', 'y']);

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
 * The edit a key makes to the line's text, where it is one of the line's editing keys; `mac` says the keyboard is a
 * Mac's. With a selection, Ctrl+F and Ctrl+B collapse it to the side they move to, as the arrows do; Alt+F moves a word
 * on from its end and Alt+B a word back from its start; and a deletion takes the selection with what it reaches beyond.
 */
export function editFor(press: LineKeyPress, text: LineText, mac: boolean): LineEdit | undefined {
  const { value, start, end } = text;
  const selected = start !== end;
  const ctrl = letterWith('ctrl', press);
  if (ctrl !== undefined) {
    // Ctrl+U is the one a Mac's field lacks; what it deletes goes to no kill buffer, so Ctrl+Y does not yank it there.
    if (ctrl === 'u') return { kind: 'delete', from: 0, to: end };
    if (mac) return MAC_NATIVE.has(ctrl) ? { kind: 'native' } : undefined;
    // Ctrl+A selects all here, as in every field of Windows and Linux; Home goes to the start.
    switch (ctrl) {
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
  const alt = mac ? undefined : letterWith('alt', press);
  switch (alt) {
    case 'f': return { kind: 'move', to: nextWordEnd(value, end) };
    case 'b': return { kind: 'move', to: previousWordStart(value, start) };
    case 'd': return { kind: 'delete', from: start, to: nextWordEnd(value, end) };
    default: return undefined;
  }
}

/**
 * Whether the keyboard is a Mac's, whose text fields edit with the Ctrl keys themselves. Read from `navigator.platform`,
 * which every browser on a Mac says `MacIntel` in, Apple silicon included; an iPad's Safari says the same by default,
 * so an iPad is taken as a Mac.
 */
export function macKeyboard(): boolean {
  return navigator.platform.startsWith('Mac');
}

/** The line's text and selection as the field holds them. */
export function textOf(field: HTMLInputElement): LineText {
  const { value, selectionStart, selectionEnd } = field;
  if (selectionStart === null || selectionEnd === null) throw new Error('The command line is a text field, which always has a caret.');
  return { value, start: selectionStart, end: selectionEnd };
}

/**
 * Makes an editing key's edit in the line's field, the key taken from the browser and from the shell's chords. A
 * deletion is the browser's own edit, so the field's undo takes it back and the line hears it as typing; a read-only
 * field (a line that is running its command) deletes nothing.
 */
export function applyEdit(field: HTMLInputElement, edit: LineEdit, event: { preventDefault(): void; stopPropagation(): void }): void {
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
