import { createContext } from 'react';

/**
 * What a page that holds a workspace gives its grids for ExGrid's undo and redo keys (ADR-0016 §11): the same two
 * commands the command line runs. A grid outside such a page gets none, and the keys stay the browser's.
 */
export interface GridUndo {
  readonly undo: () => void;
  readonly redo: () => void;
}

export const GridUndoContext = createContext<GridUndo | undefined>(undefined);
