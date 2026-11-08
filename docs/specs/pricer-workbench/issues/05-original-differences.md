# 05: A value that differs from Original is marked

**What to build:** the server reports, for each trade row, CSA and netting set, the fields whose effective value
differs from Original and Original's value (ADR-0010 §2). The page marks those cells (ExGrid's `modified` until the
Split Fill release), their description gives Original and the current value, and each input tab says how many
changes it holds (`CSA ●2`). A value edited back to Original is not marked; a value a rule changed is.

**Blocked by:** 03, 04.

**Status:** done

- [x] A CSA curve edit marks the CSA's cell and the curve of every trade that follows it, not a trade whose curve the
      user set to the same value it had.
- [x] B and A are never marked.
- [x] The CSA tab's label counts its changed values.

*Done 2026-11-08.* Original is the source under the rules with no edit, materialised once per source version; the
rows carry `original` (each differing field with Original's value), compared by value (1.0 is 1). The marks are
ExGrid's `modified` until the page moves to the Split Fill of ex-grid ADR-0490, which the side-only layers need
(Delivery step 3).
