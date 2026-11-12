# 07: Pages lay themselves out by their area's width

Status: ready-for-agent

**What to build:** every panel's content a CSS size container; the pricer's three sections, its full-height button,
`xva.css`, `trace.css` and `TraceView.tsx` reading the container's width, and the pricer's heights its height, in place
of the window's. Built last, after the pull requests open on the pricer's files are merged (ADR-0018 §11).

**Blocked by:** 02, and the open pull requests on the pricer's files (merged: ADR-0016's step 5d, #33, 2026-11-12).

- [ ] Layer 3: the XVA page in a 600px area shows its section switch; in the strip at 1440px it does not.

## Comments

2026-11-12, step 1's pull request: the panel's content is the size container (`PanelPage`, `inline-size`), and the
trace view and `trace.css` read it (layer 3: the trace drills in a 720px area and is a tree at 1080px, in the same
page). The pricer's section switch, full-height button and `xva.css` wait for ADR-0016's step 5d, which is being built
on those files (ADR-0018 §11), and are a pull request of their own after it merges. The checkbox above is theirs.

2026-11-12, later: ADR-0016's step 5d merged (#33) while step 1's pull request was open, so the pull requests on the
pricer's files that blocked the rest of this ticket have merged. The rest is the pull request after step 1's.
