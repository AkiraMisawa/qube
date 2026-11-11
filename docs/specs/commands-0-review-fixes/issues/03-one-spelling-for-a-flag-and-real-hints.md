# 03: A flag is read one way, and the hints of `set` and `vary` show the open workspace

**What to build:** `parseBoolean` reads `on`, `off`, `yes`, `no`, `true`, `false`, `y` and `n` in any case, as the
server's reader of a flag does (ADR-0006's note 4 of 2026-11-11), so a trade grid's B and A cells, a CSA's `exchange`
and `one-way` and Rates' flags take what `set` takes; a refusal says `Type on or off.` The XVA page offers
`examples()`, its first CSA's id and the first trade id each grid has read, and the hints of `set` and `vary` are
made from them, never from an id no workspace has.

**Blocked by:** nothing.

**Status:** ready-for-agent

- [ ] Every spelling is read, in any case; a word that is none is refused with `Type on or off.`
- [ ] `off` typed in an A cell of a new trade clears A.
- [ ] The hint of `set` on an open workspace shows one of its trades and one of its CSAs.
