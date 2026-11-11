# 06: A page declares its places, and `go` puts the keyboard there

**What to build:** A page registers places with the shell: a word, a title, and how the keyboard is put there. The
shell builds `go <place>` with its completions. The pricer's page declares its two trade grids, its input tabs and the
dock (ADR-0016 §9).

**Pull request:** 2b

**Blocked by:** 05.

**Status:** done

- [x] `go existing`, `go new`, `go csa`, `go results` put the keyboard there and say where it went.
- [x] `go` offers the page's places, and refuses a word that names none with the places there are.
- [x] On a page with no places `go` says so.

## Comments

- As built (ADR-0016 note 20): the registry is `client/src/shell/places.ts` (`registerPlaces`, `placesOf`), by panel;
  a place is `{ word, title, grid?, go() }`, and `go()` answers why where the keyboard cannot be put there now. A page
  of one grid registers with `registerGridFocus`, which makes the one place `grid`.
- `go <place>` is the shell's command (`panel.go`), in the client only. Its notice is `The keyboard is in Existing
  trades.`; a word that names no place is refused by the line's check of an argument (`place: one of new, existing,
  …`); a place that cannot take the keyboard keeps the line with its reason (`go results` before anything is priced).
- The pricer's places: `new`, `existing`, each input tab by the word `open` takes, `results`, and `trace` first while
  a trace is widened over the grids. Below `sm` the part a place is in is shown before the keyboard is put there.
- `open <tab>` stays, and now keeps the line with the reason where its tab does not show (it was a refused notice).
  Which of `open <tab>` and `go <tab>` goes is step 3's.
- Looked at in Chromium at 1440 and 390 px, by a script and by eye; one browser test holds `go`, `focus grid`, the
  refusals and the three parts at 390 px.
- From the review: `open <tab>` is `go` to that tab's place, so it says where the keyboard went too. A page offers
  its places only while it draws them, and each has something to take the keyboard: a tab with nothing to work in
  takes it on its own tab, the dock takes it itself where it draws no tab and no control. The line gives no keyboard
  back once a place is taken (`keepKeyboard`), however late the page draws it. A second browser test holds the tab
  that takes the keyboard itself, the notice of `open csa` and the refusals of a tab no selected measure needs; the
  trace test holds `go trace`.
