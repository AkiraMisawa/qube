# 01: The portfolio domain module

**What to build:** the shared portfolio module of ADR-0010 §10 as a prefactor. The synthetic context, agreement and
netting set, CSAs, trades with B/A, the edit rules and scopes, the effective rows and the workspace service move out
of the XVA module into the portfolio module; the XVA module keeps its price capability, history projection and
contracts and builds on the portfolio module. Nothing a user or a caller sees changes.

**Blocked by:** None (can start immediately).

**Status:** done

- [x] The server builds with the portfolio module beside the XVA module, outside the composition kernel, and the
      kernel references neither.
- [x] Every existing server test and the XVA browser test pass unchanged in what they assert.
- [x] The wire paths and the stored app id are unchanged, so a workspace saved before still opens.
