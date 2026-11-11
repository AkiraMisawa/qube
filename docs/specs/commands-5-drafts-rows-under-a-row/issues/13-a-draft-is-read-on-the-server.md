# 13: A draft is read on the server

**What to build:** A draft is a row, the revision it was opened at and the lines accepted so far; the server reads the
lines in order on the workspace and answers the form: fields by group with their values and whether the draft changed
them, the tables under the row with their rows, the templates, and each line's sentence (ADR-0016 §10).

**Pull request:** 5c

**Blocked by:** the pull request before it.

**Status:** ready-for-agent

- [ ] The form of a row with no line is the row as the workspace has it.
- [ ] Each line is read on what the lines before it left; a line that is refused is answered with its refusal and the
      form of the lines before it.
- [ ] A row made in the draft and a row removed in it are in the table under the row as the draft leaves it, each
      marked.
- [ ] A draft whose revision the workspace has left is refused, saying what moved.
- [ ] Original answers the row as the source alone gives it.
