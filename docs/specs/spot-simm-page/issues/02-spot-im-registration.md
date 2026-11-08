# 02 Spot IM registers into the page

Status: done

ADR-0012 §2, §3 and §7, with their notes of 2026-11-08, and ADR-0010 §10.

SIMM's measure module registers `simm.spotIm`, with metrics `imR` and `imP`. Its membership carries a scope per
direction and regime (`collect/JFSA`), each in or out with its reason, and the compact reason the grids show. Its
inputs are the IM-R and IM-P columns on the trade grids and the IM tab (`im`). Its validation refuses spot IM alone
(`SpotImRefused`) in two cases: a trade in Before or After that names an IM CSA the counterparty does not hold in that
direction, and a trade in scope that cannot become a Rates line under the confirmed conventions.

Its part of the accepted input freezes:

- the IM CSAs;
- each priced trade's classification;
- the market (the opening snapshot's ID and the portfolio's FX of the value date);
- the convention profiles' versions.

The register gains what this needs, generically. A membership gains its scopes. A trade-grid field can be a measure's
input. A metric can name the trace term its cell opens.
