# 07: Pages lay themselves out by their area's width

Status: ready-for-agent

**What to build:** every panel's content a CSS size container; the pricer's three sections, its full-height button,
`xva.css`, `trace.css` and `TraceView.tsx` reading the container's width, and the pricer's heights its height, in place
of the window's. Built last, after the pull requests open on the pricer's files are merged (ADR-0018 §11).

**Blocked by:** 02, and the open pull requests on the pricer's files.

- [ ] Layer 3: the XVA page in a 600px area shows its section switch; in the strip at 1440px it does not.
