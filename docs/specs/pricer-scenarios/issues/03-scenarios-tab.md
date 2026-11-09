# 03: the Scenarios tab edits every shift in one grid, and the list includes, duplicates and removes

**What to build:** the Scenarios input tab (ADR-0010 §4 and its note of 2026-11-09): the grid of shifts, `Scenario |
Category | Index | Risk factor | Shift type | Size | Description`, ending in blank rows, typed or pasted many rows at a
time, with Category and Shift type derived and read-only, each row's issue said on it, and a row whose cells are all
cleared removed; beside it the scenarios with their shift counts and include boxes, Duplicate and Remove. Commands
`scenario include|exclude|remove <name>`, `scenario duplicate <name> <new name>`, `scenarios clear` and
`open scenarios`. Price refuses an included scenario that holds an invalid row, naming it.

**Blocked by:** 01.

**Status:** done

- [x] A block pasted onto the blank rows makes a shift per row in one batch; a cell that does not resolve refuses
      the paste whole, naming its row and column.
- [x] An incomplete row says what it lacks, and Price refuses its scenario by name while it is included.
- [x] Excluding a scenario leaves it out of Price and keeps its rows; duplicating copies its rows under the new name;
      removing takes its rows away in one batch.
- [x] The tab says how many scenarios it holds.
- [x] In a browser, a block pasted onto the grid, the include box and Duplicate, by keyboard; at 390px the tab is among
      the Inputs and never scrolls sideways.
