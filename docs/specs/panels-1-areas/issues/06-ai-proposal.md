# 06: The AI proposes an arrangement, and the user applies it

Status: ready-for-agent

**What to build:** the kernel capability `layout.propose` (effect `Propose`), its contracts and its checks; the pane's
context `qube.panels`; the scripted request for "side by side"; the card's plan, Apply and `arrange <n>`; `arrange back`.

**Blocked by:** 03.

- [ ] xUnit: a well-formed proposal is answered; each malformed one is refused by path.
- [ ] Vitest: a proposal made into a layout; one naming a panel closed or retitled since is refused by name.
- [ ] Layer 3: asked, proposed, applied, put back.
