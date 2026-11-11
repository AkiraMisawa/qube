# 16: The pricer's refusals name their word

**What to build:** Where one of the pricer's verbs refuses a word of its line, the refusal names it and lists what
it could have been (ADR-0016 note 45): `vary`'s row and column, a measure, `cds`'s words, `case remove`'s case, a
scenario's name.

**Pull request:** 4d

**Blocked by:** 15.

**Status:** done

- [x] `vary CSA-Z threshold 5mm` marks `CSA-Z` and lists the rows a cell is named on; `vary CSA-A colour 5mm` marks
      `colour` and lists the CSA's columns.
- [x] `measures cva dva2` marks `dva2` and lists the measures.
- [x] `cds`, `case remove` and a scenario's name mark what the catalogue or the workspace has not.
- [x] The AI's `commands.preview` answers the same word and candidates.

## Comments

- `cds` is a candidate for a row only where a curve is chosen; with none, `cds` is refused by why there is none,
  with its word and no candidate (note 45, "Changed as built").
- A workspace with no scenario has none to offer for a scenario's name; a refusal of the whole line (`case remove`
  with no name, `vary` with no value) names no word.
- The AI's preview is checked through the agent's door with the pricer registered (`PricerCommandTests`).
