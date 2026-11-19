import { describe, expect, it } from 'vitest';
import type { PackageView } from './contracts';
import { packagesOf } from './packages';

const written: readonly PackageView[] = [
  { name: 'P1', adds: ['DRAFT-1', 'DRAFT-2'], takesOut: ['E00003'] },
  { name: 'P2', adds: ['DRAFT-2'], takesOut: ['E00003'] },
  { name: 'p3', adds: [], takesOut: ['E00007'] },
];

describe('the packages the Package cells write, as Make package… reads them (ADR-0016 notes 90 and 95)', () => {
  it('says every package a trade is in, new or existing, in the order listed, and none for a trade in no package', () => {
    expect(packagesOf(written, 'DRAFT-2')).toEqual(['P1', 'P2']);
    expect(packagesOf(written, 'E00003')).toEqual(['P1', 'P2']);
    expect(packagesOf(written, 'E00007')).toEqual(['p3']);
    expect(packagesOf(written, 'DRAFT-3')).toEqual([]);
  });
});
