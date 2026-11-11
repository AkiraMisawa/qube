# 04: `vary`, `add each` and `cds` are read on the server

**What to build:** The three capabilities say how they are typed and read their own line into the request they already
take. The preview is a trial: the changes are planned and checked with nothing written, and the reading says what
would be made or refuses as the command would. `add trades` is renamed `add each`. The client's hand-written commands
go, and the page's own control for one (the `Vary values…` dialog) calls its capability through the door (ADR-0016
note 28).

**Pull request:** 3a

**Blocked by:** 01.

**Status:** done

- [x] `vary CSA-A threshold 10mm, 5mm` previews the two cases it would make, and Enter makes them.
- [x] A value the cell cannot hold, a cell that is not one, and a comma that could group a number are refused in the
      preview with the command's own sentence.
- [x] `add each` previews the cases it would make and those it would replace; with no New row ticked it is refused.
- [x] `cds` completes from the catalogue's words and previews the curve it would choose.
- [x] A reading writes nothing: the revision and the log stand.
- [x] The AI's request with the same words gets the same answer and the same refusal.

## Comments

- The Credit tab's selects stay field edits of the page's queue, and this ticket no longer names them: a proxy is
  chosen a key at a time (type, sector, region, rating), each a field as a cell is, and `cds` says a whole choice in
  one line (ADR-0016 note 28, "What stays an edit of the page's own queue").
- `xva.vary` takes `cell`, as the line and the AI name it, or `entity` and `field`, as the dialog names the cell its
  menu opened on. Both, or neither, is refused. A request with no values is refused by the contract (`422
  ValidationFailed`), where the page's endpoint answered 400.
- The dialog shows the command's own sentence: the door's `invoke` hands it to a caller that says the outcome itself,
  and the page raises no notice of it. It sends its values as typed; the client's copy of the comma rule is gone.
- The AI's `xva.cds` now has the page read the workspace again, as its other edits do: it was missing from the list.
- Not built: `vary` completes nothing, and a domain verb's refusal marks no word and lists no candidates (ADR-0016
  note 28, "What a domain verb does not do yet").
- The page's `/vary`, `/cds` (the write) and `/add-trades` endpoints are gone.
