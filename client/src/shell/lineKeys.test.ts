import { editFor, letterWith, type LineKeyPress, type LineText } from './lineKeys';

/** A key pressed on a US layout, where the key's place is its letter. */
const place = (key: string) => (/^[a-z]$/i.test(key) ? `Key${key.toUpperCase()}` : 'Unidentified');
const ctrl = (key: string, code = place(key)): LineKeyPress => ({ key, code, ctrlKey: true, altKey: false, metaKey: false, shiftKey: false });
const alt = (key: string, code = place(key)): LineKeyPress => ({ key, code, ctrlKey: false, altKey: true, metaKey: false, shiftKey: false });
/** The line's text with the caret at `|`, or a selection between two `|`. */
const at = (marked: string): LineText => {
  const first = marked.indexOf('|');
  const second = marked.indexOf('|', first + 1);
  const value = marked.replaceAll('|', '');
  return { value, start: first, end: second === -1 ? first : second - 1 };
};
const pc = (press: LineKeyPress, text: string) => editFor(press, at(text), false);
const mac = (press: LineKeyPress, text: string) => editFor(press, at(text), true);

describe('the line\'s editing keys on Windows and Linux (ADR-0006 note 11)', () => {
  it('Ctrl+E goes to the end of the line, and Ctrl+A stays select all (the owner, 2026-11-14)', () => {
    expect(pc(ctrl('e'), 'set CSA-|A threshold')).toEqual({ kind: 'move', to: 19 });
    expect(pc(ctrl('a'), 'set CSA-|A threshold')).toBeUndefined();
  });

  it('Ctrl+F and Ctrl+B move a character, and from a selection to its side as the arrows do', () => {
    expect(pc(ctrl('f'), 'se|t')).toEqual({ kind: 'move', to: 3 });
    expect(pc(ctrl('b'), 'se|t')).toEqual({ kind: 'move', to: 1 });
    expect(pc(ctrl('f'), 'set|')).toEqual({ kind: 'move', to: 3 });
    expect(pc(ctrl('b'), '|set')).toEqual({ kind: 'move', to: 0 });
    expect(pc(ctrl('f'), 's|et| x')).toEqual({ kind: 'move', to: 3 });
    expect(pc(ctrl('b'), 's|et| x')).toEqual({ kind: 'move', to: 1 });
  });

  it('a character is what the reader sees as one: an emoji is never split', () => {
    // 👍🏽 is four UTF-16 units; a caret inside it would leave half an emoji on the line.
    expect(pc(ctrl('f'), 'a|👍🏽b')).toEqual({ kind: 'move', to: 5 });
    expect(pc(ctrl('b'), 'a👍🏽|b')).toEqual({ kind: 'move', to: 1 });
    expect(pc(ctrl('d'), 'a|👍🏽b')).toEqual({ kind: 'delete', from: 1, to: 5 });
    expect(pc(ctrl('h'), 'a👍🏽|b')).toEqual({ kind: 'delete', from: 1, to: 5 });
  });

  it('Ctrl+D and Ctrl+H delete the character after and before the caret, or the selection', () => {
    expect(pc(ctrl('d'), 'se|t')).toEqual({ kind: 'delete', from: 2, to: 3 });
    expect(pc(ctrl('h'), 'se|t')).toEqual({ kind: 'delete', from: 1, to: 2 });
    expect(pc(ctrl('d'), 'set|')).toEqual({ kind: 'delete', from: 3, to: 3 });
    expect(pc(ctrl('h'), '|set')).toEqual({ kind: 'delete', from: 0, to: 0 });
    expect(pc(ctrl('d'), 's|et| x')).toEqual({ kind: 'delete', from: 1, to: 3 });
    expect(pc(ctrl('h'), 's|et| x')).toEqual({ kind: 'delete', from: 1, to: 3 });
  });

  it('Ctrl+K deletes to the end of the line and Ctrl+U to its start, a selection with it', () => {
    expect(pc(ctrl('k'), 'set CSA-A| threshold')).toEqual({ kind: 'delete', from: 9, to: 19 });
    expect(pc(ctrl('u'), 'set CSA-A| threshold')).toEqual({ kind: 'delete', from: 0, to: 9 });
    expect(pc(ctrl('k'), 'set |CSA|-A')).toEqual({ kind: 'delete', from: 4, to: 9 });
    expect(pc(ctrl('u'), 'set |CSA|-A')).toEqual({ kind: 'delete', from: 0, to: 7 });
  });

  it('Alt+F and Alt+B move by words, and Alt+D deletes to the end of the next', () => {
    const line = 'set CSA-A threshold 1mm';
    expect(pc(alt('f'), `|${line}`)).toEqual({ kind: 'move', to: 3 });
    expect(pc(alt('f'), 'set| CSA-A threshold 1mm')).toEqual({ kind: 'move', to: 7 });
    // `-` separates words; `1mm` is one.
    expect(pc(alt('f'), 'set CSA|-A threshold 1mm')).toEqual({ kind: 'move', to: 9 });
    expect(pc(alt('b'), `${line}|`)).toEqual({ kind: 'move', to: 20 });
    expect(pc(alt('b'), 'set CSA-|A threshold 1mm')).toEqual({ kind: 'move', to: 4 });
    expect(pc(alt('b'), 'set CSA-A |threshold 1mm')).toEqual({ kind: 'move', to: 8 });
    expect(pc(alt('f'), 'set CSA-A threshold 1mm|')).toEqual({ kind: 'move', to: 23 });
    expect(pc(alt('b'), '  |set')).toEqual({ kind: 'move', to: 0 });
    expect(pc(alt('d'), 'set |CSA-A threshold')).toEqual({ kind: 'delete', from: 4, to: 7 });
    expect(pc(alt('d'), 'set CSA-A| threshold')).toEqual({ kind: 'delete', from: 9, to: 19 });
  });

  it('with a selection, Alt+F moves a word on from its end and Alt+B a word back from its start', () => {
    expect(pc(alt('f'), 'set |CSA|-A threshold')).toEqual({ kind: 'move', to: 9 });
    expect(pc(alt('b'), 'set CSA-|A| threshold')).toEqual({ kind: 'move', to: 4 });
    expect(pc(alt('d'), 'set |CSA|-A threshold')).toEqual({ kind: 'delete', from: 4, to: 9 });
  });

  it('an identifier is one word, and so is a number with its unit', () => {
    expect(pc(alt('f'), 'set |nocsa_MA1 curve')).toEqual({ kind: 'move', to: 13 });
    expect(pc(alt('b'), 'set 1.5mm|')).toEqual({ kind: 'move', to: 4 });
  });

  it('a key held with another modifier is not an editing key: Ctrl+Shift+E stays a chord, AltGr types', () => {
    expect(pc({ ...ctrl('E'), shiftKey: true }, 'a|b')).toBeUndefined();
    // AltGr is Ctrl+Alt on Windows.
    expect(pc({ ...ctrl('e'), altKey: true }, 'a|b')).toBeUndefined();
    expect(pc({ ...ctrl('a'), metaKey: true }, 'a|b')).toBeUndefined();
    expect(pc({ ...alt('f'), shiftKey: true }, 'a|b')).toBeUndefined();
  });

  it('every other key is left as it was: Ctrl+Y redoes, Ctrl+N and Ctrl+P choose, Ctrl+W and Ctrl+T are the browser\'s', () => {
    for (const key of ['y', 'w', 't', 'g', 'r', 's', 'n', 'p', 'z', 'c', 'v', 'x']) expect(pc(ctrl(key), 'a|b')).toBeUndefined();
    expect(pc(alt('v'), 'a|b')).toBeUndefined();
  });

  it('a key is read whatever the Caps Lock says', () => {
    expect(pc(ctrl('E'), 'a|b')).toEqual({ kind: 'move', to: 2 });
  });

  it('a layout of another script is read by the key\'s place, as the browser\'s shortcuts are; a Latin layout by its letter', () => {
    // Russian: Ctrl with the key at E says `у`, and is Ctrl+E; the key at A says `ф`, and stays select all.
    expect(pc(ctrl('у', 'KeyE'), 'a|b')).toEqual({ kind: 'move', to: 2 });
    expect(pc(ctrl('ф', 'KeyA'), 'a|b')).toBeUndefined();
    expect(pc(alt('и', 'KeyB'), 'ab|')).toEqual({ kind: 'move', to: 0 });
    // Dvorak: the key at F types `u`, so Ctrl with it is Ctrl+U.
    expect(pc(ctrl('u', 'KeyF'), 'ab|')).toEqual({ kind: 'delete', from: 0, to: 2 });
  });
});

describe('the letter a key is read as (ADR-0006 note 11)', () => {
  it('is read with one modifier alone, so the list\'s Ctrl+N and Ctrl+P and the editing keys read the same', () => {
    expect(letterWith('ctrl', ctrl('n'))).toBe('n');
    expect(letterWith('ctrl', { ...ctrl('n'), shiftKey: true })).toBeUndefined();
    expect(letterWith('ctrl', { ...ctrl('n'), altKey: true })).toBeUndefined();
    expect(letterWith('ctrl', alt('n'))).toBeUndefined();
    expect(letterWith('alt', alt('n'))).toBe('n');
    expect(letterWith('ctrl', ctrl('ArrowDown', 'ArrowDown'))).toBeUndefined();
  });
});

describe('the line\'s editing keys on a Mac (ADR-0006 note 11)', () => {
  it('the Ctrl keys a Mac\'s text field edits with are left to it, so Ctrl+K feeds its kill buffer and Ctrl+Y yanks', () => {
    for (const key of ['a', 'e', 'f', 'b', 'd', 'h', 'k', 't', 'y']) expect(mac(ctrl(key), 'se|t')).toEqual({ kind: 'native' });
    // Ctrl+N and Ctrl+P choose in the list, on a Mac as elsewhere.
    expect(mac(ctrl('n'), 'se|t')).toBeUndefined();
    expect(mac(ctrl('p'), 'se|t')).toBeUndefined();
  });

  it('Ctrl+U, which a Mac\'s field lacks, deletes to the start of the line there too', () => {
    expect(mac(ctrl('u'), 'set CSA-A| threshold')).toEqual({ kind: 'delete', from: 0, to: 9 });
  });

  it('Option types characters on a Mac, so Option+F, B and D are not the line\'s', () => {
    for (const [key, code] of [['ƒ', 'KeyF'], ['∫', 'KeyB'], ['∂', 'KeyD']] as const) expect(mac(alt(key, code), 'a|b')).toBeUndefined();
  });

  it('⌘ with a letter is never an editing key', () => {
    expect(mac({ key: 'k', code: 'KeyK', ctrlKey: false, altKey: false, metaKey: true, shiftKey: false }, 'a|b')).toBeUndefined();
  });
});
