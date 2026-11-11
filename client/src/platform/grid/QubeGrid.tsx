import { useContext } from 'react';
import { MantineExGrid, type MantineExGridProps } from '@ex-grid/mantine';
import { GridUndoContext } from './gridUndo';

/**
 * Every grid in the workbench: ExGrid drawn by its Mantine Wrapper (ADR-0001, docs/design-guidelines.md "Grids").
 * The Wrapper draws the grid's menus, filter panel and editor with Mantine's components, maps the theme's colours onto
 * ExGrid's Visual Tokens in both schemes, and draws the cells in the font whose widths ExGrid judges `####` by
 * (ex-grid ADR-0390 §9). The paper's look is decided here once: a square, bordered panel, of the class `qube-grid` that
 * the workbench's own paint in theme.css is scoped to. On a page that holds a workspace, ExGrid's undo and redo keys run
 * the page's two commands (ADR-0016 §11): the grid keeps no history of its own.
 */
export function QubeGrid<Row>({ className, ...props }: MantineExGridProps<Row>) {
  const history = useContext(GridUndoContext);
  const keys = history === undefined ? {} : { onUndo: history.undo, onRedo: history.redo };
  return <MantineExGrid<Row> withBorder radius={0} {...keys} {...props} className={className === undefined ? 'qube-grid' : `qube-grid ${className}`} />;
}
