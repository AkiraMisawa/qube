# 02: A short form expands to a kernel line

**What to build:** A module registers short forms for its application: words, a title, a form and how it expands,
given the words typed and the view. The server reads one by reading its expansion: the preview answers the kernel
command, its inputs and the line it expands to. The catalog lists short forms apart, and the line builds their
entries, shows the expansion under the input and runs the expansion's inputs at Enter (ADR-0016 note 29).

**Pull request:** 3a

**Blocked by:** 01.

**Status:** done

- [x] A short form's preview names the kernel command, its rows and what would change, and says the line it expands to.
- [x] Enter runs the kernel command, and `undo` names the expanded line.
- [x] A short form that cannot expand is refused by name, with nothing run.
- [x] Two commands, or a command and a short form, that answer to the same words fail at start.
- [x] The agent's tools hold no short form.

## Comments

- Built as `IShortForm` (a descriptor, `Expand(LineContext)` and `Complete(LineContext)`) answered by an
  `IShortFormCatalog`. The reading of a short form is the reading of the line it expands to, and the preview says that
  line (`expands`), which the line shows as `Runs as …` before Enter.
- A short form that shares its words with a command, or with another short form of its application, fails where the
  lines are built, which the host does at start: the registration's defect. What fails at the read is a short form
  whose expansion begins with no command's words.
- The agent's tools are the capabilities, and a short form is none: the catalog test's list of the commands that say
  how they are typed holds no short form's id.
