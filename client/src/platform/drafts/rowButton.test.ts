import type { LineView } from '../contracts';
import { editColumn, openRow } from './rowButton';

// ADR-0016 note 57 of 2026-11-12: a grid's row has one button, edit, and it runs what `open <row>` typed would.

const door = (ended?: { sentence: string }) => {
  const entered: { line: string; view: LineView | undefined; heard: boolean }[] = [];
  return {
    entered,
    enter: (line: string, _shown?: unknown, view?: LineView, heard?: (said: string) => void) => { entered.push({ line, view, heard: heard !== undefined }); return Promise.resolve(ended); },
  };
};

describe('a row\'s edit button (ADR-0016 note 57)', () => {
  it('is one action, Edit, in a column with no header', () => {
    const column = editColumn<{ id: string }>();
    expect(column.name).toBe('Actions');
    expect(column.header).toBe('');
    expect(column.actions?.map((a) => `${a.name}:${a.label}`)).toEqual(['edit:Edit']);
  });

  it('runs open on the row through the door, in the view of its table, and raises no notice of the dialog it opened', async () => {
    const through = door();
    const notify = vi.fn();
    await openRow(through, 't.stations', 'S1', notify);
    // The dialog itself is what opening it says: the sentence is heard here and no notice is raised.
    expect(through.entered).toEqual([{ line: 'open S1', view: { table: 't.stations' }, heard: true }]);
    expect(notify).not.toHaveBeenCalled();
  });

  it('says the server\'s refusal as a notice', async () => {
    const notify = vi.fn();
    await openRow(door({ sentence: 'stations has no row S9.' }), 't.stations', 'S9', notify);
    expect(notify).toHaveBeenCalledWith('refused', 'open S9: stations has no row S9.');
  });

  it('refuses a key a line cannot say as one word, and asks the server nothing', async () => {
    const through = door();
    const notify = vi.fn();
    await openRow(through, 't.stations', 'S 1', notify);
    expect(through.entered).toEqual([]);
    expect(notify).toHaveBeenCalledWith('refused', 'The dialog of S 1 was not opened: a line names a row by one word, and its key has a space.');
  });
});
