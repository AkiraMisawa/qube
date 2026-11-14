using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Qube.Composition.Application;

namespace Qube.Samples.Xva;

/// <summary>
/// The files of the request the XVA analytics takes (ADR-0016 note 69; owner, 2026-11-14), written from the trades of
/// one request as the accepted input carries them: Before and After are requests of their own, each with its trades.
/// Nothing writes them to an engine yet: every measure here is simulated, and the engine's adapter will call this.
/// </summary>
public static class XvaRequestFiles
{
    /// <summary>Where a request holds its trades' official PVs, by leg.</summary>
    public const string OfficialPvPath = "/deals/official_pv.csv";

    /// <summary>
    /// <see cref="OfficialPvPath"/>: <c>id,ccy,pv,notional</c>, then a row for each leg of each trade, in the trades'
    /// order and under a trade in the order it holds its legs. A leg's id is its trade's, an underscore and the leg's
    /// place under its trade from 0; its PV and notional are in its currency, written as the input holds them. A
    /// trade with no leg has no row, and a leg with no notional leaves that field empty: nothing stands in for it.
    /// </summary>
    public static string OfficialPv(IEnumerable<JsonNode> trades)
    {
        var file = new StringBuilder("id,ccy,pv,notional\n");
        foreach (var trade in trades)
        {
            var legs = Held(trade, "legs").AsArray();
            for (var at = 0; at < legs.Count; at++)
            {
                var leg = legs[at] ?? throw new CapabilityFailure("InvalidInput", $"Trade {Text(trade, "tradeId")} carries a leg that is nothing.");
                var id = $"{Text(trade, "tradeId")}_{at.ToString(CultureInfo.InvariantCulture)}";
                var fields = new[] { id, Text(leg, "currency"), Number(Held(leg, "pv")), leg["notional"] is { } notional ? Number(notional) : "" };
                if (fields.Any(f => f.IndexOfAny([',', '"', '\r', '\n']) >= 0))
                    throw new CapabilityFailure("InvalidInput", $"{OfficialPvPath} cannot hold {id}: a comma, a quote or a line break would break its row.");
                file.AppendJoin(',', fields).Append('\n');
            }
        }
        return file.ToString();
    }

    static JsonNode Held(JsonNode row, string field) =>
        row[field] ?? throw new CapabilityFailure("InvalidInput", $"The accepted input carries a row without its {field}, which {OfficialPvPath} is written from.");

    static string Text(JsonNode row, string field) => Held(row, field).GetValue<string>();

    static string Number(JsonNode value) => value.GetValue<decimal>().ToString(CultureInfo.InvariantCulture);
}
