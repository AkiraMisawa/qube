# 06: A page declares its places, and `go` puts the keyboard there

**What to build:** A page registers places with the shell: a word, a title, and how the keyboard is put there. The
shell builds `go <place>` with its completions. The pricer's page declares its two trade grids, its input tabs and the
dock (ADR-0016 §9).

**Pull request:** 2b

**Blocked by:** 05.

**Status:** ready-for-agent

- [ ] `go existing`, `go new`, `go csa`, `go results` put the keyboard there and say where it went.
- [ ] `go` offers the page's places, and refuses a word that names none with the places there are.
- [ ] On a page with no places `go` says so.
