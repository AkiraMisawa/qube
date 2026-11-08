# 01 Result tabs registered by a measure

Status: done

ADR-0010 §7 and §10. A measure's registration gains its result tabs: an ID, a title and the view that draws the tab.
The view is `trace` for spot IM's `SIMM` and for spot CCR capital's `CCR`. The pricer API lists them with the measure.
The dock draws `Metrics`, then the tabs of the selected measures that the Run in view priced: a measure left out after
the Price keeps its Metrics and loses its tab. A Metrics cell or `explain` of a measure with a trace tab opens that tab
at the term.
Otherwise the cell opens the trace panel, as today. `open result simm` opens the tab from the command line, and `dock
full` and `dock restore` widen the dock and put it back. A tab of a measure priced on no side says why.

Tests: the register and the API list the tab; Vitest derives the dock's tabs from the register and the Run.
