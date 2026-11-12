# 15: The detail dialog is drawn from the registration

**What to build:** A modal drawn from the form: a section a group, a grid a table under the row with its add row and
remove button, a button a template, tabs Original and Live, Save and Cancel; full screen on a phone.

**Pull request:** 5c

**Blocked by:** 13, 14.

**Status:** done

- [x] A field changed in the dialog is a line of the draft, and a refusal marks the field and leaves the dialog open.
- [x] Save runs `apply`; closing with pending lines asks first. *Changed as built (ADR-0016 note 57):* the Cancel
      button discards the draft on the page and asks the server nothing, so a draft the workspace has left still
      closes; `cancel` typed on the line is the server's to read, as before.
- [x] Original is read-only.
- [x] The dialog says that the sample's measures do not read the legs.
- [x] Keyboard, mouse and touch reach every field; looked at 390, 768, 992 and 1440px in both themes.
