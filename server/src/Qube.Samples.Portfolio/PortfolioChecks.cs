using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

/// <summary>
/// What a value is checked for before it becomes an edit, wherever it was written: a grid's cell, the Changes tab,
/// <c>vary</c>, or a command over a registered table (ADR-0016 §1). One check, so a command is refused as the grid
/// refuses the cell. Type and bounds only: the meaning (eligibility, availability) is the materialiser's, and shows on
/// the row.
/// </summary>
public sealed class PortfolioChecks(MeasureRegister register)
{
    /// <summary>Refuses, by name, a value the field cannot hold on the reading it is written into, or a field no edit sets.</summary>
    /// <param name="trade">The trade with its modules' cells, where the caller has read them already; found here otherwise.</param>
    /// <param name="selected">The modules whose measures the reading selects, where the caller has read them already.</param>
    public void Check(WorkspaceState reading, string entity, string field, JsonNode? value, EffectiveTrade? trade = null, IReadOnlyList<IMeasureModule>? selected = null)
    {
        RefuseNotLoaded(reading, entity);
        if (register.ColumnOf(field) is { } column) CheckCell(reading, entity, field, column, value, trade, selected);
        // A module's setting is checked by the module that reads it (ADR-0010 §8, §10).
        else if (register.SettingsOf(entity) is { } module)
        {
            if (module.Settings.All(x => x.Id != field))
                throw new WorkspaceRefused("NotEditable", $"{module.Title} has no setting {field}; its settings are {string.Join(", ", module.Settings.Select(x => x.Id))}.");
            if (module.CheckSetting(field, value) is { } problem)
                throw new WorkspaceRefused("InvalidValue", problem, [PortfolioEditRules.Target(Source(reading), entity, field)]);
        }
        else
        {
            if (!Editable(entity, field))
                throw new WorkspaceRefused("NotEditable", $"{entity}.{field} is not editable in this workspace.");
            Validate(reading, entity, field, value);
        }
    }

    // A module's column is typed where its cell on the trade says it may be, as the page shows it: PV Adj is read-only
    // while Margin is entered (ADR-0010 §6), and refused then with the cell's own note.
    void CheckCell(WorkspaceState state, string entity, string field, (IMeasureModule Module, TradeColumn Column) owner, JsonNode? value, EffectiveTrade? given,
        IReadOnlyList<IMeasureModule>? selected)
    {
        var (kind, id) = Entity.Split(entity);
        var title = owner.Column.Title;
        var trade = kind != Entity.Trade ? null : given ?? PortfolioMaterialiser.Trades(state).FirstOrDefault(t => t.TradeId == id);
        if (trade is null) throw new WorkspaceRefused("NotEditable", $"{title} is a column of a trade, and {entity} is not one in this workspace.");
        if ((selected ?? register.Selected(state)).All(m => m != owner.Module))
            throw new WorkspaceRefused("NotEditable", $"{title} shows while one of {owner.Module.Id}'s measures is selected.");
        var cell = (given ?? register.WithCells(state, [trade])[0]).Cells?.GetValueOrDefault(field);
        if (cell is null) throw new WorkspaceRefused("NotEditable", $"{trade.TradeId} has no {title}.");
        if (!cell.Editable)
            throw new WorkspaceRefused("NotEditable", cell.Note is null ? $"{title} of {trade.TradeId} is read-only." : $"{title} of {trade.TradeId} is read-only: {cell.Note}");
        if (owner.Module.Check(owner.Column.Id, value) is { } problem)
            throw new WorkspaceRefused("InvalidValue", problem, [PortfolioEditRules.Target(Source(state), entity, field)]);
    }

    static PortfolioSourceVersion Source(WorkspaceState state) => PortfolioSourceVersion.Of(state);

    // A value is written on a trade the workspace holds, or on a row under one (ADR-0016 note 59): the source answers
    // for every trade of the store of booked trades, so one the workspace has not loaded is refused here, by name.
    static void RefuseNotLoaded(WorkspaceState reading, string entity)
    {
        var (kind, id) = Entity.Split(entity);
        if (kind is not (Entity.Trade or Entity.Leg or Entity.Amount)) return;
        if (BookedTrades.TradeNamed(id) is { } trade && Source(reading).Trade(trade) is null)
            throw new WorkspaceRefused("UnknownTarget", $"No trade {trade} in this workspace: load trades {trade} brings it in.");
    }

    static bool Editable(string entity, string field) => (Entity.Split(entity).Kind, field) switch
    {
        // A field beyond the grids is set where its table says a command sets it (ADR-0016 note 51).
        (Entity.Trade or Entity.Csa, var f) when PortfolioDetails.Find(Entity.Split(entity).Kind, f) is { } detail => detail.Editable,
        (Entity.Trade, var f) => PortfolioMaterialiser.EditableFields.Contains(f),
        (Entity.Csa, var f) => PortfolioMaterialiser.CsaFields.Contains(f),
        // A PV leg's currency, PV and notional (ADR-0016 note 49). An independent amount is removed, never changed.
        (Entity.Leg, var f) => PortfolioLegs.EditableFields.Contains(f),
        // The CVA recovery is the netting set's since ADR-0010 §8; one set on the counterparty before still applies.
        (Entity.Netting, "cvaRecovery") => true,
        // The counterparty's CDS and the spreads of a curve of the catalogue (ADR-0010 §8's note of 2026-11-09).
        (Entity.Credit, var f) => CdsCatalog.ChoiceFields.Contains(f),
        (Entity.Cds, var f) => CdsCatalog.Find(Entity.Split(entity).Id) is not null && CdsCatalog.Tenors.Contains(f),
        // B or A for every row of a grid at once (ADR-0010 §6).
        (Entity.Grid, "before" or "after") => Entity.Split(entity).Id is Entity.NewGrid or Entity.ExistingGrid,
        // The workspace's display currency (ADR-0010 §1).
        (Entity.Settings, Entity.DisplayCurrency) => entity == Entity.WorkspaceSettings,
        _ => false,
    };

    static void Validate(WorkspaceState state, string entity, string field, JsonNode? value)
    {
        var kind = Entity.Split(entity).Kind;
        // A field beyond the grids is checked by its form (ADR-0016 note 51).
        string? problem = PortfolioDetails.Find(kind, field) is { } detail ? PortfolioDetails.Problem(detail, value)
        // A leg's fields share names with a trade's and mean their own (ADR-0016 note 49): its notional may be empty.
        : kind == Entity.Leg ? field switch
        {
            "pv" => value is JsonValue p && p.TryGetValue<decimal>(out _) ? null : "A leg's PV is a number.",
            "notional" => value is null || value is JsonValue l && l.TryGetValue<decimal>(out _) ? null : "A leg's notional is a number or empty.",
            "currency" => value is JsonValue lc && lc.TryGetValue<string>(out var legCode) && PortfolioMarket.Currencies.Contains(legCode)
                ? null : $"A currency is {string.Join(", ", PortfolioMarket.Currencies)}.",
            _ => null,
        }
        : field switch
        {
            "notional" => value is JsonValue v && v.TryGetValue<decimal>(out var n) && n != 0 ? null : "A notional is a non-zero number.",
            "fixedRate" => value is null || value is JsonValue r && r.TryGetValue<decimal>(out _) ? null : "A fixed rate is a number or empty.",
            "cvaRecovery" => value is null || value is JsonValue c && c.TryGetValue<decimal>(out var rec) && rec is >= 0 and < 1 ? null : "A CVA recovery is empty or from 0 up to 1.",
            "before" or "after" => value is JsonValue b && b.TryGetValue<bool>(out _) ? null : "Before and After are true or false.",
            "schedule" => value is JsonValue s && s.TryGetValue<string>(out var t) && t is "3M" or "6M" ? null : "A schedule is 3M or 6M.",
            "csa" or "curve" or "fvaCurve" => value is JsonValue x && x.TryGetValue<string>(out var id) && id.Length > 0 ? null : $"A {field} is an identifier.",
            // Empty is no IM CSA of that direction (ADR-0012 §2).
            "imR" or "imP" => value is null || value is JsonValue im && im.TryGetValue<string>(out var imId) && imId.Length > 0 ? null : $"{(field == "imR" ? "IM-R" : "IM-P")} is an IM CSA's identifier, or empty for none.",
            "frequency" => value is JsonValue f && f.TryGetValue<string>(out var often) && PortfolioCatalog.Frequencies.Contains(often)
                ? null : $"A frequency is {string.Join(", ", PortfolioCatalog.Frequencies)}.",
            "exchange" or "oneWay" => value is JsonValue e && e.TryGetValue<bool>(out _) ? null : $"{field} is true or false.",
            "counterpartyThreshold" or "counterpartyMta" or "selfThreshold" or "selfMta" =>
                value is JsonValue a && a.TryGetValue<decimal>(out var amount) && amount >= 0 ? null : "A threshold or MTA is an amount of 0 or more.",
            "currency" or "counterpartyCurrency" or "selfCurrency" or Entity.DisplayCurrency => value is JsonValue c3 && c3.TryGetValue<string>(out var code) && PortfolioMarket.Currencies.Contains(code)
                ? null : $"A currency is {string.Join(", ", PortfolioMarket.Currencies)}.",
            // The counterparty's CDS (ADR-0010 §8's note of 2026-11-09): each key is the catalogue's, or empty.
            "cdsType" => value is null || value is JsonValue ct && ct.TryGetValue<string>(out var type) && type is CdsCatalog.SingleName or CdsCatalog.Proxy
                ? null : $"A CDS type is {CdsCatalog.SingleName}, {CdsCatalog.Proxy}, or empty for none.",
            "cdsReference" => value is null || value is JsonValue cr && cr.TryGetValue<string>(out var reference) && CdsCatalog.Find(reference) is { Type: CdsCatalog.SingleName }
                ? null : $"A CDS reference is a single name of the catalogue: {string.Join(", ", CdsCatalog.SingleNames.Select(c => c.Id))}.",
            "proxySector" => OneOf(value, CdsCatalog.Sectors, "A proxy's sector"),
            "proxyRegion" => OneOf(value, CdsCatalog.Regions, "A proxy's region"),
            "proxyRating" => OneOf(value, CdsCatalog.Ratings, "A proxy's rating"),
            _ when kind == Entity.Cds =>
                value is JsonValue sp && sp.TryGetValue<decimal>(out var spread) && spread is > 0 and < 1 ? null : "A spread is above 0bp and below 10000bp.",
            _ => null,
        };
        if (problem is not null) throw new WorkspaceRefused("InvalidValue", problem, [PortfolioEditRules.Target(Source(state), entity, field)]);
    }

    static string? OneOf(JsonNode? value, IReadOnlyList<string> allowed, string what) =>
        value is null || value is JsonValue v && v.TryGetValue<string>(out var text) && allowed.Contains(text) ? null : $"{what} is one of {string.Join(", ", allowed)}, or empty.";
}
