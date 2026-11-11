import { CommandRefused, assertDistinct, completionsOf, drawn, drawnSections, execute, finish, listed, placeholder, rankCommands, readLine, take, type Command, type CommandDoor, type ListSection } from './commands';

const command = (words: string, extra: Partial<Command> = {}): Command => ({ id: words, words, title: words, group: 'G', run: vi.fn(), ...extra });

const irs = command('new irs', {
  args: [
    { name: 'currency', required: true, complete: () => [{ value: 'JPY' }, { value: 'USD' }] },
    { name: 'index', required: true, complete: (g) => (g.currency === 'USD' ? [{ value: 'SOFR' }] : [{ value: 'TONA' }]) },
    { name: 'end', required: true, check: (v) => (/^\d+y$/.test(v) ? undefined : 'a tenor such as 10y') },
    { name: 'rate', required: false },
  ],
});

describe('finding a command by typing (ADR-0006)', () => {
  it('puts the words typed in full above longer words that start with them', () => {
    expect(rankCommands('price', [command('price all'), command('price')]).map((c) => c.words)).toEqual(['price', 'price all']);
  });

  it('finds a command from the start of each of its words, in order', () => {
    expect(rankCommands('op pr', [command('price'), command('open pricing db')]).map((c) => c.words)).toEqual(['open pricing db']);
    expect(rankCommands('zz', [command('open pricing db')])).toEqual([]);
  });

  it('ranks what the user runs most first among equal matches, so a few letters are enough', () => {
    const ranked = rankCommands('p', [command('previous panel'), command('price')], (id) => (id === 'price' ? 3 : 0));
    expect(ranked[0]!.words).toBe('price');
  });

  it('does not run an unavailable command and says why', () => {
    const c = command('price', { unavailable: 'Open an XVA or Rates workspace to price it.' });
    expect(execute(c)).toBe('Open an XVA or Rates workspace to price it.');
    expect(c.run).not.toHaveBeenCalled();
  });

  it('ADR-0006 note 1 (2026-11-11): a command that cannot read its arguments says why, where it used to close in silence', () => {
    const set = command('set', { run: () => { throw new CommandRefused('set needs a row, a column and a value.'); } });
    expect(execute(set, { cell: 'E00006 a' })).toBe('set needs a row, a column and a value.');
  });

  it('ADR-0006 note 1 (2026-11-11): a defect in a command is thrown on, never shown as a reason', () => {
    const broken = command('price', { run: () => { throw new Error('The XVA workspace in panel p1 registered no commands.'); } });
    expect(() => execute(broken)).toThrow('The XVA workspace in panel p1 registered no commands.');
  });

  it('refuses two commands with the same words, or on one chord', () => {
    expect(() => assertDistinct([command('price', { id: 'a' }), command('price', { id: 'b' })])).toThrow(/name both a and b/);
    expect(() => assertDistinct([command('a', { shortcut: 'mod+shift+G' }), command('b', { shortcut: 'Mod+Shift+g' })])).toThrow(/bound to both a and b/);
  });
});

describe('what the empty line and ? list (ADR-0006 note 3 of 2026-11-11)', () => {
  const onPage = { target: 'XVA ABC/LE-01', page: true } as const;
  const commands = [
    command('new xva', { group: 'XVA' }),
    command('price', { group: 'Pricing', ...onPage }),
    command('set', { group: 'XVA', ...onPage }),
    command('live start', { group: 'Rates', unavailable: 'Open a Rates workspace first.' }),
    command('new rates', { group: 'Rates' }),
    command('run station summary', { group: 'Workflows' }),
    command('close panel', { group: 'Panels', target: 'XVA ABC/LE-01' }),
  ];
  const shape = (sections: readonly ListSection[]) => sections.map((s) => [s.heading, s.commands.map((c) => c.words)]);

  it('puts the commands of the page in the active panel first, under its title, then each group of the rest that can run', () => {
    expect(shape(listed('?', commands))).toEqual([
      ['XVA ABC/LE-01', ['price', 'set']],
      ['XVA', ['new xva']],
      ['Rates', ['new rates']],
      ['Workflows', ['run station summary']],
      ['Panels', ['close panel']],
    ]);
  });

  it('lists what cannot run now last on the empty line, with the rest as ? has it, and not at all for ?', () => {
    expect(shape(listed('', commands))).toEqual([...shape(listed('?', commands)), ['Not available now', ['live start']]]);
  });

  it('puts what the user runs most first under a heading (ADR-0006 §6)', () => {
    expect(shape(listed('', commands, (id) => (id === 'set' ? 2 : 0)))[0]).toEqual(['XVA ABC/LE-01', ['set', 'price']]);
  });

  it('ranks a typed query as one list with no heading', () => {
    expect(shape(listed('pr', commands))).toEqual([[undefined, ['price']]]);
  });

  it('orders what cannot run now by use too', () => {
    const cannot = [command('live start', { group: 'Rates', unavailable: 'Open a Rates workspace first.' }),
      command('capture', { group: 'Rates', unavailable: 'Open a Rates workspace first.' })];
    expect(shape(listed('', cannot, (id) => (id === 'capture' ? 1 : 0)))).toEqual([['Not available now', ['capture', 'live start']]]);
  });

  it('names the first commands of the list in the placeholder, so it differs by page', () => {
    expect(placeholder(listed('', commands))).toBe('Type what you want to do: price, set, new xva… (? lists them)');
    const nothingOpen = [command('new xva', { group: 'XVA' }), command('price', { group: 'Pricing', unavailable: 'Open a workspace.' }),
      command('new rates', { group: 'Rates' }), command('open pricing db', { group: 'History' }), command('run station summary', { group: 'Workflows' })];
    expect(placeholder(listed('', nothingOpen))).toBe('Type what you want to do: new xva, new rates, open pricing db… (? lists them)');
  });

  it('never cuts a list in silence: what is not drawn is counted', () => {
    expect(drawn(['a', 'b', 'c'], 2)).toEqual({ shown: ['a', 'b'], more: 1 });
    expect(drawn(['a', 'b'], 2)).toEqual({ shown: ['a', 'b'], more: 0 });
  });

  it('cuts a list in sections where the cap falls, and draws no heading with nothing under it', () => {
    const [a, b, c, d] = ['a', 'b', 'c', 'd'].map((w) => command(w));
    const sections = [{ heading: 'One', commands: [a!, b!] }, { heading: 'Two', commands: [c!, d!] }];
    const words = (cut: ReturnType<typeof drawnSections>) => cut.sections.map((s) => [s.heading, s.start, s.commands.map((x) => x.words)]);
    expect(words(drawnSections(sections, 3))).toEqual([['One', 0, ['a', 'b']], ['Two', 2, ['c']]]);
    expect(drawnSections(sections, 3).more).toBe(1);
    expect(words(drawnSections(sections, 2))).toEqual([['One', 0, ['a', 'b']]]);
    expect(drawnSections(sections, 2).more).toBe(2);
    expect(drawnSections(sections, 200).more).toBe(0);
  });
});

describe('completing arguments one slot at a time (ADR-0006 §4)', () => {
  it('chooses a command once its words are followed by a space, and offers the first slot', () => {
    expect(readLine('new irs', [irs]).kind).toBe('choosing');
    const reading = readLine('new irs ', [irs]);
    expect(reading.kind === 'arguments' && reading.slot?.name).toBe('currency');
    expect(completionsOf(reading).map((v) => v.value)).toEqual(['JPY', 'USD']);
  });

  it('ADR-0016 note 36: a line that is exactly a command\'s words is that command being chosen, though a shorter command\'s words begin it', () => {
    // `new` is the server's to read, and `new rates` opens a dialog: the words typed whole are the dialog's command.
    const made = command('new', { served: { arguments: [{ name: 'product', required: true }], door: {} as CommandDoor } });
    const rates = command('new rates');

    expect(readLine('new rates', [made, rates])).toEqual({ kind: 'choosing', query: 'new rates' });
    expect(rankCommands('new rates', [made, rates])).toEqual([rates]);
    // Before its words are whole the line is the shorter command's, and with a space after them it is the longer's.
    expect(readLine('new rat', [made, rates])).toMatchObject({ kind: 'arguments', command: { id: 'new' } });
    expect(readLine('new rates ', [made, rates])).toMatchObject({ kind: 'arguments', command: { id: 'new rates' } });
    expect(readLine('new irs 5y', [made, rates])).toMatchObject({ kind: 'arguments', command: { id: 'new' } });
  });

  it('offers values that follow from the arguments before them', () => {
    expect(completionsOf(readLine('new irs USD ', [irs])).map((v) => v.value)).toEqual(['SOFR']);
  });

  it('takes a completion into the line and moves to the next slot', () => {
    const text = 'new irs U';
    const reading = readLine(text, [irs]);
    expect(take(text, reading, { value: 'USD' })).toBe('new irs USD ');
  });

  it('names every missing argument and runs nothing', () => {
    const reading = readLine('new irs JPY', [irs]);
    expect(reading.kind).toBe('arguments');
    if (reading.kind !== 'arguments') return;
    expect(finish(reading)).toEqual({ problems: ['Missing: index, end.'] });
  });

  it('reads a line typed in one go, optional values by name, and checks each value', () => {
    const ok = readLine('new irs JPY TONA 10y rate=0.5', [irs]);
    if (ok.kind !== 'arguments') throw new Error('not chosen');
    expect(finish(ok)).toEqual({ args: { currency: 'JPY', index: 'TONA', end: '10y', rate: '0.5' } });

    const bad = readLine('new irs JPY TONA ten spread=1 extra', [irs]);
    if (bad.kind !== 'arguments') throw new Error('not chosen');
    expect(finish(bad)).toEqual({ problems: ['new irs has no option spread.', 'new irs takes no more values: extra.', 'end: a tenor such as 10y'] });
  });

  it('ADR-0006: a completion taken into a rest of several words replaces the rest whole', () => {
    const include = command('scenario include', { args: [{ name: 'scenario', required: true, rest: true, complete: () => [{ value: 'jpy_ois +50bp · usdjpy -10%' }] }] });
    const text = 'scenario include jpy_ois +5';
    const reading = readLine(text, [include]);
    expect(completionsOf(reading)).toEqual([{ value: 'jpy_ois +50bp · usdjpy -10%' }]);
    const taken = take(text, reading, { value: 'jpy_ois +50bp · usdjpy -10%' });
    expect(taken).toBe('scenario include jpy_ois +50bp · usdjpy -10% ');
    const read = readLine(taken, [include]);
    if (read.kind !== 'arguments') throw new Error('not chosen');
    expect(finish(read)).toEqual({ args: { scenario: 'jpy_ois +50bp · usdjpy -10% ' } });
  });

  it('keeps the rest of the line for a request to the AI', () => {
    const ai = command('ai', { args: [{ name: 'request', required: true, rest: true }] });
    const reading = readLine('ai make a 10y swap  and tick it', [ai]);
    if (reading.kind !== 'arguments') throw new Error('not chosen');
    expect(finish(reading)).toEqual({ args: { request: 'make a 10y swap  and tick it' } });
  });

  it('reads a rest of words whole and completes only the word typed now (ADR-0008 §3)', () => {
    const create = command('new swap', { args: [{ name: 'terms', required: true, rest: true, words: true, complete: () => [{ value: 'pay' }, { value: '10y' }] }] });
    const reading = readLine('new swap 10y p', [create]);
    if (reading.kind !== 'arguments') throw new Error('not chosen');
    expect(reading.typed).toBe('p');
    expect(completionsOf(reading).map((v) => v.value)).toEqual(['pay']);
    expect(take('new swap 10y p', reading, { value: 'pay' })).toBe('new swap 10y pay ');
    expect(finish(reading)).toEqual({ args: { terms: '10y p' } });
  });
});
