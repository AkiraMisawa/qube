# 01: A command says which workspaces it is for

**What to build:** A command form names the workspace application its command acts on, or none for a kernel verb. The
server reads a line for a workspace with the commands of that application and the kernel's. A page's door says its
application, and the line lists a command only where it can act (ADR-0016 note 27).

**Pull request:** 3a

**Blocked by:** none.

**Status:** done

- [x] A form's `app` is in the catalog, and a kernel verb's form has none.
- [x] A line that begins with another application's words is read as no command of this workspace.
- [x] On a page of another application the line does not list the command.

## Comments

- Built as written. A form with no `app` is every application's (the kernel's verbs); the pricer's forms name `xva`.
  The catalog carries `app` on a form, and the client lists a served command on a page whose application it names.
