# 05: The pricer and Rates run a sequence

**What to build:** The module registered by the host, and a sequence run on each application (ADR-0016 notes 60 to
62).

**Pull request:** 6

**Blocked by:** the ticket before it.

**Status:** done

- [x] A pricer's sequence that ends with `price` leaves a Run of the revision its edits left, with origin `workflow`.
- [x] A sequence saved on one counterparty's workspace runs on another's.
- [x] A pricer's sequence is not seen on a Rates workspace.
- [x] The host serves both commands in its catalog, and a saved sequence is there after a restart.
