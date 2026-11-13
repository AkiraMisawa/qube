# 06: Layer 3, the screens and the status

Status: in progress (the Device Pixel test, marked failing, goes green with 04; step 1's mouse-drag flake is open)

**What to build:** `e2e/windows.spec.ts` as the spec's Testing Decisions list it; the pull request's screens (the
window beside the main one, both schemes); `IMPLEMENTATION_STATUS.md`.

**Blocked by:** 03, 04, 05.

- [x] The new spec passes with `--repeat-each=3`.
- [x] The full run passes: 148 on the code as it stands. One run before it, on the same code, failed step 1's test of
  a tab dragged with the mouse, which passed in every run after it: a flake, open (`IMPLEMENTATION_STATUS.md`, step 2's
  Known limits).
- [ ] The full run passes again after the ExGrid bump (04), the Device Pixel test green in it.
