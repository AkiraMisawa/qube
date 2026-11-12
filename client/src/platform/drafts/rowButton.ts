import { actionColumn, type GridColumn } from '@ex-grid/core';
import type { CommandDoor } from '../commands/commands';
import type { Notify } from '../notices';

// ADR-0016 §10, note 57 of 2026-11-12: a grid's row has one button, edit, which opens the row's dialog. It runs what
// `open <row>` typed on the line would, through the page's door, so the dialog a button opens and the one a line opens
// are the same.

/** The column of a grid that holds its rows' buttons. */
export const ROW_ACTIONS = 'Actions';
/** The one button of a row: its dialog. */
export const EDIT_ROW = 'edit';

/** The column a grid gains for its rows' edit button: no header, and as wide as the button. */
export const editColumn = <Row>(): GridColumn<Row> => actionColumn<Row>(ROW_ACTIONS, [{ name: EDIT_ROW, label: 'Edit' }], { header: '', width: 64 });

/**
 * Opens the dialog of a row of a grid: `open <key>` read in the view of the grid's table. A refusal is a notice; the
 * dialog opened says itself, so no notice is raised of it. A line names a row by one word: a key that is not one is
 * refused here, never sent to be read as something else.
 */
export async function openRow(door: Pick<CommandDoor, 'enter'>, table: string, key: string, notify: Notify): Promise<void> {
  if (/\s/.test(key)) {
    notify('refused', `The dialog of ${key} was not opened: a line names a row by one word, and its key has a space.`);
    return;
  }
  const line = `open ${key}`;
  const ended = await door.enter(line, undefined, { table }, () => undefined);
  if (ended !== undefined && 'sentence' in ended) notify('refused', `${line}: ${ended.sentence}`);
}
