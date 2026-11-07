# Issue tracker: docs/specs/

Specs and tickets for this repository live as markdown files under `docs/specs/`, versioned with the ADRs they
implement. Like everything committed here, they are written in English, and every example in them is synthetic.

## Conventions

- One feature per directory: `docs/specs/<feature-slug>/`
- The spec is `docs/specs/<feature-slug>/spec.md`. Where the ADRs already state the work completely, the spec may be
  a short file that cites them and lists what is in and out of the slice.
- Implementation tickets are one file per ticket at `docs/specs/<feature-slug>/issues/<NN>-<slug>.md`, numbered from
  `01`, never a single combined tickets file
- Triage state is a `Status:` line near the top of each file. The roles are `needs-triage`, `needs-info`,
  `ready-for-agent`, `ready-for-human`, `done`, `wontfix`
- Comments and conversation history append to the bottom of the file under a `## Comments` heading

## When a skill says "publish to the issue tracker"

Create a new file under `docs/specs/<feature-slug>/` (creating the directory if needed). "Apply the `<role>` label"
means write `Status: <role>` near the top of that file.

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the ticket number directly.

## A spec is not a decision record

A spec synthesises decisions that already exist; it does not make new ones. Where writing it exposes a decision the
ADRs do not yet contain, stop and record the ADR first (see `domain.md`), then cite it from the spec. A spec that
disagrees with an ADR is wrong, not the ADR.
