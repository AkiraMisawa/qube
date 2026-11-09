using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Portfolio;

// The measures register (ADR-0010 §10): a measure is a figure family a Price can be asked for, registered by the domain
// module that owns it. The page and this module know no measure's rules; they read what the owner registered.

/// <summary>
/// One figure a measure reports, such as CVA's CVA Charge. <paramref name="Term"/> is the term of the measure's trace its
/// Metrics cell opens, where the trace has several roots, as spot IM's IM-R opens at <c>collect</c> (ADR-0010 §7's note
/// of 2026-11-08); none opens the trace at its roots.
/// </summary>
public sealed record MetricDefinition(string Id, string Title, string? Term = null);

/// <summary>
/// A measure as its module registers it. <paramref name="Headline"/> is the metric the collapsed result line shows.
/// <paramref name="DeclaresMembership"/> says whether it prices only some trades and says per trade why (FVA's rule is the
/// first, ADR-0010 §5); <paramref name="Traced"/> whether a Metrics cell opens its calculation trace (ADR-0011);
/// <paramref name="Quoted"/> whether a Run that priced it has a quote to copy (XVA's Copy quote, from CVA, §7, §11);
/// <paramref name="Tabs"/> the result tabs it draws beside Metrics (§7, §10), none where it has no tab of its own.
/// </summary>
public sealed record MeasureDefinition(string Id, string Title, IReadOnlyList<MetricDefinition> Metrics, string Headline, bool DeclaresMembership, bool Traced,
    bool Quoted = false, IReadOnlyList<ResultTab>? Tabs = null);

/// <summary>
/// A result tab a measure registers beside Metrics (ADR-0010 §7, §10), such as spot IM's <c>SIMM</c>. <paramref name="View"/>
/// names the approved view that draws it from the Run: <c>trace</c> draws the measure's calculation trace, a card per
/// root above its drill-down (ADR-0011 §6, ADR-0012 §7); <c>risks</c> its deltas and <c>profile</c> the profiles the Run
/// returned (the owner's Risks and Profile of 2026-11-08). A tab several measures register, by one id, is one tab drawn
/// for all of them. A Metrics cell of a traced measure opens its tab at its term.
/// </summary>
public sealed record ResultTab(string Id, string Title, string View)
{
    /// <summary>The view that draws a measure's calculation trace: a card per root, then the drill-down.</summary>
    public const string Trace = "trace";
    /// <summary>The view that draws a measure's deltas from the Run's <c>portfolio.risks</c> rows, for the case and market in view.</summary>
    public const string Risks = "risks";
    /// <summary>The view that draws the Run's <c>portfolio.profiles</c> rows as a table and a chart, for the case and market in view.</summary>
    public const string Profile = "profile";
}

/// <summary>
/// A field of a shared input tab that a measure needs (ADR-0010 §10), such as CVA's recovery on Netting: it shows while a
/// selected measure needs it, and a hidden one keeps its changes. <paramref name="Tab"/> names the tab, as the page's
/// tabs are named (<c>netting</c>, <c>csa</c>), and <paramref name="Field"/> the field as its edits name it. The trade
/// grids are <c>trades</c>, where a field is every trade's, as spot IM's IM-R; a tab of the measure's own is named by it,
/// as spot IM's <c>im</c> (ADR-0010 §10's note of 2026-11-08).
/// </summary>
public sealed record MeasureInput(string Tab, string Field, string Measure);

/// <summary>
/// Whether a measure prices a trade, and why (ADR-0010 §5, §10). Out is never silent: it says why. A measure that
/// declares named scopes gives each, in or out with its own reason, and its own reason is then their compact form, as spot
/// IM's `R: JFSA, CFTC · P: JFSA` (ADR-0012 §2); the trade is in when any scope holds it.
/// </summary>
public sealed record Membership(bool In, string Reason, IReadOnlyList<MembershipScope>? Scopes = null);

/// <summary>One named scope of a measure's membership, such as spot IM's <c>collect/JFSA</c>, titled as the page shows it.</summary>
public sealed record MembershipScope(string Id, string Title, bool In, string Reason);

/// <summary>
/// A measure's membership rule over one state (ADR-0010 §10): which trades it prices and why, decided on the server
/// once. Its version is frozen into the Run. Where the rule reads a CSA's terms (FVA's, §5), it says so per CSA too.
/// </summary>
public interface IMembershipRule
{
    string Measure { get; }
    string Version { get; }
    Membership Of(EffectiveTrade trade);
    /// <summary>The CSA's own standing under the rule, or null where the rule does not read CSAs.</summary>
    Membership? Of(EffectiveCsa csa);
}

/// <summary>Why one measure cannot be priced as the workspace stands. It blocks that measure alone (ADR-0010 §10).</summary>
public sealed record MeasureRefusal(string Measure, string Code, string Message);

/// <summary>
/// A column a module adds to the trade grids (ADR-0010 §10), such as XVA's PV01. On a trade its field is
/// <c>{module}.{id}</c>, which no source holds: a module's input on a trade starts empty. <paramref name="Editable"/>
/// says whether it is ever typed; whether it may be now is its cell's.
/// </summary>
public sealed record TradeColumn(string Id, string Title, string Type, bool Editable);

/// <summary>A module's trade column as the page shows it: its field on a trade, and the module that owns it.</summary>
public sealed record RegisteredColumn(string Field, string Title, string Type, bool Editable, string Module);

/// <summary>
/// A module column's cell on one trade (ADR-0010 §6): its value, or none while it is <c>pending</c> or after it
/// <c>failed</c> (never 0 for either); a note saying where the value came from or why there is none; and whether the
/// user may type it now. A cell a rule fills, such as PV Adj from Margin, is read-only and says so in its note.
/// </summary>
public sealed record TradeCell(JsonNode? Value, string State, string? Note, bool Editable)
{
    public const string Shown = "value";
    public const string Pending = "pending";
    public const string Failed = "failed";

    public static TradeCell Input(JsonNode? value) => new(value, Shown, null, true);
}

/// <summary>
/// A measure in the workspace view: its definition, the module that owns it, and the module whose request prices it,
/// whose step's result holds its rows (ADR-0010 §10).
/// </summary>
public sealed record RegisteredMeasure(string Id, string Title, string Module, IReadOnlyList<MetricDefinition> Metrics, string Headline, bool DeclaresMembership, bool Traced,
    bool Quoted, string Request, IReadOnlyList<ResultTab> Tabs);

/// <summary>
/// A setting a module registers (ADR-0010 §8, §10), as it registers its trade columns: a model input the user sets
/// before a quote, such as XVA's cost of capital. <paramref name="Form"/> is how it is written and read, as a cell's;
/// <paramref name="Default"/> the value it has until set, none where empty means the model's own, which
/// <paramref name="Empty"/> then says; <paramref name="Measures"/> the measures it serves, which it shows beside and
/// only while one of them is selected; <paramref name="Names"/> the names a cell is named by after the module's row
/// (`set xva cost of capital 12%`), its title's words first. Its value is an edit on <c>settings:&lt;module&gt;</c>, the
/// workspace's own, so it survives a counterparty change.
/// </summary>
public sealed record MeasureSetting(string Id, string Title, CellName.Form Form, JsonNode? Default, IReadOnlyList<string> Measures, string? Empty = null,
    IReadOnlyList<string>? Names = null);

/// <summary>One setting in the workspace view: its value as the reading holds it and as written, its default, and whether it differs.</summary>
public sealed record SettingView(string Id, string Title, CellName.Form Form, JsonNode? Value, string Text, JsonNode? Default, string DefaultText, string? Empty,
    IReadOnlyList<string> Measures, bool Changed);

/// <summary>A module's settings in the workspace view (the Settings tab's section of it): those a selected measure needs.</summary>
public sealed record ModuleSettings(string Module, string Title, IReadOnlyList<SettingView> Settings);

/// <summary>A domain module's registration into the pricer (ADR-0010 §10). XVA's is the first (§11).</summary>
public interface IMeasureModule
{
    /// <summary>The module's id, such as <c>xva</c>; it names the module's part of the accepted input and its Run output.</summary>
    string Id { get; }

    /// <summary>
    /// The module whose request prices this module's measures: which request computes a measure is the provider's,
    /// not the page's (ADR-0010 §10). Its own unless another module's provider returns them, as XVA's KVA option
    /// returns spot CCR capital (ADR-0013 §2); that request then reads this module's part of the accepted input, and
    /// this module's step is never asked for.
    /// </summary>
    string PricedBy => Id;

    /// <summary>Its title, as the Settings tab heads its section: its id in capitals unless it says.</summary>
    string Title => Id.ToUpperInvariant();

    /// <summary>The measures it owns, in the order the page shows them.</summary>
    IReadOnlyList<MeasureDefinition> Measures { get; }

    /// <summary>The settings it reads in its input part (ADR-0010 §8, §10), in the order the Settings tab shows them; none unless it says.</summary>
    IReadOnlyList<MeasureSetting> Settings => [];

    /// <summary>Why a value cannot be one of its settings, by its type and bounds; null when it can.</summary>
    string? CheckSetting(string setting, JsonNode? value) => $"{Id} has no setting {setting}.";

    /// <summary>
    /// Whether its figures come from a simulated provider, which the page says (`SIMULATED`, ADR-0010 §1). A module
    /// that does not say is shown simulated: a figure is never presented as a real provider's by default.
    /// </summary>
    bool Simulated => true;

    /// <summary>The module's own part of the accepted input, read from the frozen state.</summary>
    JsonObject Input(WorkspaceState state);

    /// <summary>The membership rules of its measures that declare one (<see cref="MeasureDefinition.DeclaresMembership"/>).</summary>
    IReadOnlyList<IMembershipRule> Membership(WorkspaceState state);

    /// <summary>What blocks each of this module's selected measures, by name. Shared inputs are the portfolio's to check.</summary>
    IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected);

    /// <summary>
    /// As <see cref="Validate(WorkspaceState, IReadOnlyList{string})"/>, given the trades Price froze with their cells, so
    /// that what it refuses and what Price froze are one reading of a cell that may change on its own, such as PV01.
    /// </summary>
    IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected, IReadOnlyList<EffectiveTrade> priced) => Validate(state, selected);

    /// <summary>The columns it adds to the trade grids (ADR-0010 §10), in order; none unless it says.</summary>
    IReadOnlyList<TradeColumn> TradeColumns => [];

    /// <summary>The fields of the shared input tabs its measures need (ADR-0010 §10); none unless it says.</summary>
    IReadOnlyList<MeasureInput> Inputs => [];

    /// <summary>Its cells on each trade, by column id, in the trades' order; null for a trade it adds none to.</summary>
    IReadOnlyList<IReadOnlyDictionary<string, TradeCell>?> Cells(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades) => [.. trades.Select(_ => (IReadOnlyDictionary<string, TradeCell>?)null)];

    /// <summary>Why a value cannot be typed into one of its columns, by its type alone; null when it can.</summary>
    string? Check(string column, JsonNode? value) => $"{column} is not typed.";

    /// <summary>Asks again for every failed result among its cells on these trades; answers how many it asked again.</summary>
    int Retry(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades) => 0;

    /// <summary>The step that prices this module's selected measures from the accepted input, writing the measure result.</summary>
    WorkflowStep Step(TypedDataRef acceptedInput);
}

/// <summary>Every registered measure, in module order. Ids are unique across modules, or startup stops.</summary>
public sealed class MeasureRegister
{
    public IReadOnlyList<IMeasureModule> Modules { get; }
    public IReadOnlyList<RegisteredMeasure> Measures { get; }

    public MeasureRegister(IEnumerable<IMeasureModule> modules)
    {
        Modules = [.. modules];
        Measures = [.. Modules.SelectMany(m => m.Measures.Select(d => new RegisteredMeasure(d.Id, d.Title, m.Id, d.Metrics, d.Headline, d.DeclaresMembership, d.Traced, d.Quoted, m.PricedBy, d.Tabs ?? [])))];
        var duplicate = Measures.GroupBy(m => m.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Measure {duplicate.Key} is registered by {string.Join(" and ", duplicate.Select(m => m.Module))}.");
        var clash = Modules.Select(m => m.Id).FirstOrDefault(id => Measures.Any(m => m.Id == id));
        if (clash is not null)
            throw new InvalidOperationException($"{clash} is both a module and a measure, so `measures {clash}` would mean two things.");
        // A request is a module's own: one priced by another module's request cannot pass it on.
        foreach (var module in Modules.Where(m => m.PricedBy != m.Id))
            if (Modules.SingleOrDefault(m => m.Id == module.PricedBy) is not { } request || request.PricedBy != request.Id)
                throw new InvalidOperationException($"Module {module.Id} is priced by {module.PricedBy}, which is not a registered module pricing its own measures.");
    }

    /// <summary>The modules whose requests a selection runs, in module order: those that price a selected measure.</summary>
    public IReadOnlyList<IMeasureModule> Requests(IReadOnlyList<string> selected)
    {
        var requests = Measures.Where(m => selected.Contains(m.Id)).Select(m => m.Request).ToHashSet();
        return [.. Modules.Where(m => requests.Contains(m.Id))];
    }

    /// <summary>The modules whose part of the accepted input a selection's requests read: every module they price.</summary>
    public IReadOnlyList<IMeasureModule> Inputs(IReadOnlyList<string> selected)
    {
        var requests = Requests(selected).Select(r => r.Id).ToHashSet();
        return [.. Modules.Where(m => requests.Contains(m.PricedBy))];
    }

    public IReadOnlyList<string> Ids => [.. Measures.Select(m => m.Id)];

    /// <summary>Every module's trade columns, by field (<c>{module}.{id}</c>), in module order.</summary>
    public IReadOnlyList<RegisteredColumn> TradeColumns => [.. Modules.SelectMany(m => m.TradeColumns.Select(c => new RegisteredColumn($"{m.Id}.{c.Id}", c.Title, c.Type, c.Editable, m.Id)))];

    /// <summary>The shared input fields the selected measures need, in module order: only they show (ADR-0010 §10).</summary>
    public IReadOnlyList<MeasureInput> Inputs(WorkspaceState state)
    {
        var selected = PortfolioMaterialiser.SelectedMeasures(state, this);
        return [.. Modules.SelectMany(m => m.Inputs).Where(i => selected.Contains(i.Measure))];
    }

    /// <summary>
    /// The settings of each selected module that a selected measure needs, with their values on the reading (ADR-0010 §8):
    /// only they show, and a hidden one keeps its value. A module none of whose settings is needed has no section.
    /// </summary>
    public IReadOnlyList<ModuleSettings> Settings(WorkspaceState state)
    {
        var selected = PortfolioMaterialiser.SelectedMeasures(state, this);
        return [.. Selected(state).Select(m => new ModuleSettings(m.Id, m.Title, [.. m.Settings.Where(s => s.Measures.Any(selected.Contains)).Select(s =>
        {
            var value = MeasureSettings.Value(state, m.Id, s);
            var cell = MeasureSettings.Cell(m, s);
            return new SettingView(s.Id, s.Title, s.Form, value?.DeepClone(), PortfolioCells.Write(cell, value), s.Default?.DeepClone(), PortfolioCells.Write(cell, s.Default),
                s.Empty, s.Measures, !PortfolioMaterialiser.Same(value, s.Default));
        })])).Where(m => m.Settings.Count > 0)];
    }

    /// <summary>The module whose settings an entity is (<c>settings:&lt;module&gt;</c>), or null where it is no module's settings.</summary>
    public IMeasureModule? SettingsOf(string entity) =>
        Entity.Split(entity) is (Entity.Settings, var id) && id != Entity.WorkspaceId ? Modules.SingleOrDefault(m => m.Id == id && m.Settings.Count > 0) : null;

    /// <summary>The module and column a trade field names, or null where it is not a module's column.</summary>
    public (IMeasureModule Module, TradeColumn Column)? ColumnOf(string field) =>
        Modules.SelectMany(m => m.TradeColumns.Select(c => (Module: m, Column: c))).Where(x => $"{x.Module.Id}.{x.Column.Id}" == field)
            .Select(x => ((IMeasureModule, TradeColumn)?)x).SingleOrDefault();

    /// <summary>The modules whose measures the state selects: only their columns show and only they are asked (ADR-0010 §10).</summary>
    public IReadOnlyList<IMeasureModule> Selected(WorkspaceState state)
    {
        var selected = PortfolioMaterialiser.SelectedMeasures(state, this);
        return [.. Modules.Where(m => m.Measures.Any(d => selected.Contains(d.Id)))];
    }

    /// <summary>
    /// The trades with the cells of every selected module's columns on them, keyed by field. Asked each time it is
    /// read, never kept with the state: a result cell such as PV01 changes with no edit. A module's input starts empty,
    /// as no source holds it, so a value in one differs from Original (ADR-0010 §2), whether it was typed or a rule
    /// filled it, as PV Adj from Margin. A result column, such as PV01, is no one's change and is never marked.
    /// </summary>
    public IReadOnlyList<EffectiveTrade> WithCells(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades)
    {
        var modules = Selected(state).Where(m => m.TradeColumns.Count > 0).ToList();
        if (modules.Count == 0 || trades.Count == 0) return trades;
        var inputs = modules.SelectMany(m => m.TradeColumns.Where(c => c.Editable).Select(c => $"{m.Id}.{c.Id}")).ToHashSet();
        var cells = modules.Select(m =>
        {
            var own = m.Cells(state, trades);
            if (own.Count != trades.Count) throw new InvalidOperationException($"Module {m.Id} gave {own.Count} rows of cells for {trades.Count} trades.");
            return (Module: m, Cells: own);
        }).ToList();
        return [.. trades.Select((t, i) =>
        {
            var row = new Dictionary<string, TradeCell>();
            foreach (var (module, own) in cells)
                if (own[i] is { } mine)
                    foreach (var (id, cell) in mine) row[$"{module.Id}.{id}"] = cell;
            if (row.Count == 0) return t;
            var held = row.Where(c => inputs.Contains(c.Key) && c.Value.Value is not null).Select(c => c.Key).ToList();
            var original = held.Count == 0 ? t.Original
                : (t.Original ?? new Dictionary<string, JsonNode?>()).Concat(held.Select(f => KeyValuePair.Create(f, (JsonNode?)null))).ToDictionary();
            return t with { Cells = row, Original = original };
        })];
    }

    /// <summary>Every declared membership rule over a state. A measure that declares membership and gives no rule stops here.</summary>
    public IReadOnlyList<IMembershipRule> Rules(WorkspaceState state)
    {
        var rules = Modules.SelectMany(m => m.Membership(state)).ToList();
        var missing = Measures.Where(m => m.DeclaresMembership && rules.All(r => r.Measure != m.Id)).Select(m => m.Id).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"Measure {string.Join(", ", missing)} declares membership and gave no rule.");
        return rules;
    }

    /// <summary>The trades with each declared measure's membership on them, as the page shows them and Price freezes them.</summary>
    public IReadOnlyList<EffectiveTrade> WithMembership(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades)
    {
        var rules = Rules(state);
        return rules.Count == 0 ? trades : [.. trades.Select(t => t with { Membership = rules.ToDictionary(r => r.Measure, r => r.Of(t)) })];
    }

    /// <summary>The CSAs with each rule's standing on them, where the rule reads CSAs.</summary>
    public IReadOnlyList<EffectiveCsa> WithMembership(WorkspaceState state, IReadOnlyList<EffectiveCsa> csas)
    {
        var rules = Rules(state);
        return [.. csas.Select(c => c with
        {
            Membership = rules.Select(r => (r.Measure, Standing: r.Of(c))).Where(x => x.Standing is not null).ToDictionary(x => x.Measure, x => x.Standing!),
        })];
    }

    /// <summary>
    /// A selection as given, checked and put in register order. A module's id stands for all of its measures
    /// (`measures cva ccr`, ADR-0013 §1). Refused by name, never trimmed to what is known.
    /// </summary>
    public IReadOnlyList<string> Checked(IReadOnlyList<string> selection)
    {
        var expanded = selection.SelectMany(s => Modules.SingleOrDefault(m => m.Id == s) is { } module ? module.Measures.Select(d => d.Id) : [s]).ToList();
        var unknown = expanded.Where(s => !Ids.Contains(s)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new WorkspaceRefused("UnknownMeasure", $"No measure {string.Join(", ", unknown)} is registered; the measures are {string.Join(", ", Ids)}.");
        if (expanded.Count == 0)
            throw new WorkspaceRefused("NoMeasure", "Select at least one measure to price.");
        return [.. Ids.Where(expanded.Contains)];
    }
}

/// <summary>A module's settings as a reading holds them (ADR-0010 §8): the user's value where set, else the default.</summary>
public static class MeasureSettings
{
    /// <summary>The entity a module's settings are edits on.</summary>
    public static string EntityOf(string module) => Entity.Of(Entity.Settings, module);

    /// <summary>One setting's value on a reading: the user's where set, empty included, else its default.</summary>
    public static JsonNode? Value(WorkspaceState state, string module, MeasureSetting setting)
    {
        PortfolioCases.Unlayered(state);
        var entity = EntityOf(module);
        return state.Overrides.Values.SingleOrDefault(o => o.Target.Entity == entity && o.Target.Field == setting.Id) is { } set ? set.Value : setting.Default;
    }

    /// <summary>Every setting of a module on a reading, by id, as its input part carries them: frozen by Price, never defaulted again.</summary>
    public static JsonObject Values(WorkspaceState state, IMeasureModule module) =>
        new([.. module.Settings.Select(s => KeyValuePair.Create(s.Id, Value(state, module.Id, s)?.DeepClone()))]);

    /// <summary>The cell a setting is, on its module's row.</summary>
    public static CellName Cell(IMeasureModule module, MeasureSetting setting) =>
        new(EntityOf(module.Id), setting.Id, module.Id, Names(setting)[0], setting.Form);

    /// <summary>The names a setting's cell is named by: its title's words, then its own names.</summary>
    public static IReadOnlyList<string> Names(MeasureSetting setting) =>
        [.. new[] { setting.Title }.Concat(setting.Names ?? []).Select(n => string.Join(' ', n.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))).Distinct()];
}
