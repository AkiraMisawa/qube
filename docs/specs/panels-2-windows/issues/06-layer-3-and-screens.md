# 06: Layer 3, the screens and the status

Status: in progress (the Device Pixel test, marked failing, goes green with 04)

**What to build:** `e2e/windows.spec.ts` as the spec's Testing Decisions list it; the pull request's screens (the
window beside the main one, both schemes); `IMPLEMENTATION_STATUS.md`.

**Blocked by:** 03, 04, 05.

- [x] The new spec passes with `--repeat-each=3`.
- [ ] The full run passes. On the final code it passed but for step 1's test of a tab dragged with the mouse, which failed
  once and passed in every run after it: a flake, open (`IMPLEMENTATION_STATUS.md`, step 2's Known limits).
