# 01 IM CSAs and each trade's IM-R and IM-P

Status: done

ADR-0012 §2 and its note of 2026-11-08. The portfolio's synthetic source gives each agreement its IM CSAs, at most one
per direction, each with the regimes it serves and the date the counterparty came into each one's scope. It also gives
each existing trade its IM-R and IM-P, and classifies every trade by direction. A physically settled FX forward is
exempt everywhere.

On the trade, `imR` and `imP` are assignment fields, as `csa` is. A new trade takes the counterparty's IM CSA of each
direction automatically, or none where there is none. An edit sets an IM CSA's ID or empties it. Both are compared
against Original, sortable, and scoped to the counterparty.

The synthetic data:

- ABC/LE-01 collects under JFSA and CFTC and posts under JFSA and EMIR. Its regimes start at dates that leave some
  existing trades out of CFTC or EMIR.
- ABC/LE-02 has IM CSAs whose only regime starts after most of its trades.
- XYZ/LE-03 has none.

All of it is made up.
