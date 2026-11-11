# 15: A workspace's refusal names its word

**What to build:** `WorkspaceRefused` may hold the word it refuses and what it could have been, and a reading marks
that word in the line and answers the candidates, as it does for a table's refusal (ADR-0016 note 45).

**Pull request:** 4d

**Blocked by:** the pull request before it.

**Status:** done

- [x] A command whose reader refuses with a word has it marked where it stands after the command's words, with its
      candidates; a word that stands there twice is not marked.
- [x] A capability's failure is its code and sentence, as before.

## Comments

- The word is marked by the same rule as a table's refusal: where it stands once after the command's own words.
- The capability a reader serves maps the refusal to its code and sentence, as before: the word is the line's.
