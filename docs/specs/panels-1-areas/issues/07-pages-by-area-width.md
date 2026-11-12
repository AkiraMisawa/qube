# 07: Pages lay themselves out by their area's width

Status: done

**What to build:** every panel's content a CSS size container; the pricer's three sections, its full-height button,
`xva.css`, `trace.css` and `TraceView.tsx` reading the container's width, and the pricer's heights its height, in place
of the window's. Built last, after the pull requests open on the pricer's files are merged (ADR-0018 §11).

**Blocked by:** 02, and the open pull requests on the pricer's files (merged: ADR-0016's step 5d, #33, 2026-11-12).

- [x] Layer 3: the XVA page in a 600px area shows its section switch; in the strip at 1440px it does not.

## Comments

2026-11-12, step 1's pull request: the panel's content is the size container (`PanelPage`, `inline-size`), and the
trace view and `trace.css` read it (layer 3: the trace drills in a 720px area and is a tree at 1080px, in the same
page). The pricer's section switch, full-height button and `xva.css` wait for ADR-0016's step 5d, which is being built
on those files (ADR-0018 §11), and are a pull request of their own after it merges. The checkbox above is theirs.

2026-11-12, later: ADR-0016's step 5d merged (#33) while step 1's pull request was open, so the pull requests on the
pricer's files that blocked the rest of this ticket have merged. The rest is the pull request after step 1's.

2026-11-12, the pricer's pull request: the pricer's three sections, its Full height and `xva.css` read the panel's
width, and its Existing grid the panel's height (`--qube-panel-room`), with the profile's and the deltas' two columns
from the panel's 1200px. The checkbox is `areas.spec.ts`'s "the pricer lays itself out by its area": the switch in an
area of 605px (`size panel 42%` of a 1440px row; no verb gives an area 600px), and none in the strip of a 1440px
window, twice: where the window is too short for two areas one above the other (the page 1440px wide), and where the
AI pane leaves too little for four areas in a row (the page 1040px wide). The same test floats the pricer to the right
half of the window, under 768px, where it is the three tabs. The container queries
are in pixels and the range syntax: in `em` they read the page's 13px, which put the trace's `md` at 806px in step 1's
pull request, and an area's fractional width fell between `max-width: 767.98px` and `min-width: 768px`.
