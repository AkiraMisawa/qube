import { useSyncExternalStore } from 'react';
import type { SerializedDockview } from 'dockview-react';
import { AREA_MIN, AREAS_FROM, type AreaOf, type Line, type Size } from './layout';

export type { Line } from './layout';

// The port the panels' verbs read and change the arrangement through (ADR-0018 §2): dockview holds where each panel
// is, and the verbs reach it only here, so a verb is tested over a fake and dockview could be replaced without changing
// one. `PanelAreas` gives the areas' port while areas are shown; the strip gives one that has no areas.

export const SIDES = ['right', 'left', 'down', 'up'] as const;
/** Where `split panel` puts the new area, beside the panel's own. */
export type Side = (typeof SIDES)[number];

export const FLOAT_PLACES = ['centre', 'left', 'right', 'top', 'bottom'] as const;
/** Over which part of the panels' space `float panel` puts a floating area: a half, or the middle. */
export type FloatPlace = (typeof FLOAT_PLACES)[number];

/** A floating area's place in the panels' space, in CSS pixels from its top left. */
export interface Box {
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
}

/** An area as it stands now: its panels, the one it shows, whether it floats, and its size. */
export interface Area extends AreaOf, Size {}

export interface Arrangement {
  /**
   * Why the panels are one strip and not areas, said where a verb that arranges is refused; undefined while areas are
   * shown (ADR-0018 §5).
   */
  readonly strip: string | undefined;
  /** The areas in reading order (ADR-0018 §1). None in the strip. */
  areas(): readonly Area[];
  /** The panels' space: the window less the AI pane. */
  room(): Size;
  lineOf(areaId: string): Line | undefined;
  /** The area that has the whole of the panels' space, if one has. */
  maximized(): string | undefined;
  /** Moves the panel into a new area on that side of its own. */
  split(panelId: string, side: Side): void;
  /** Moves the panel into that area, after its panels. */
  sendTo(panelId: string, areaId: string): void;
  /** Moves the panel into a new floating area at that place; a floating panel's area is placed there. */
  float(panelId: string, box: Box): void;
  /**
   * Moves the panel into an area of its own in a new window of the browser (ADR-0018 §9). The window opens a moment
   * later; one the browser does not open leaves the panel where it stood, and the areas say so.
   */
  popOut(panelId: string): void;
  /**
   * Puts a floating panel, or one in another window, back among the main window's areas, and answers where, as the user
   * is told.
   */
  dock(panelId: string): string;
  /** Gives the area this many pixels of its row or column. */
  size(areaId: string, pixels: number): void;
  maximize(areaId: string): void;
  unmaximize(): void;
  /** Puts every panel in this area, in this order. */
  gather(areaId: string, order: readonly string[]): void;
  /** Moves the panel to this place among its area's panels. */
  moveWithin(panelId: string, index: number): void;
  /**
   * Draws this arrangement in place of the one drawn (`arrange`, ADR-0018 §8); it names every panel open. Where its
   * floating panels came from is not part of it: the caller keeps that after the drawing (`keptArrangement`).
   */
  replace(layout: SerializedDockview): void;
}

/** The areas' ids, for the page's life: an area keeps its id across the strip, so a panel closed in it goes back to it. */
let areaIds = 0;

/** A new area's id, which no area drawn in this page has had. */
export function nextAreaId(): string {
  areaIds += 1;
  return `area-${areaIds}`;
}

/** Says which ids the areas drawn have, as an arrangement kept across a reload is drawn: no new area takes one. */
export function sawAreas(ids: readonly string[]): void {
  for (const id of ids) {
    const n = /^area-(\d+)$/.exec(id)?.[1];
    if (n !== undefined) areaIds = Math.max(areaIds, Number(n));
  }
}

/** The area holding this panel, if areas are shown and one does. */
export function areaHolding(areas: Arrangement, panelId: string): Area | undefined {
  return areas.areas().find((a) => a.panels.includes(panelId));
}

/** Why the panels are one strip at this room: under 768px, or under what the arrangement needs. */
export function stripReason(room: Size, needed: Size | undefined): string {
  if (room.width < AREAS_FROM)
    return `The panels are one strip below ${AREAS_FROM}px; the arrangement comes back with more room.`;
  const short = needed === undefined ? undefined : needed.width > room.width ? `${needed.width}px wide` : needed.height > room.height ? `${needed.height}px high` : undefined;
  return short === undefined
    ? 'The panels are one strip; the arrangement comes back with more room.'
    : `The arrangement needs ${short}, so the panels are one strip until there is that room.`;
}

/** The strip's port: there are no areas, and every change of the arrangement is refused before it reaches here. */
export function stripArrangement(room: Size, why: string): Arrangement {
  const none = (): never => { throw new Error(`The strip has no areas to change: ${why}`); };
  return {
    strip: why,
    areas: () => [],
    room: () => room,
    lineOf: () => undefined,
    maximized: () => undefined,
    split: none,
    sendTo: none,
    float: none,
    popOut: none,
    dock: none,
    size: none,
    maximize: none,
    unmaximize: none,
    gather: none,
    moveWithin: none,
    replace: none,
  };
}

/** The place `float panel <where>` gives a floating area in this room: a half of it, or the middle three fifths. */
export function floatBox(place: FloatPlace, room: Size): Box {
  const half = { width: Math.max(AREA_MIN.width, Math.floor(room.width / 2)), height: Math.max(AREA_MIN.height, Math.floor(room.height / 2)) };
  switch (place) {
    case 'left': return { left: 0, top: 0, width: half.width, height: room.height };
    case 'right': return { left: room.width - half.width, top: 0, width: half.width, height: room.height };
    case 'top': return { left: 0, top: 0, width: room.width, height: half.height };
    case 'bottom': return { left: 0, top: room.height - half.height, width: room.width, height: half.height };
    case 'centre': {
      const width = Math.max(AREA_MIN.width, Math.floor(room.width * 0.6));
      const height = Math.max(AREA_MIN.height, Math.floor(room.height * 0.6));
      return { left: Math.floor((room.width - width) / 2), top: Math.floor((room.height - height) / 2), width, height };
    }
  }
}

/** The port the line and the menus read: the one the drawn panels registered, the strip's until one has. */
let current: Arrangement = stripArrangement({ width: 0, height: 0 }, 'The panels are not drawn yet.');

export function currentArrangement(): Arrangement {
  return current;
}

/** Registers the port of the panels drawn now; answers what puts back the one before it. */
export function registerArrangement(port: Arrangement): () => void {
  const before = current;
  current = port;
  arrangementChanged();
  return () => {
    if (current !== port) return;
    current = before;
    arrangementChanged();
  };
}

// What reads the arrangement to say what a verb can do now (the line's list) is told when it changes: a split, a drop,
// a float, or the panels drawn as a strip in place of areas.
let version = 0;
const listeners = new Set<() => void>();

/** Says that the arrangement has changed, to what reads it. */
export function arrangementChanged(): void {
  version += 1;
  listeners.forEach((listener) => listener());
}

/** A number that changes whenever the arrangement does, for what lists the verbs to be listed again. */
export function useArrangementVersion(): number {
  return useSyncExternalStore((listener) => {
    listeners.add(listener);
    return () => { listeners.delete(listener); };
  }, () => version);
}
