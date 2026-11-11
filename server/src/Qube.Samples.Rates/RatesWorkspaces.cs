using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Rates;

/// <summary>
/// The market the server publishes (docs/07 §7): a synthetic market advanced one snapshot at a time. A ticker
/// calls <see cref="AdvanceAsync"/> on a clock; tests call it directly. Snapshots are kept for the process, so
/// every identifier a frame or a Run names stays resolvable while it runs.
/// </summary>
public sealed class MarketFeed(SyntheticMarket market, RatesLiveService live)
{
    readonly Lock gate = new();
    long next;

    public MarketSnapshot? Latest { get; private set; }

    public MarketSnapshot? Resolve(string id) => market.Resolve(id);

    public async Task<MarketSnapshot> AdvanceAsync()
    {
        MarketSnapshot snapshot;
        lock (gate)
        {
            snapshot = market.Snapshot(next++);
            Latest = snapshot;
        }
        // Each tick also stops what nobody has watched for the idle limit.
        live.SweepIdle();
        await live.PublishAsync(snapshot.Update);
        return snapshot;
    }
}

/// <summary>A line as the grid shows it: its fields, which of them the user set, and why it is not priceable.</summary>
public sealed record LineView(
    string Line,
    IReadOnlyDictionary<string, JsonNode?> Fields,
    IReadOnlyList<string> UserFields,
    bool Priceable,
    int? ParLeg,
    DateOnly? Spot,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Held,
    IReadOnlyDictionary<string, string> Defaulted);

/// <summary>A Rates workspace's defaults for new lines (ADR-0008 §4b), set by its user. A null is no default.</summary>
public sealed record LineDefaults(string? Currency, double? Notional, string? Direction)
{
    public static LineDefaults Of(WorkspaceState state) => state.Context["defaults"] is JsonObject d
        ? new(d["currency"]?.GetValue<string>(), d["notional"] is { } n ? RatesMaterialiser.Number(n) : null, d["direction"]?.GetValue<string>())
        : new(null, null, null);

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (!string.IsNullOrEmpty(Currency)) json["currency"] = Currency;
        if (Notional is { } n) json["notional"] = n;
        if (!string.IsNullOrEmpty(Direction)) json["direction"] = Direction;
        return json;
    }
}

/// <summary>One period of a line's cashflows, for the detail panel; the user's edited fields are named.</summary>
public sealed record PeriodView(
    string Entity,
    int Leg,
    DateOnly AccrualStart,
    DateOnly AccrualEnd,
    DateOnly PaymentDate,
    double Notional,
    double? Rate,
    double YearFraction,
    IReadOnlyList<string> UserFields);

public sealed record ExchangeView(int Leg, ExchangeKind Kind, DateOnly PaymentDate, double Amount, string Currency);

public sealed record CashflowsView(string Line, long Revision, IReadOnlyList<PeriodView> Periods, IReadOnlyList<ExchangeView> Exchanges);

/// <summary>An edit held back for the user, as in the XVA page: a conflict with the schedule's new values, or a lost period.</summary>
public sealed record RatesHeldEdit(string Scope, string Entity, string Field, JsonNode? Value, IReadOnlyDictionary<string, JsonNode?>? Source, bool Acknowledged);

public sealed record RatesRunLink(string RunId, long Revision);

public sealed record RatesWorkspaceView(
    string WorkspaceId,
    long Revision,
    DateOnly ValuationDate,
    LineDefaults Defaults,
    IReadOnlyList<LineView> Lines,
    IReadOnlyList<RatesHeldEdit> Conflicts,
    IReadOnlyList<RatesHeldEdit> Lost,
    CarrySummary? Rederived,
    IReadOnlyList<RatesRunLink> Runs,
    LiveStatus? Live);

/// <summary>Why a line could not be created from the terms given: every reason, and nothing was written.</summary>
public sealed class LineTermsRefused(IReadOnlyList<string> issues) : Exception(string.Join(" ", issues))
{
    public IReadOnlyList<string> Issues { get; } = issues;
}

/// <summary>The frame a capture names: what the user saw, so a newer frame is never saved under it (spine 6).</summary>
public sealed record FrameIdentity(int SubscriptionRevision, long InputRevision, string MarketSnapshotId);

/// <summary>What Price would price (ADR-0016 note 36): the market snapshot, the lines priced on it and the lines left out.</summary>
public sealed record RatesPricePlan(string MarketSnapshotId, IReadOnlyList<string> Priced, IReadOnlyList<string> LeftOut);

/// <summary>
/// The Rates workspace application service: what the endpoints call (docs/07 §4's coordinator). It names lines and
/// checks types. The generic kernel stores and carries the edits; the module derives the schedules, and a running
/// live subscription is told of each accepted revision by the workspace's observer (<see cref="RatesLiveObserver"/>).
/// </summary>
public sealed class RatesWorkspaces(WorkspaceService workspaces, IDataStore store, RunService runs, RatesLiveService live, MarketFeed market,
    ConventionRegistry conventions, IRatesPricingProvider provider)
{
    /// <summary>The source a Rates workspace acquires: none; everything priced is created or derived in it (ADR-0004).</summary>
    public static readonly ISourceVersion NoAcquiredSource = new EmptySource("rates:none");

    public static ISourceVersion Resolve(JsonObject descriptor) => NoAcquiredSource;

    public WorkspaceState Create(string user, DateOnly valuationDate, RunOrigin origin = RunOrigin.Pricer) =>
        workspaces.Create(user, RatesEditRules.App, Context(valuationDate), NoAcquiredSource, origin);

    public IReadOnlyList<WorkspaceState> OwnedBy(string user) => [.. workspaces.OwnedBy(user).Where(w => w.App == RatesEditRules.App)];

    public RatesWorkspaceView View(string workspaceId, string user)
    {
        var state = Owned(workspaceId, user);
        var links = runs.List(new RunQuery(user, 0, 20, workspaceId)).Runs
            .Select(r => new RatesRunLink(r.RunId, r.Relations.WorkspaceRevision ?? throw new InvalidOperationException($"Run {r.RunId} names a workspace but no revision.")))
            .ToList();
        var lines = RatesMaterialiser.Lines(state, conventions).Select(l => new LineView(l.Line, l.Fields, l.UserFields, l.Priceable, l.ParLeg, l.Spot,
            [.. l.Legs.Select(g => $"{g.Profile.Id}@{g.Profile.Version}")], l.Issues, l.Held,
            // A defaulted field the user has since set is theirs, not a default.
            (state.Created[l.Line].Sources ?? new Dictionary<string, string>()).Where(p => !l.UserFields.Contains(p.Key)).ToDictionary())).ToList();
        return new(state.WorkspaceId, state.Revision, RatesEditRules.ValuationDate(state), LineDefaults.Of(state), lines,
            [.. state.Conflicts.Values.OrderBy(c => c.Override.Seq).Select(c => new RatesHeldEdit(c.Override.Target.Scope, c.Override.Target.Entity, c.Override.Target.Field, c.Override.Value, c.Source, false))],
            [.. state.Lost.Values.OrderBy(l => l.Override.Seq).Select(l => new RatesHeldEdit(l.Override.Target.Scope, l.Override.Target.Entity, l.Override.Target.Field, l.Override.Value, null, l.Acknowledged))],
            state.Rederived, links, live.StatusOf(user, workspaceId));
    }

    public CashflowsView Cashflows(string workspaceId, string user, string line)
    {
        var state = Owned(workspaceId, user);
        if (!state.Created.ContainsKey(line)) throw new WorkspaceRefused("UnknownEntity", $"No line {line}.");
        var effective = RatesMaterialiser.Line(state, line, RatesEditRules.ValuationDate(state), conventions);
        return new(line, state.Revision,
            [.. effective.Legs.SelectMany((leg, i) => leg.Periods.Select(p =>
                new PeriodView(p.Entity, i + 1, p.AccrualStart, p.AccrualEnd, p.PaymentDate, p.Notional, p.Rate, p.YearFraction, p.UserFields)))],
            [.. effective.Legs.SelectMany((leg, i) => leg.Exchanges.Select(e => new ExchangeView(i + 1, e.Kind, e.PaymentDate, e.Amount, leg.Terms.Currency)))]);
    }

    /// <summary>Adds a line of a product with every field empty but its kind, named by the revision that creates it.</summary>
    public (WorkspaceState State, string Line) AddLine(string workspaceId, long revision, string user, RatesProduct product, RunOrigin origin = RunOrigin.Pricer)
    {
        Owned(workspaceId, user);
        var line = LineAt(revision);
        return (workspaces.Create(workspaceId, revision, user, RatesEditRules.LinesScope, line, LineFields.Template(product), origin), line);
    }

    /// <summary>The line a command at a revision makes: named by the revision that makes it, so no two are named alike.</summary>
    public static string LineAt(long revision) => $"L{revision + 1}";

    /// <summary>
    /// The line <see cref="CreateLine"/> would make of the terms on a workspace as it stands, once they are checked as
    /// it checks them (ADR-0016 note 36). Writes nothing.
    /// </summary>
    public string WouldCreateLine(WorkspaceState state, JsonObject terms)
    {
        LineFrom(terms, RatesEditRules.ValuationDate(state));
        return LineAt(state.Revision);
    }

    /// <summary>
    /// Creates one line from its terms in one edit (ADR-0005), at the workspace's current revision. The terms are read
    /// and resolved as the row would be; anything that would leave the line unpriceable refuses the whole command, with
    /// every reason, before anything is written. Nothing the caller did not give is filled in: the product decides only
    /// which legs float, as a line added on the page does, and an empty quote is the leg solved at par (C40).
    /// </summary>
    public (WorkspaceState State, string Line) CreateLine(string workspaceId, string user, JsonObject terms, RunOrigin origin, long? expectedRevision = null,
        IReadOnlyDictionary<string, string>? sources = null)
    {
        var state = Owned(workspaceId, user);
        // The page names the revision it saw, as every page edit does; a command acts on the current one.
        var revision = expectedRevision ?? state.Revision;
        var line = LineAt(revision);
        var fields = LineFrom(terms, RatesEditRules.ValuationDate(state));
        return (workspaces.Create(workspaceId, revision, user, RatesEditRules.LinesScope, line, fields, origin, sources), line);
    }

    /// <summary>A line's fields from its terms, refused with every reason it could not be priced at the valuation date.</summary>
    public JsonObject LineFrom(JsonObject terms, DateOnly valuationDate)
    {
        var product = terms[LineFields.Product] is JsonValue p && p.TryGetValue<string>(out var text) && Enum.TryParse<RatesProduct>(text, true, out var parsed)
            ? parsed : throw new LineTermsRefused(["The product is irs or ccs."]);
        var fields = LineFields.Template(product);
        var unknown = terms.Select(t => t.Key).Where(k => !LineFields.All.Contains(k) || k.EndsWith(".floating", StringComparison.Ordinal)).ToList();
        if (unknown.Count > 0) throw new LineTermsRefused([.. unknown.Select(k => $"{k} is not a term a line is created with.")]);
        foreach (var (name, value) in terms.Where(t => t.Key != LineFields.Product))
        {
            if (value is not null and not JsonValue) throw new LineTermsRefused([$"{name} takes a single value."]);
            fields[name] = value?.DeepClone();
        }
        var (read, issues) = LineReader.Read("new", fields.ToDictionary(f => f.Key, f => f.Value));
        if (read is null) throw new LineTermsRefused(issues);
        var resolution = LineResolver.Resolve(read, valuationDate, conventions);
        if (!resolution.Priceable) throw new LineTermsRefused(resolution.Issues);
        return fields;
    }

    public WorkspaceState EditLine(string workspaceId, long revision, string user, string line, string field, JsonNode? value)
    {
        var state = Owned(workspaceId, user);
        if (!state.Created.ContainsKey(line)) throw new WorkspaceRefused("UnknownEntity", $"No line {line}.");
        if (!LineFields.All.Contains(field) || field == LineFields.Product)
            throw new WorkspaceRefused("NotEditable", $"{field} is not an editable line field.");
        var target = new EditTarget(RatesEditRules.LinesScope, line, field);
        // Types only: whether the terms make a line is the resolver's, and shows on the row.
        if (value is not null and not JsonValue)
            throw new WorkspaceRefused("InvalidValue", $"{field} takes a single value.", [target]);
        return workspaces.Set(workspaceId, revision, user, target, value, RunOrigin.Pricer);
    }

    public WorkspaceState EditCashflow(string workspaceId, long revision, string user, string line, string period, string field, JsonNode? value)
    {
        var state = Owned(workspaceId, user);
        var scope = RatesEditRules.SchedulePrefix + line;
        if (!state.Derived.TryGetValue(scope, out var schedule) || schedule.Read(period, field) is null)
            throw new WorkspaceRefused("UnknownEntity", $"{line} has no period {period} with a {field}.");
        var target = new EditTarget(scope, period, field);
        string? problem = field switch
        {
            "accrualStart" or "accrualEnd" or "paymentDate" =>
                value is JsonValue d && d.TryGetValue<string>(out var text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? null : "A date is written yyyy-MM-dd.",
            "notional" => value is JsonValue n && n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? null : "A notional is a number.",
            "rate" => value is null || value is JsonValue r && r.GetValueKind() == System.Text.Json.JsonValueKind.Number ? null : "A rate is a number or empty.",
            _ => $"{field} is not an editable cashflow field.",
        };
        if (problem is not null) throw new WorkspaceRefused("InvalidValue", problem, [target]);
        return workspaces.Set(workspaceId, revision, user, target, value, RunOrigin.Pricer);
    }

    public WorkspaceState Resolve(string workspaceId, long revision, string user, string scope, string entity, string field, string resolution)
    {
        var state = Owned(workspaceId, user);
        var target = new EditTarget(scope, entity, field);
        if (!state.Overrides.ContainsKey(target) && !state.Conflicts.ContainsKey(target) && !state.Lost.ContainsKey(target))
            throw new WorkspaceRefused("NotOverridden", $"{entity}.{field} has no edit.");
        return resolution switch
        {
            "reset" => workspaces.Reset(workspaceId, revision, user, target),
            "keep" => workspaces.KeepOverride(workspaceId, revision, user, target),
            "takeSource" => workspaces.TakeSource(workspaceId, revision, user, target),
            "acknowledge" => workspaces.AcknowledgeLost(workspaceId, revision, user, target),
            _ => throw new WorkspaceRefused("UnknownResolution", $"{resolution} is not reset, keep, takeSource or acknowledge."),
        };
    }

    /// <summary>Moves the grid's valuation date. Every schedule is derived again, and its cashflow edits carry or are held.</summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeValuationDate(string workspaceId, long revision, string user, DateOnly valuationDate)
    {
        var current = Owned(workspaceId, user);
        var (state, _) = workspaces.Reacquire(workspaceId, revision, user, Context(valuationDate, current.Context["defaults"]?.AsObject()), NoAcquiredSource);
        // No summary is the kernel's word for no schedule having moved: no edit was carried, held or lost, which the zeros say.
        return (state, state.Rederived ?? new CarrySummary(0, 0, 0));
    }

    /// <summary>
    /// What Price would price at a revision (ADR-0016 note 36): the market snapshot, the latest where none is named, the
    /// lines priced and the lines left out. It reads what <see cref="Price"/> freezes and keeps and submits nothing, and
    /// refuses what Price would refuse.
    /// </summary>
    public RatesPricePlan PlanPrice(string workspaceId, long revision, string user, string? marketSnapshotId)
    {
        var (snapshot, _, input) = Frozen(workspaceId, revision, user, marketSnapshotId);
        return new(snapshot.Id, [.. input.Lines.Select(l => l.RowKey)], [.. input.NotPriced.Select(n => n.Line)]);
    }

    /// <summary>
    /// Price once (docs/07 §3): freezes the revision the user saw and prices its priceable lines against the market
    /// snapshot named; the lines left out are listed in the result with why. Refused when nothing is priceable. With no
    /// snapshot named, as a caller that read none may, it is the latest then, which the Run's input names; a snapshot
    /// named and not resolved is refused, never replaced.
    /// </summary>
    public SubmissionResult Price(string workspaceId, long revision, string user, string idempotencyKey, string? marketSnapshotId, RunOrigin origin = RunOrigin.Pricer)
    {
        var (snapshot, freeze, input) = Frozen(workspaceId, revision, user, marketSnapshotId);
        var accepted = RatesAcceptedInput.Build(freeze.WorkspaceId, freeze.Revision, freeze.Context, snapshot.Update, input, freeze.Overrides);
        var reference = store.Put($"rates-input-{freeze.WorkspaceId}-r{freeze.Revision}-{Sanitise(snapshot.Id)}", RatesContracts.AcceptedInput, RatesContracts.Version, accepted);
        return runs.Submit(new RunSubmission(RatesModule.Price(reference), freeze.Context,
            new JsonObject { ["workspaceId"] = freeze.WorkspaceId, ["revision"] = freeze.Revision, ["marketSnapshotId"] = snapshot.Id },
            idempotencyKey, user, origin, new RunRelations(freeze.WorkspaceId, freeze.Revision)));
    }

    // The revision frozen for Price with its market and what of it is priced, or the refusal Price gives.
    (MarketSnapshot Snapshot, WorkspaceFreeze Freeze, LiveInput Input) Frozen(string workspaceId, long revision, string user, string? marketSnapshotId)
    {
        Owned(workspaceId, user);
        var snapshot = marketSnapshotId is null
            ? market.Latest ?? throw new WorkspaceRefused("NoMarket", "No market snapshot has been published yet; price waits for one.")
            : market.Resolve(marketSnapshotId) ?? throw new WorkspaceRefused("UnknownMarket", $"Market snapshot {marketSnapshotId} cannot be resolved.");
        LiveInput? input = null;
        var freeze = workspaces.Freeze(workspaceId, revision, state =>
        {
            input = RatesAcceptedInput.Of(state, conventions, provider);
            var priced = input.Lines.Select(l => l.RowKey).ToHashSet();
            return entity => priced.Contains(LineOf(entity));
        });
        if (input!.Lines.Count == 0)
            throw new WorkspaceRefused("NothingToPrice", "No line in this workspace can be priced.");
        return (snapshot, freeze, input);
    }

    /// <summary>The revision the caller's workspace holds: what a command that names none acts on.</summary>
    public long Revision(string workspaceId, string user) => Owned(workspaceId, user).Revision;

    /// <summary>How many lines of a workspace as it stands a calculation prices: the rest are left out, each with why.</summary>
    public int Priced(WorkspaceState state) => RatesAcceptedInput.Of(state, conventions, provider).Lines.Count;

    /// <summary>The caller's workspace's live state: its running subscription, or the last one stopped; null when it never ran.</summary>
    public LiveStatus? LiveOn(string workspaceId, string user)
    {
        Owned(workspaceId, user);
        return live.StatusOf(user, workspaceId);
    }

    /// <summary>Starts ticking the workspace's current revision. Later edits move it to their revision.</summary>
    public string StartLive(string workspaceId, string user, RunOrigin origin = RunOrigin.Pricer)
    {
        var state = Owned(workspaceId, user);
        return live.Start(user, workspaceId, RatesAcceptedInput.Of(state, conventions, provider), origin);
    }

    /// <summary>Stops the workspace's running subscription, for every page watching it, and answers which it was.</summary>
    public string StopLiveOn(string workspaceId, string user, RunOrigin origin)
    {
        var status = RunningOn(workspaceId, user);
        live.Stop(user, status.SubscriptionId, LiveStopReason.User, origin);
        return status.SubscriptionId;
    }

    /// <summary>The workspace's running subscription, which <c>live stop</c> stops; refused where none runs.</summary>
    public LiveStatus RunningOn(string workspaceId, string user) => Running(workspaceId, user, "Live is not running on this workspace.");

    /// <summary>
    /// The frame the workspace's running subscription shows now (ADR-0016 note 36): what a capture that names no frame
    /// saves, and what a reading of <c>capture</c> names. Refused where no subscription runs, and before its first frame.
    /// </summary>
    public ShownFrame ShownOn(string workspaceId, string user)
    {
        const string notRunning = "Live is not running: capture saves a frame a running subscription shows.";
        var status = Running(workspaceId, user, notRunning);
        try
        {
            return live.Shown(user, status.SubscriptionId) ?? throw new WorkspaceRefused("NoFrame", "The subscription has shown no frame yet.");
        }
        catch (KeyNotFoundException)
        {
            // It stopped between the two reads: the refusal is the one a read a moment later gives.
            throw new WorkspaceRefused("NotRunning", notRunning);
        }
    }

    LiveStatus Running(string workspaceId, string user, string otherwise) =>
        LiveOn(workspaceId, user) is { Running: true } status ? status : throw new WorkspaceRefused("NotRunning", otherwise);

    /// <summary>
    /// Captures the frame the user saw as a Run (docs/07 §3): the frame the capture names, with its own input and
    /// market, never a newer frame or a newer edit relabelled as it. A newer frame shown meanwhile changes nothing
    /// (spine 6); a frame the subscription no longer keeps (<see cref="RatesLiveService.KeptFrames"/>) is refused, and
    /// so is a subscription that is not the workspace's.
    /// </summary>
    public SubmissionResult Capture(string workspaceId, string subscriptionId, string user, FrameIdentity seen, string idempotencyKey, RunOrigin origin = RunOrigin.Pricer)
    {
        Owned(workspaceId, user);
        if (live.WorkspaceOf(user, subscriptionId) != workspaceId)
            throw new WorkspaceRefused("UnknownSubscription", $"{subscriptionId} is not a live subscription of workspace {workspaceId}.");
        if (live.Shown(user, subscriptionId) is null) throw new WorkspaceRefused("NoFrame", "The subscription has shown no frame yet.");
        var shown = live.Kept(user, subscriptionId, seen.SubscriptionRevision, seen.InputRevision, seen.MarketSnapshotId)
            ?? throw new WorkspaceRefused("FrameNotKept",
                $"The frame captured ({seen.MarketSnapshotId} at revision {seen.InputRevision}) is not one of the last {RatesLiveService.KeptFrames} this subscription showed. Capture the frame on screen now.");
        var d = shown.Delivery;
        var context = Context(shown.Input.ValuationDate);
        var accepted = RatesAcceptedInput.Build(workspaceId, d.InputRevision, context, d.Market, shown.Input, null);
        var key = $"{workspaceId}-r{d.InputRevision}-{Sanitise(d.Market.SnapshotId)}-{subscriptionId}-s{d.SubscriptionRevision}";
        var inputRef = store.Put($"rates-capture-input-{key}", RatesContracts.AcceptedInput, RatesContracts.Version, accepted);
        var (lines, risk) = RatesResults.Datasets(new RatesBatchResult(d.Provider, d.CalculationMode, d.Rows), shown.Input.Lines, shown.Input.NotPriced, d.Market,
            $"{subscriptionId}/s{d.SubscriptionRevision}/{d.Market.SnapshotId}");
        var frameRef = store.Put($"rates-capture-frame-{key}", RatesContracts.LineResult, RatesContracts.Version, lines);
        var riskRef = store.Put($"rates-capture-risk-{key}", RatesContracts.Risk, RatesContracts.Version, risk);
        return runs.Submit(new RunSubmission(RatesModule.Capture(inputRef, frameRef, riskRef), context,
            new JsonObject
            {
                ["workspaceId"] = workspaceId, ["revision"] = d.InputRevision, ["marketSnapshotId"] = d.Market.SnapshotId,
                ["subscriptionId"] = subscriptionId, ["subscriptionRevision"] = d.SubscriptionRevision,
            },
            idempotencyKey, user, origin, new RunRelations(workspaceId, d.InputRevision)));
    }

    public void Delete(string workspaceId, string user)
    {
        Owned(workspaceId, user);
        foreach (var (owner, id) in live.OnWorkspace(workspaceId)) live.Stop(owner, id, LiveStopReason.Deleted);
        workspaces.Delete(workspaceId, user);
    }

    WorkspaceState Owned(string workspaceId, string user)
    {
        var state = workspaces.Find(workspaceId);
        if (state is null || state.App != RatesEditRules.App) throw new WorkspaceRefused("UnknownWorkspace", $"No Rates workspace {workspaceId}.");
        if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
        return state;
    }

    /// <summary>The line an entity belongs to: a line itself, or a period named <c>{line}/leg{n}/…</c>.</summary>
    static string LineOf(string entity) => entity.Split('/')[0];

    static string Sanitise(string id) => id.Replace(':', '-');

    static JsonObject Context(DateOnly valuationDate, JsonObject? defaults = null)
    {
        var context = new JsonObject { ["valuationDate"] = valuationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (defaults is { Count: > 0 }) context["defaults"] = defaults.DeepClone();
        return context;
    }
}
