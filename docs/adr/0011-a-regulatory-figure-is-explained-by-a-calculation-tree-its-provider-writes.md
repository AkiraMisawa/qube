# ADR-0011: A regulatory figure is explained by a calculation tree its provider writes, drawn by one view

Status: PROPOSED 2026-11-07. Raised by the owner: "SIMM and CCR are regulatory calculations, so I want to show their
drill-down too. I want a UI where you can easily trace which term of the regulatory formula has how much value"
(2026-11-07). Written for both spot measures, spot SIMM (ADR-0012) and spot SA-CCR capital (ADR-0013), with the
shapes the SIMM work needs, so the platform has one drill-down and not two. Builds on ADR-0003 §4 (the server pages
large data), docs/05 §2 (Run, Case, AnalyticsRequest) and §6 (intermediate results), ADR-0009 (design guidelines) and
ADR-0010 §7 and §10 (a measure registers whether its Metrics cell opens a trace).

*Revised the same day, before review. The first draft put an expression, a label and a citation on every term, in the
provider's output. Agreed with the SIMM work instead: the formula belongs to the term's **kind**, registered once per
method version (§2), and a provider writes only values. A formula repeated on thousands of terms is bulk and a second
place to be wrong; a formula a provider or the AI sends is text the page would draw without having checked it. The
owner then asked that formulas be typeset as real mathematics (2026-11-07), so a kind also carries its TeX (§6). The
operation set gained `slot`, `children`, `param`, `field`, `const`, `ln`, `normalCdf`, `sumLeaves`, `singleFactor` and
`lookup`, and `quadraticForm` was generalised, because SA-CCR's options, its single-factor asset classes and SA's risk
weight table need them. The engine team is building the drill-down output now; the owner wanted the client's proposal
of its format, which is the Claude Doc "ドリルダウン形式の提案：スポットCCRキャピタル" and follows this ADR.*

## Context

A regulatory measure is a published formula with named terms. SA-CCR's exposure is
`EAD = alpha × (RC + PFE)`, where `PFE = multiplier × AddOn`, the add-on is a sum over asset classes, each asset class
a sum over hedging sets, and so down to each trade's adjusted notional, supervisory delta and maturity factor. SIMM's
initial margin is a root of a sum of squares with correlations, over products, risk classes, margin types, buckets
and weighted sensitivities, down to each CRIF line. SA's risk weight is not a formula at all but a chain of decisions
(exposure class, approach, the rating used, a table row) that the analytics take at run time from the counterparty
(owner, 2026-11-07), and the owner wants that chain shown too.

One figure without its terms cannot be checked or explained. The questions a desk, a reviewer or a regulator asks
are about terms: why did capital go up when this trade was added (the add-on of one hedging set, or the multiplier
leaving its floor), which bucket drives the margin, which rating set the risk weight. In the what-if page a figure
is always a Before, an After and their Diff (ADR-0010 §7), so the explanation has to be by term and by side.

Four constraints shape the answer:

- **Rather than be quietly wrong, say it.** A term that was not computed is not 0; a term that failed is not 0; a
  term the engine did not return must not be filled in by a recomputation of ours that may disagree with the engine.
- **Size.** A real CRIF runs to hundreds of thousands of lines, and a portfolio to tens of thousands of trades
  (ADR-0003 §4). The browser cannot hold a whole tree.
- **The kernel stays domain-free.** It may hold the shape of an explanation, never what EAD or a bucket is.
- **Formulas are not sums.** max, min, abs, square roots, exponentials, clamps and quadratic forms over correlation
  matrices appear in both formulas; a Diff of terms does not add up to the Diff of their parent.

## Decision

### 1. A calculation trace is a result contract of the kernel

A **calculation trace** is one immutable document per (Run, case, scenario, side, measure), retained with the Run
like any other result (docs/05 §6). It is a flat list of **terms**, each naming its parent, plus a table of
**parameters** and the trade-level **leaf rows**. The kernel defines the contract and knows nothing of any regulation;
a domain module registers the kinds (§2) and a provider fills the values (§3).

The trace's header names the measure, the method and its version (`sa-ccr`, `CRE52`, `2023-01-01`), the producing
engine and request, the valuation date, the reporting currency, the counterparty with its legal entity, the **depth**
the provider returned (root, netting set, hedging set, trade), the reconciliation **tolerance** and its **roots**.

Each term has:

- an **id**, stable for the method version, written as a path (`NS-002/pfe/addon/ir/USD/en/b3`), so that the same
  term on Before and After pairs by id;
- its **parent**, except on a root, and its **childCount**, so the view knows what it can open before it fetches it;
- its **kind** (§2), and a **key** of the attributes the kind's label takes (`{"currency": "USD", "bucket": "b3"}`);
- a **state**, one of:
  - `value`: the term has its value, a number at full precision or, for a decision, a text;
  - `absent`: nothing on this side contributes to it (a bucket with no trade, a trade only in After). It is shown as
    absent, not as 0. A term the provider leaves out, under a parent that has a value, is read as absent;
  - `unavailable`: the provider tried and failed, with its **reason**;
  - `notReturned`: the provider computed the figure but did not return this term (§3), with its reason. Below the
    declared depth every term is `notReturned`;
- **slots**, when the kind's expression names its operands by role and the ids do not already say which term plays it
  (`{"v": "NS-002/v", "c": "NS-002/c", "addon": "NS-002/pfe/addon"}`);
- for a `max`, `min` or `clamp`, which operand **governed** (`RC = max(V − C, 0)` took its first operand; the governing
  regime of a margin is the largest);
- for a decision, **inputs** (the counterparty attributes it read, with their values) and **because** (the reasons, in
  words);
- optionally **leaves**: the leaf set and the number of rows below it;
- optionally an **allocation**: the share of the root attributed to the term by a named method (Euler), whose shares
  sum to their parent's. It is only shown when the provider gives it, and is labelled with its method; it never
  replaces the literal terms.

A **parameter** is a value an expression uses that is not a term: a name, a **scope** (the term it applies to and
below; none is the whole trace, and an expression takes the nearest ancestor's), a scalar, a matrix with labelled rows
and columns, or a text, its unit, and its **source**:

- `regulation`: a fixed value of the rule, with its citation (alpha 1.4, the multiplier's floor 5%, a supervisory
  factor, a SIMM risk weight or correlation, a risk-weight table);
- `configuration`: a configured value with its key and version (the capital ratio);
- `input`: a workspace value with its entity and field path, so the view can go to the cell that set it (a CSA's
  threshold, a counterparty's institution type);
- `data`: a value from source data with its reference and version (an FX rate, a rating);
- `derived`: a value the provider decided from others, with its citation and **because** (an MPOR of 20 days because
  the netting set has 6,210 trades).

The provider writes the values it used, regulatory ones included. Where one differs from the catalogue's (a national
rule), the view shows both and says which the figure used.

A **leaf row** is a trade-level row in a paged dataset (`TypedDataRef`), JSON Lines in the provider's output: its leaf
set, its parent term, the trade's source id and the `tradeInstanceId` the request carried, the **component** when one
trade appears in several places (a cross-currency swap's two legs and its FX part), and the columns its leaf set
declares. The page goes from a row to the trade's row in its grid, and from a trade to every row it feeds.

A trace may have **several roots**: SIMM's Post and Collect for each regulatory regime, with a governing root that
is the largest of them and says which it took.

### 2. Formulas belong to kinds, from a closed set of operations, and every term must reconcile

A **kind** is registered once per method version by the domain module that owns the measure, as it registers
capabilities. It holds a label template over the term's key, a symbol and a **TeX template**, a unit, a citation, and
an **expression**: a small tree of operations, data and not code. A leaf set is registered the same way, with an
expression per computed column (`adjustedNotional = notional × supervisoryDuration`). The provider never sends a
formula. The page draws only formulas that are registered, so nothing a provider or the AI writes is ever rendered as
mathematics.

The operations are:

| Operation | Takes | Means |
|---|---|---|
| `slot` | a role | the value of the child term playing that role |
| `children` | an optional kind | the values of the term's children, for a `sum` and the like |
| `param` | a name | the parameter in scope |
| `field` | a column | in a leaf set only: a column of the same row |
| `const` | a number | a number written in the formula (the 2 in the multiplier) |
| `sum`, `difference`, `product`, `quotient`, `negate` | operands | arithmetic |
| `max`, `min` | operands | the term records which operand governed |
| `abs`, `sqrt`, `exp`, `ln`, `normalCdf` | one operand | an option's supervisory delta uses `ln` and `normalCdf` |
| `clamp` | x, low, high | `max(min(x, high), low)` (SIMM's `S_b`) |
| `sumLeaves` | a column | the sum of a column over the term's leaf rows (a maturity bucket's `D_k`) |
| `quadraticForm` | diagonal, off-diagonal, matrices | `sqrt(Σ_k d_k² + Σ_{k≠l} c_kl · o_k · o_l)`, where `c` is the element-wise product of the matrix parameters and the off-diagonal operands default to the diagonal ones (SA-CCR's maturity buckets, SIMM's `K_b` and its margins) |
| `singleFactor` | operands, correlations | `sqrt((Σ_k ρ_k x_k)² + Σ_k (1 − ρ_k²) x_k²)` (SA-CCR's credit, equity and commodity add-ons) |
| `lookup` | a table parameter, a text term | the row of the table that the term names (a rating band's risk weight) |

A domain module needs no operation of its own; an operation is added to the set by changing this ADR.

A kind's TeX template marks each operand with `\slot{role}`, and its slots are exactly the expression's operands. A kind
whose two do not match one for one is refused at registration.

**The kernel checks that each term reconciles.** Where a term and all its operands have values, the kernel evaluates
its kind's expression and checks `|computed − value| ≤ tolerance × max(1, |value|)`, with the tolerance the provider
declares (DEFAULT: 1e-9). An `absent` operand contributes nothing to a `sum`, `difference` or `sumLeaves`; any other
operand without a value leaves the term not checked, and the view says so. A term that does not reconcile is marked
`inconsistent` with both figures. Its value is never replaced: the check exists to say that a tree does not add up,
not to correct it. A decision term is not evaluated; its `lookup` is checked against the table parameter.

### 3. The provider that computed the figure writes its values

The values come from the provider that produced the figure, in the same response, never from a separate recomputation
by the platform. A reference implementation in a domain module (ADR-0013 proposes one for SA-CCR and SA's risk
weight) writes its own terms as it computes. An engine adapter maps the intermediate terms the engine returned onto
the kinds. **The depth of a tree is what its provider returned**: below it, a term is `notReturned`, and the platform
does not compute what it would have been. A provider declares the deepest level it returns, so the page can say
before Price how far a figure will be explained.

The trace's provenance is the Run's (`ExecutionMode`, `DataOrigin`, the producing invocation): a simulated provider's
tree is labelled simulated on every level.

### 4. Before, After and Diff are paired by term id

The kernel pairs the two sides of a case by term id. A term's Diff is After − Before when both sides have a value.
When one side is `absent`, that side contributes nothing and the Diff is the other side's value, with its sign (a
trade only in After). When either side is `unavailable`, `notReturned` or `inconsistent`, the Diff is unavailable with
that reason. Nothing is ever read as 0.

A Diff of a non-additive term (the multiplier, a square root of a sum) is the change of that term, and the view never
suggests that the children's Diffs add up to their parent's. Only an allocation (§1), when the provider gives it, is
additive; the view shows it as its own column.

### 5. Fetched by term, never whole

A trace is stored whole with the Run and read a term at a time:
`GET /api/runs/{run}/cases/{case}/traces/{measure}?scenario=…&path=…` answers the term on both sides with its Diff,
its kind, the parameters in its scope and its children (paged when there are many), and a leaf set is paged and
sorted by the server like any other dataset (ADR-0003 §4). The catalogue of kinds is read once per method version.
The browser holds the terms on the path it has opened and the page of children in view.

### 6. One view draws every trace

The kernel's view components gain `trace`, drawn by one renderer for every measure. A Metrics cell of a measure that
registers a trace (ADR-0010 §7) opens it at the root for the case and scenario in view; the Before, After and Diff
cells open the same paired view.

- **A breadcrumb** of the path from the root (`K › RWA › EAD › NS-002 › PFE › AddOn › IR › USD`), each step a link back.
- **The formula** of the term in focus, typeset: first its kind's TeX, then the same formula with the values
  substituted, once for Before and once for After, then the term's Diff. An absent operand keeps its symbol, drawn
  faint, and is never written as 0. Its citation, the operand that governed, and its parameters with their sources
  sit beside it; a matrix parameter is typeset as a matrix. Each operand of the formula opens that term or parameter.
- **Its children in a grid** (`WorkbenchGrid`): term, symbol, Before, After, Diff, unit, state, governed mark and
  citation, and the allocation when there is one. Rows sort by the absolute Diff, so the term that moved most is first.
  Enter or a double click opens a child; Alt+Up or the breadcrumb goes back. A leaf set is the same grid over its
  rows, with the trade's membership on each side and an action that goes to the trade's row on its page; opening a row
  typesets its leaf set's column formulas with the row's values.
- **A decision** (SA's risk weight) is drawn as its steps in order, each with its outcome, its reasons, the inputs it
  read and their source, and its citation, and then the table with the row the lookup took marked.
- **The reconciliation** of the term in focus: "matches", "does not reconcile: 12.40mm by its terms, 12.38mm returned",
  or "not checked" with the reason.
- States are words as well as colours (design guidelines §7): `absent`, `unavailable: <reason>`, `not returned by
  <provider>`, `does not reconcile`. The provider and the depth it declared are always on screen, and a simulated
  provider says so.
- Below `sm` the breadcrumb and the formula sit above the list, and the grid shows term, Diff and state.

Mathematics is set by KaTeX at an exact pinned version, in strict mode, with trust granted only to `\htmlData`, which
the renderer itself writes around each `\slot` to make the operand selectable. Templates come from the registered
catalogue only.

DEFAULT: drilling in, one level at a time, as the sample of 2026-11-07 shows
(https://claude.ai/artifact/U6XtjAisqPAyT3zSCB1QW6). The SIMM sample of the same day draws a persistent tree of all
levels beside the formula instead, and the choice is with the owner (OPEN, below).

### 7. Commands and the AI read the same tree

- `explain <measure> [path]` opens the trace of the focused case, scenario and side at that term.
- `trace.read` is a command capability with effect `ReadData` (ADR-0005): given a Run, case, scenario, measure and
  path, it answers the term on both sides with its kind, its parameters and its children. The AI pane (ADR-0007) calls
  it to answer "why did capital go down": it walks down the largest Diffs and quotes the terms, the decisions' reasons
  and their citations. The AI reads the provider's terms and never computes one; a term that is `notReturned` is
  reported as such.

## Consequences

- The kernel gains the trace contract, the kind and leaf-set registration and its slot check, the operation set and
  its evaluator, the reconciliation check, the pairing of sides, the read endpoint, the `trace` view component and
  `trace.read`. A test fills a trace from a non-financial synthetic capability, as CO01 does for capabilities, to show
  the kernel holds no regulatory term.
- ADR-0012 and ADR-0013 each register their measure's kinds, leaf sets, ids and depth, and each provider writes its
  values. The engine adapters map the engine's output onto the same kinds, so the reference implementation and the
  engine are drawn and checked alike.
- The client gains KaTeX, pinned, with its fonts served by the application, not a CDN.
- A measure's result row in the page or in the Pricing DB opens its trace; the history projection (docs/05 §7) holds
  only the root figures.
- Storing a deep trace costs storage per (case, scenario, side, measure). Leaf rows are the bulk, and they are the
  inputs the provider read (trades, CRIF), so they are retained by reference where the Run already retains them.

## OPEN

- Whether the company engines return intermediate terms, and down to which level. The engine team is building the
  output now (owner, 2026-11-07), and the client's format proposal asks twelve questions of it. Until known, a
  provider declares its depth and the page says it.
- Drill-in or a persistent tree of all levels on desktop (put to the owner 2026-11-07 by the SIMM work). A tree in one
  grid needs an ExGrid row toggle and indentation that it does not draw today (it has Row Kind, not a row tree): an
  ex-grid ADR, or a tree drawn beside the grid with the leaf rows still in it.
- Whether an Euler allocation is wanted beside the literal terms (asked 2026-11-07).
