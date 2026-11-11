# 10: `price`, `refresh` and `pv01 retry` go through the door

**What to build:** The three are capabilities that say how they are typed. They are not tried: the preview says what
would start (the measures and the cases Price would price) and refuses what can be refused without starting. The
page's Price button and its keys call the same capability.

**Pull request:** 3b

**Blocked by:** 04.

**Status:** done

- [x] `price` previews the measures and the number of cases, and a set of cases that no longer matches is refused there.
- [x] Price's result arrives as it does now, and a chained `price` says it in the line.
- [x] `refresh` and `pv01 retry` say what they did.
- [x] The line lists the page's application's served commands first, in the order the module registers them, `price`
      first, and the commands still written in the client after them. (3a left the order as it was: the client's,
      then the served, so the placeholder reads `price, scenario, scenario include…`.)

## Comments

2026-11-11, as built (ADR-0016 note 33). The command is `xva.workspace.price`, since `xva.price` is the step inside
the Run. `price` and `pv01 retry` have the new effect `execute`. The gateway hands a handler the key it was invoked
with, and `price` submits its Run under it. The reading of `price` builds the Run's input and keeps nothing; what
that costs at 10,000 trades is not measured. A command whose arguments may all be left out is read by its words
alone, so `pv01 retry` is previewed as it is typed.
