# 09: Templates

**What to build:** A table registers templates, each with the values it takes; `set <rows> as <template>
[<name>=<value> …]` writes the field values the table answers, as several values are written; the line completes it;
`tables.describe` lists them (ADR-0016 §1).

**Pull request:** 5b

**Blocked by:** the pull request before it.

**Status:** done

- [x] `set <rows> as <template> name=value` writes each field, each checked in order, as one command.
- [x] A value the template needs and is not given refuses it by name; a template the table has not is refused with
      those it has.
- [x] After `as` the line offers the templates, then the values one takes.
- [x] `tables.describe` answers a table's templates with what each takes.
