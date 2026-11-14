# 06: Layer 3, the screens and the status

Status: done (2026-11-14, on ExGrid 0.3.0). Step 1's mouse-drag flake, met once here, is open
(`IMPLEMENTATION_STATUS.md`, step 2's Known limits).

**What to build:** `e2e/windows.spec.ts` as the spec's Testing Decisions list it; the pull request's screens (the
window beside the main one, both schemes); `IMPLEMENTATION_STATUS.md`.

**Blocked by:** 03, 04, 05.

- [x] The new spec passes with `--repeat-each=3`.
- [x] The full run passes: 148 on ExGrid 0.2.0, before the bump. One run before it, on the same code, failed step 1's
  test of a tab dragged with the mouse, which passed in every run after it: a flake, open (`IMPLEMENTATION_STATUS.md`,
  step 2's Known limits).
- [x] The full run passes again after the ExGrid bump (04), the Device Pixel test green in it: 148 on ExGrid
  0.3.0. The pull request's screens are taken again on 0.3.0.
