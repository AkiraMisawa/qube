import { describe, expect, it } from 'vitest';
import type { PackageView } from './contracts';
import { nextPackage, packageOf } from './packages';

const written: readonly PackageView[] = [
  { name: 'P1', adds: ['DRAFT-1', 'DRAFT-2'], takesOut: ['E00003'] },
  { name: 'p3', adds: [], takesOut: ['E00007'] },
];

describe('the packages the Package cells write, as Make package… reads them (ADR-0016 note 90)', () => {
  it('offers the first of P1, P2, … that no package is, whatever the case of its letters', () => {
    expect(nextPackage([])).toBe('P1');
    expect(nextPackage(written)).toBe('P2');
    expect(nextPackage([...written, { name: 'P2', adds: ['DRAFT-3'], takesOut: [] }])).toBe('P4');
  });

  it('says the package a trade is in, new or existing, and none for a trade in no package', () => {
    expect(packageOf(written, 'DRAFT-2')).toBe('P1');
    expect(packageOf(written, 'E00007')).toBe('p3');
    expect(packageOf(written, 'DRAFT-3')).toBeUndefined();
  });
});
