# 05: `add trades` prices each new trade alone

**What to build:** `add trades` makes one case per New row with A ticked, at that moment, each holding that trade alone
on top of the existing portfolio and never the other candidates (docs/03 §5, ADR-0010 §3's note of 2026-11-08). A
candidate ticked in Before is refused by name, never unticked. Price refuses, naming the trades, when the New rows with
A ticked no longer match the set; `add trades` again makes it anew. A common change to the existing trades is said in
every case of the set.

**Blocked by:** 02.

**Status:** done

- [x] Three new trades with A ticked make three cases, each After holding the existing members and one new trade.
- [x] A candidate in Before is refused by name and nothing is made.
- [x] Ticking a new trade the set left out, after it was made, refuses Price, naming it.
- [x] Each case's figures equal a Price of the workspace with only that new trade ticked.
