# Domain docs

How the engineering skills consume this repository's domain documentation. Single-context: one set of decisions at
the repository root.

## Before exploring, read these

- **`docs/adr/`**: the decisions and their reasons. Read every ADR that touches the area you are about to work in.
  Where code, a comment, a plan or a conversation disagrees with an ADR, the ADR wins and the disagreement is a defect
  in the other thing.
- **`docs/00-decisions.md`** and the numbered documents `docs/01` to `docs/14`, by the reading guide in `CLAUDE.md`.
  ADR-0001 supersedes them where they still name Blazor as the client.
- **`docs/design-guidelines.md`** before any change to the client's look or layout (ADR-0009).
- **`GLOSSARY.md`** at the repository root, once it exists. If it does not, proceed silently; the `/domain-modeling`
  skill creates it when the first term is resolved.

## Decision labels

`CONFIRMED` is the owner's requirement. `DEFAULT` is a proposal that may be used without asking again; record a
reason before changing it. `OPEN` needs discovery when its slice is reached. A ticket never turns a `DEFAULT` or
`OPEN` into a `CONFIRMED` on its own.

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0010 §3 (a case is one Before/After pair), but worth reopening because…_

A decision that changes is written into its ADR, with what changed and why, before the code that follows it.
