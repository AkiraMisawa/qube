# API and contract starter

All routes and type names here are proposed. Adapt them to an existing host without changing the domain invariants. `contracts/*.schema.json` describes a bounded starter vocabulary; it is not the company analytics wire protocol.

## Endpoint responsibilities

| Endpoint proposal | Purpose and invariants |
|---|---|
| `GET /api/quant/counterparties?query=...` | Fuzzy authorized search; return registration ID plus CP code, legal entity, name and special flag. |
| `POST /api/quant/workspaces` | Create an editable workspace, not a pricing run. |
| `POST /api/quant/workspaces/{id}/prepare` | Resolve a context change; return immutable source snapshot IDs and dependent data; echo preparation generation/context. |
| `POST /api/quant/trade-inputs/import` | Resolve draft booking IDs to immutable TradeInput versions before a Run exists. |
| `PUT /api/quant/workspaces/{id}/draft` | Optional draft persistence with expected server version; no implicit execution. |
| `POST /api/quant/analysis-runs` | Validate/freeze effective input or a fully bound workflow, persist a Run and enqueue required work. Idempotent. |
| `GET /api/quant/analysis-runs/{runId}` | Run/case/step state, typed result references and links. |
| `POST /api/quant/analysis-runs/{runId}/cancellation` | Record cancellation requested; does not imply provider cancellation succeeded. |
| `GET /api/quant/analysis-runs/{runId}/diagnostics` | Authorized manifest and diagnostic bundle, including legacy locations through the same abstraction. |
| `GET /api/quant/data/{id}/rows` | Authorized projection/filter/sort over a fixed dataset revision. |
| `POST /api/quant/rates/subscriptions` | Open/update a live workload; streaming transport separately bound to subscription ID. |
| `POST /api/quant/rates/subscriptions/{id}/capture` | Persist the exact selected complete frame and its input/market refs. |
| `POST /api/quant/definitions` | Save a validated workflow/view version; execution is separate. |
| `POST /api/quant/agent/...` | Adapter-owned AG-UI endpoint using a pinned verified protocol implementation. |
| `POST /api/agent/commands/{capability}/{version}` | A command capability (ADR-0005) as the agent's tool call reaches it: inputs checked against its contracts, an idempotency key, the caller set by the server with origin `ai`; no Run. |
| `POST /api/commands/{capability}/{version}` | The same command as the page reaches it (ADR-0016 §5): the same checks and the same answers (404, 403, 409, else 422), the caller set by the server with origin `pricer`. `entity.set`, `entity.reset`, `entity.remove`, `workspace.undo` and `workspace.redo` (ADR-0016 §3, §11) are what the pricer's page runs through it, with the pricer's own verbs that edit (note 28): `xva.vary`, `xva.trades.add` (`add each`), `xva.cds`, `xva.measures`, `xva.case.remove` and `xva.cases.clear`. The page's endpoints that wrote for those are gone: `/vary`, `/cds` (the write), `/add-trades`, `/cases/remove`, `/cases/clear` and `/measures` under `/api/xva/workspaces/{id}`. Since step 3b (notes 32 and 33) the ten scenario commands (`xva.scenario`, `xva.scenario.include`, `.exclude`, `.remove`, `.duplicate`, `.ladder`, `.cross`, `.load`, `.save` and `xva.scenarios.clear`), `xva.workspace.price`, `xva.refresh` and `xva.pv01.retry` go the same way, and `/scenarios` with everything under it, `/price`, `/refresh` and `/retry` are gone with `GET /api/xva/scenario-sets`; the Scenarios grid's own edits stay `/shifts`. `xva.workspace.price` answers `{ workspaceId, revision, said, runId }` and submits its Run under the request's `idempotencyKey`, which the gateway hands to the handler: the same invocation sent twice is one Run. Its effect, and `xva.pv01.retry`'s, is `execute` (it starts something `undo` does not take back). `xva.refresh` answers `{ workspaceId, revision, said, carried, conflicted, lost, stays }`. Since step 3c (notes 34 to 36) a Rates workspace is reached the same way: the kernel's verbs over `rates.lines` and `rates.workspace` (its defaults), `entity.create` (`new <product> <terms>`; request `{ workspaceId, revision?, product, terms }`, answer `entity`: `{ workspaceId, entity, revision, product, said, defaulted }`), `rates.line.add` (`{ product }`; answers `{ workspaceId, revision, said, line }`), `rates.workspace.price` (`{ marketSnapshotId? }`, the latest where it is left out; answers `{ workspaceId, revision, said, runId, marketSnapshotId }`, one Run under the request's `idempotencyKey`), `rates.frame.capture` (`{ subscriptionId, subscriptionRevision, inputRevision, marketSnapshotId }`, all four or none, none meaning the frame shown as it runs; answers `{ workspaceId, revision, said, runId, marketSnapshotId }`) and `rates.live.start` and `rates.live.stop` (port `workspace`: `{ workspaceId }`; answer `subscription`: `{ workspaceId, subscriptionId, revision, said }`). The page reads a command's output named `answer`, or its one output. Gone with them: `POST /api/rates/workspaces/{id}/lines`, `/lines/new`, `/lines/{line}/remove`, `/defaults`, `/price` and `/live`, `POST /api/rates/live/{id}/capture`, `DELETE /api/rates/live/{id}`, `GET /api/products` and `POST /api/products/{id}/preview`. What stays under `/api/rates`: the market, a workspace's list, creation, view and deletion, a typed cell's edit (`/lines/{line}/edits`), the cashflows and their edits, resolutions, the valuation date, and a subscription's frames (`/live/{id}/frames`). Since step 3d (note 37) `xva.trades.add`'s request may hold `iterations`, a list of `{ add: [ids], without?: [ids] }`: the new trades a case adds together and the existing trades it takes out of After. It is the whole set: the cases of `add each` made before are replaced, and a New row with A ticked that none names is one more case alone. Left out, the command is `add each` as typed. A case of kind `add` names its `iteration` (`{ add, without }`) in the workspace's view and in the input Price freezes, where it named one `trade`. A contract's field may be a list (`type: array` with `items`, of values or of records whose fields are values or lists of values); a list's problem is said at its path (`/inputs/request/iterations/0/add`). |
| `GET /api/catalog` | What the server declares, read once by the client: the capabilities, each command with how it is typed (`command`: its words and arguments, ADR-0016 §4), and apart from them the verbs of the screen the server reads (`viewVerbs`: `verbId`, `title`, `command`; ADR-0016 note 21). A view verb is no capability: nothing invokes it, and the agent's tools hold none. A command's `command` may name the application of the workspaces it acts on (`app`, note 27): the line lists it on that application's pages alone. `shortForms` (`formId`, `title`, `command`; note 29) lists the words that stand for a line of another command; like a view verb, a short form is no capability and no tool of the agent's. |
| `POST /api/commands/preview` | Reads a line as typed for a workspace and executes nothing (ADR-0016 §5, note 15). Request: `{ line, workspaceId, revision?, view? }`, where `view` is what the user was looking at when the line opened (notes 16 to 19): `{ table, subset?, sorts?, order?, keys?, ranges?, focus?, shown? }`, its selection by row keys or by ranges of positions in the order `order` names, `focus` a cell (`{ field, key? }` or `{ field, position? }`), and `shown` the keys of the rows a page that holds every row and filters them shows. Answer (200): the revision read, the command (`id`, `version`), its `inputs` ready to post to the gateway (rows of a screen resolved to their keys, and the table named where the screen gave one the line left out: note 23), each argument as read with where it came `from` (`typed` or `context`), the view in one sentence (`view`, with `viewMoved` true where its positions are of an order that has moved since), the rows (`table`, `title`, `count`, `first`), what would change in one sentence (`would`, and `at` where it speaks of a moment), the line a short form stands for (`expands`, note 29: the command, inputs, rows and `would` are then that line's), a `place` and no command where the line is a view verb's (note 21: `table`, `subset?`, `key`, `position?` in the order the view names with `order`, left out where it names none, `column?`, and `said`, the sentence once the keyboard is there), or `refused` (`code`, `sentence`, the `word` at fault with its `start` and `end` in the line, `candidates`); and what could follow the last word (`complete`: `value`, `start`, `detail`; `more`). A line that cannot be read is an answer, not an error. 400 `InvalidView` for a view that names what the workspace has not or is no view (a range of no row, a selection said two ways), 404 for an unknown workspace, 403 for another user's, 409 where a revision is given and the workspace has moved. |
| `GET /api/workspaces/{id}/tables` | The tables of a workspace with the actions worth an item of a grid's Context Menu (ADR-0016 §4, note 25). Answer (200): `[{ tableId, title, actions: [{ title, line }] }]`, the kernel's `Reset this cell` (`reset`) first on every table that takes it, then the table's own (`Take out of After`, `set selected after off`). A page runs an action's `line` as a line typed, through `POST /api/commands/preview` with the menu's view, and the command it reads. 404 for an unknown workspace, 403 for one that is not the caller's. An action whose line begins with no command's words is the registration's defect and fails the read. |
| `POST /api/workspaces/{id}/selection` | The keys a view's selection names (ADR-0016 note 37), for a page that makes a typed value from a grid the server pages. Request: `{ view }`, the view as the preview takes it. Answer (200): `{ table, keys }`, read as the preview reads `selected` (note 17), and no key where nothing is selected. It writes nothing and runs nothing. 400 for a view that cannot be read (`InvalidView`), 409 where the order the selection was written in has moved (`ViewMoved`) or the table has none (`NoOrder`), 404 for an unknown workspace, 403 for one that is not the caller's. |

Endpoints map transport inputs to application services. Tests call those services and pure domain functions without starting Blazor or MQ. Read-only capability discovery may be tailored to the caller's permissions.

## XVA accepted input

`examples/xva-final-input.json` shows a submitted full effective input subset:

- Context and pinned baseline/market versions.
- Effective CDS/proxy and separate recovery settings.
- NS legality, CSA definitions/selection and scenario definitions.
- Every relevant trade row with immutable TradeInput reference, B/A membership and explicit assignments.
- Calculation options and separately stored user/automatic provenance.

`examples/xva-uncollateralized-input.json` covers the optional-CSA case. An explicit `uncollateralized` selection uses the reserved CSA value `nocsa_{ma id}` (for example `nocsa_MA-001`), with no real collateral agreement required. The initial UI's corresponding main selection uses the same marker; its real CSA collection may be empty. Validate the marker against the trade's effective MA; it is not an unresolved choice or a real CSA master record. The default uncollateralized discount curve is configurable, for example `jpy_ois`, and is resolved into the visible typed row during preparation. Price preserves the final row curve, including any later manual override. A source/config version can be recorded through `defaultsProfileRef`.

When `cvaRecoveryOverride` is null/empty, preserve it as null/empty in the company engine request: analytics automatically uses the applicable CDS recovery already supplied in the fixed credit data. No additional `effectiveCvaRecovery` field is required and no explicit override is synthesized. An explicit numeric zero remains zero. Mapping empty wire representation and selecting the applicable CDS record follow the real analytics contract. This documented fallback is part of the accepted semantics, not an excuse to refetch credit data or change user settings.

In iterative mode, `candidateTradeInstanceIds` is a derived consistency projection of all eligible New rows checked for After, unique and nonempty; it cannot independently omit a checked row. The server verifies this equality before case expansion.

The `synthetic` field in these fixtures is an advisory source-data marker only. Real/mock execution provenance and source origin are determined by the server from actual source/provider records, not trusted from client input. A real engine using synthetic markets is still actual computation on synthetic data.

The example uses explicit rows for clarity. A very large portfolio may instead reference an immutable complete dataset revision plus a fixed resolved selection set. This must be logically equivalent and cannot mean only the viewport or a filter evaluated later against mutable data.

`workspaceRevision` identifies the locally visible accepted edit revision. If draft persistence is enabled, use a separate server ETag/expected-version check; do not assume a client counter alone prevents concurrent writes. Price may atomically persist the submitted complete draft and its Run. A conflict returns a structured error; it does not merge economic fields silently.

`schemaVersion` is the contract version, not an engine version. The server computes the authoritative accepted-input digest using a documented canonical encoding. A raw file SHA-256 is a separate byte-integrity digest. Client digests are consistency hints, not trusted proof. Preserve semantically significant ordering and units.

## Typed reference

Use a data ID, immutable revision and explicit contract identifier/version. Resolve contents through authorized services. `dataId` is not a file path, and knowledge of it grants no access. For artifacts, preserve actual media type, schema/format version, byte digest and producing invocation. Small typed values may be embedded instead of stored separately.

## Workflow starter vocabulary

`examples/simm-comparison.input.json` supplies the resolved case/MA/CSA/curve and fixed source bindings used by the Workflow. Its domain-specific schema is separate from the generic composition schema. A real source adapter resolves these example references; this bundle contains no company snapshots.

The example workflow uses `input`, `context`, `constant`, `output` and `item` bindings. `forEach` maps a step over a typed collection with bounded concurrency. Every mapped output is lifted to `Outcome<T>[]`: each envelope contains a stable `itemKey`, status, domain identity metadata, and either a value or an error. Consumers cannot infer identity from completion order.

With `itemMode: value`, the source is a plain input collection and `itemKeyPath` supplies its unique stable expansion key. The CRIF step uses this mode. With `itemMode: outcomeValue`, the source is a prior mapped output; the runtime binds `item` to the successful envelope's `value`. Failed/blocked upstream envelopes do not invoke the downstream capability; they produce a blocked outcome with the same item identity. The SIMM step uses this mode. The comparison step consumes the complete `qube.simm-side-outcome` collection plus the original planned comparison, so cases with both sides failed remain present with unavailable values. `collect` does not mean errors disappear.

The runtime records expanded invocation keys such as `crif/case-A/before` and `simm/case-A/before`, distinct from the definition's `stepId`. Their construction is stable across retries. In the SQL sketch, `step_key` stores this expanded key, so map iterations do not collide under the per-Run uniqueness constraint.

`dependsOn` is explicit and must agree with output bindings. `outputs` names stable workflow output ports. Bindings use JSON Pointer paths within declared contracts; no executable expressions are permitted.

Generic dataset schemas contain column/row structure and generic execution/data-origin provenance. Domain basis metadata is a versioned extension; the SIMM fixture's value date, market, methodology and currency live there. The generic kernel does not interpret those financial fields.

The sample CRIF generation occurs per prepared portfolio side. A real domain adapter may compute deltas or reuse common sensitivities if mathematically valid and represented by compatible lineage; this optimization is not assumed by the sample.

## Numerical result semantics

Return values together with currency/unit, measure definition, status, provider ID/version, engine/model/config version, value date, data versions and input/result references. A numeric null with failure/unavailable status is not zero. Compare results only when their bases are compatible.

The app's agreed CVA convention is `CVA <= 0`, with `V_hat = V + CVA`; movement is `after - before`. A trader charge or charge ratio is a separately named business measure, not an implicit sign reversal. Rates results distinguish PV, par rate and fair spread with target leg/convention.

## Common error envelope

Proposed fields: `code`, `message`, `fieldErrors`, `retryable`, `operationId`, `detailsRef`. Useful codes include `ValidationFailed`, `ContextChanged`, `SourceVersionUnavailable`, `UnsupportedProduct`, `UnsupportedOption`, `ProviderUnavailable`, `PermissionDenied`, `IdempotencyConflict` and `ExecutionOutcomeUnknown`.

Do not put raw company credentials, connection strings or unfiltered engine dumps in end-user errors. Authorized diagnostic artifacts may contain detailed engine messages.

## Versioning

Pin Capability contract, Workflow definition, View definition, provider/engine configuration and source data revisions per execution. Additive schema changes need compatibility tests; changed financial meaning requires a new semantic version or explicit migration. Existing Runs are not edited in place when adapters change.

## Files versus production compatibility

The JSON Schemas verify shape and limited enumerations. Cross-reference resolution, catalog compatibility, B/A case construction, authorization, curve/CSA compatibility and economic semantics require additional validators. `storage.sql` is an illustrative local persistence starting point. No example claims to reproduce company ZIP/CSV/XML formats.
