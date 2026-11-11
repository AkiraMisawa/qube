# 03: A flag is read one way, and the hints of `set` and `vary` show the open workspace

**What to build:** One reader of a flag (`parseBoolean` at first, `readFlag` since the review) reads `on`, `off`,
`yes`, `no`, `true`, `false`, `y` and `n` in any case, as the server's reader of a flag does (ADR-0006's note 4 of
2026-11-11), so a trade grid's B and A cells, a CSA's `exchange` and `one-way` and Rates' flags take what `set` takes;
a refusal says `Type on or off.` The XVA page offers `examples()`, its first CSA's id and the first trade id each grid
has read, and the hints of `set` and `vary` are made from them, never from an id no workspace has.

**Blocked by:** nothing.

**Status:** done

- [x] Every spelling is read, in any case; a word that is none is refused with `Type on or off.`
- [x] `off` typed in an A cell of a new trade clears A.
- [x] The hint of `set` on an open workspace shows one of its trades and one of its CSAs.

Done 2026-11-11. `parseFlag` is the one reader of a flag's cell (`editing.ts`), Rates' flags included. The grid writes
a flag `True` or `False`; what it reads is wider than what it writes, as `set` is. `setHint` and `varyHint`
(`xva/commands.ts`) take the page's `examples()`: with a trade but no CSA the example is `<trade> a off`. The trade
is the first of the page a grid read last, the Existing grid's before the New one's, as the spec says.

After the code review, 2026-11-11: the reader is `readFlag` (`client/src/platform/flags.ts`), and a product's `flag`
term reads through it too. The server reads the same words in one place, `FlagWords.Read` in the kernel, for a
product's term and for a portfolio cell: a term took `true` and `false` only (`ProductTests`).
