# 02: `tables.describe`

**What to build:** A kernel capability that answers a workspace's registration read out (ADR-0016 §7, note 38): its
tables, and their fields with each one's words, form, how a value is written, whether a command sets it, whether it
may be empty, its choices, the field that carries its unit and the table an id points into. One table where one is
named. It is typed on no line.

**Pull request:** 4a

**Blocked by:** 01.

**Status:** done

- [x] The tables of the workspace's application are answered with their words, key, subsets and row counts.
- [x] A field is answered with its form and the words that say how its value is written, the same a refusal uses.
- [x] Choices over fifty are listed to fifty, with how many more there are.
- [x] One table by its id or its words; an unknown one is refused with the tables there are.
- [x] Another user's workspace is refused (`NotOwner`), and a moved revision as any command's is.
- [x] The answer meets the contract it declares.

## Comments

- *Corrected as built (ADR-0016 note 38):* another user's workspace is refused `NotOwner`, as every command of a
  workspace refuses it, where this ticket's first wording said "as an unknown one". The box above says it as built.
- *Added as built:* how a value is written has one wording (`FieldForms.What`), which a refused value is told and
  the description answers. A choice's `written` is `one of its choices`, with the choices beside it.
