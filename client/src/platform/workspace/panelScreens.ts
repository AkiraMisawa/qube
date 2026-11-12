// What a page shows, kept by its panel while the panel is open (ADR-0006 note 10, the owner's choice of 2026-11-11): its
// inner tab, what is open or folded, a grid's sort, filter and selection. Only the page of the panel shown is mounted
// (note 7), so a page hidden is a page unmounted, and one shown again is drawn anew: it starts from what its panel kept
// rather than from its defaults. What a page keeps is the screen's, never the user's work, which is the server's: a
// value kept here is no input to anything priced. It is kept in memory for this page load, as the panels are (ADR-0018
// §7 restores the panels after a reload, each page opened anew), and dropped as its panel closes, so a panel reopened
// comes back as a page opened anew (note 7).

/**
 * A value a page keeps by panel, declared once with its type, as a verb is: two declarations of one name are refused as
 * the second is made, so no component reads a value another component kept under that name as its own.
 */
export interface PanelKey<T> {
  readonly name: string;
  /** Never set: carries the type of the value, so a key read back is the type it was declared with. */
  readonly kept?: T;
}

const declared = new Set<string>();

/** Declares a value a page keeps by panel, by a name no other key takes (`xva.inputTab`). */
export function panelKey<T>(name: string): PanelKey<T> {
  if (declared.has(name)) throw new Error(`Two values a page keeps by panel are named ${name}.`);
  declared.add(name);
  return { name };
}

/** The values of every open panel's page, by panel and by key: one per workspace store, as the panels are. */
export class PanelScreens {
  readonly #byPanel = new Map<string, Map<string, unknown>>();

  /** Whether the panel's page kept a value under this key and instance. */
  has(panelId: string, key: PanelKey<unknown>, instance?: string): boolean {
    return this.#byPanel.get(panelId)?.has(slot(key, instance)) === true;
  }

  /** The value the panel's page kept under this key and instance; asked only where `has` says it kept one. */
  get<T>(panelId: string, key: PanelKey<T>, instance?: string): T {
    const values = this.#byPanel.get(panelId);
    const name = slot(key, instance);
    if (values === undefined || !values.has(name)) throw new Error(`Panel ${panelId} kept nothing under ${name}.`);
    // The one place a kept value meets its type: the key's name is declared once, with that type (`panelKey`).
    return values.get(name) as T;
  }

  set<T>(panelId: string, key: PanelKey<T>, value: T, instance?: string): void {
    let values = this.#byPanel.get(panelId);
    if (values === undefined) {
      values = new Map();
      this.#byPanel.set(panelId, values);
    }
    values.set(slot(key, instance), value);
  }

  /** Drops what the panels no longer open kept: a panel closed takes its page's state with it. */
  keepOnly(open: Iterable<string>): void {
    const ids = new Set(open);
    for (const panelId of this.#byPanel.keys()) if (!ids.has(panelId)) this.#byPanel.delete(panelId);
  }
}

const slot = (key: PanelKey<unknown>, instance: string | undefined): string => (instance === undefined ? key.name : `${key.name}/${instance}`);
