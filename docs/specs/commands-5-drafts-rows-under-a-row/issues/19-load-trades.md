# 19: `load trades <ids>`

**What to build:** A synthetic store of booked trades by id; `load trades <ids>` makes each a new trade of the
workspace (ADR-0016 note 37).

**Pull request:** 5d

**Blocked by:** the pull request before it.

**Status:** done

- [x] `load trades B00012, B00013` adds the two as new trades, as one command.
- [x] An id the store has not, or a trade the workspace has already, is refused by name, and none is loaded.
- [x] The line completes ids from the store.
