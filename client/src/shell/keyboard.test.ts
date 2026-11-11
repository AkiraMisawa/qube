import { holdKeyboard, keepKeyboard, releaseKeyboard, rememberKeyboard, returnKeyboard } from './keyboard';

// ADR-0016 §9, notes 22 and 24 of 2026-11-11: where the keyboard goes around the command line, and who has it while a
// chain of commands runs from the line.

describe('the keyboard while a chain of commands runs from the line (ADR-0016 §9, note 24 of 2026-11-11)', () => {
  const made: HTMLElement[] = [];
  const button = (parent: HTMLElement = document.body) => { const b = document.createElement('button'); parent.append(b); made.push(b); return b; };
  const palette = () => { const p = document.createElement('div'); p.className = 'qube-palette'; document.body.append(p); made.push(p); return p; };
  afterEach(() => { releaseKeyboard(); keepKeyboard(); for (const element of made.splice(0)) element.remove(); });

  it('a focus that lands outside the line is where the keyboard goes when the line closes, and the line has it back at once', () => {
    const [before, place, line] = [button(), button(), palette()];
    const [input, option] = [button(line), button(line)];
    before.focus();
    rememberKeyboard();
    input.focus();
    const arrived = vi.fn();

    holdKeyboard(input, { taken: arrived, lost: () => undefined });
    place.focus();

    expect(document.activeElement).toBe(input);
    expect(arrived).toHaveBeenCalledTimes(1);
    // A focus inside the line is the line's own: nothing is remembered for it.
    option.focus();
    expect(document.activeElement).toBe(option);
    expect(arrived).toHaveBeenCalledTimes(1);
    // Closed, the keyboard is where the chained command put it, not where it was when the line opened.
    returnKeyboard();
    expect(document.activeElement).toBe(place);
  });

  it('once the line closes it holds nothing: a later focus stays where it lands', () => {
    const [before, place, line] = [button(), button(), palette()];
    const input = button(line);
    before.focus();
    rememberKeyboard();
    input.focus();
    const arrived = vi.fn();
    holdKeyboard(input, { taken: arrived, lost: () => undefined });

    returnKeyboard();
    place.focus();

    expect(document.activeElement).toBe(place);
    expect(arrived).not.toHaveBeenCalled();
  });

  it('a chained command that put the keyboard somewhere itself leaves the line nothing to give back from before, and its place is kept whether it took the keyboard at once or later', () => {
    const [before, place, line] = [button(), button(), palette()];
    const input = button(line);
    const opened = () => { before.focus(); rememberKeyboard(); input.focus(); holdKeyboard(input, { taken: () => undefined, lost: () => undefined }); };

    // A place its page draws later: `go` says the keyboard is the place's before the place has it. Closed meanwhile,
    // the line gives nothing back, and the place takes the keyboard when it is drawn.
    opened();
    keepKeyboard();
    returnKeyboard();
    expect(document.activeElement).toBe(input);

    // A place that takes it at once: the line has it back, and its closing gives it to the place.
    opened();
    place.focus();
    keepKeyboard();
    returnKeyboard();
    expect(document.activeElement).toBe(place);

    // The next chained command's own arrival is counted apart: one that put the keyboard nowhere yet keeps no old place.
    opened();
    place.focus();
    holdKeyboard(input, { taken: () => undefined, lost: () => undefined });
    keepKeyboard();
    returnKeyboard();
    expect(document.activeElement).toBe(input);
  });

  it('a dialog that takes the keyboard keeps it: the line is told it lost the keyboard, holds it no longer and has no place to give it back to', () => {
    const [before, line, dialog] = [button(), palette(), document.createElement('div')];
    dialog.setAttribute('role', 'dialog');
    document.body.append(dialog);
    made.push(dialog);
    const [input, field, other] = [button(line), button(dialog), button(dialog)];
    before.focus();
    rememberKeyboard();
    input.focus();
    const [taken, lost] = [vi.fn(), vi.fn()];
    holdKeyboard(input, { taken, lost });

    field.focus();

    expect(document.activeElement).toBe(field);
    expect(lost).toHaveBeenCalledTimes(1);
    expect(taken).not.toHaveBeenCalled();
    // The dialog's own keys move within it untouched, and the line's closing moves the keyboard nowhere.
    other.focus();
    returnKeyboard();
    expect(document.activeElement).toBe(other);
    expect(lost).toHaveBeenCalledTimes(1);
  });
});
