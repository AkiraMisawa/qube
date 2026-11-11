/**
 * A Run's context or its input as its panel says it: each value after its name, and a value that holds values by
 * each of theirs (`defaults.currency JPY`), so that none is written as `[object Object]`.
 */
export function factsSaid(facts: Readonly<Record<string, unknown>>, under = ''): readonly string[] {
  return Object.entries(facts).flatMap(([name, value]) => {
    if (value === null || typeof value !== 'object') return [`${under}${name} ${String(value)}`];
    if (Array.isArray(value) || Object.keys(value).length === 0) return [`${under}${name} ${JSON.stringify(value)}`];
    return factsSaid(value as Readonly<Record<string, unknown>>, `${under}${name}.`);
  });
}
