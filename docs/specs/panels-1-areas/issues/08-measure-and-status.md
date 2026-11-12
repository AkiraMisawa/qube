# 08: ADR-0018 §6's measurement, the status and the screens

Status: done

**What to build:** three XVA pages of 10,000 trades in three areas at 1440px, beside one area: the elements on the
page and the time to show another tab of one area; `IMPLEMENTATION_STATUS.md`; README's commands; the pull request's
screens.

**Blocked by:** 02 to 07.

- [x] The elements are the three pages' own and one tab per open panel.

## Comments

2026-11-12, step 1's pull request: measured at 1440 by 900px with the Pricing DB and three XVA workspaces of 10,000
synthetic trades: 491 elements in one area (the shown page 280), 1367 in three (the pages 317, 317 and 389); about 65
elements an area beside its page, none for a panel not shown. A tab's switch, median of ten: 92ms in one area, 89ms in
three. The figures are in `IMPLEMENTATION_STATUS.md`. README's commands are unchanged: the areas add a spec file
(`e2e/areas.spec.ts`), which `npx playwright test` already runs, and no command.
