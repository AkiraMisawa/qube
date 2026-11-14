using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Qube.Composition.Application;

namespace Qube.Samples.Xva;

/// <summary>The side of a case that a request prices: Before and After are requests of their own (docs/03).</summary>
public enum XvaRequestSide
{
    /// <summary>The trades the case holds in Before.</summary>
    Before,

    /// <summary>The trades the case holds in After.</summary>
    After,
}

/// <summary>
/// The files of the request the XVA analytics takes (ADR-0016 note 69; owner, 2026-11-14), written from a case of the
/// accepted input for one of its sides. No engine is written to yet: every measure here is simulated, and nothing but
/// a test calls this.
/// </summary>
public static class XvaRequestFiles
{
    /// <summary>Where a request holds its trades' official PVs, by leg.</summary>
    public const string OfficialPvPath = "/deals/official_pv.csv";

    const string ACase = "A case of the accepted input", ATrade = "A trade of the accepted input";

    /// <summary>
    /// <see cref="OfficialPvPath"/>: <c>id,ccy,pv,notional</c>, then a row for each leg of each trade of the side, in
    /// the trades' order and under a trade in the order it holds its legs. A leg's id is its trade's, an underscore
    /// and the leg's place under its trade from 0; its PV and notional are in its currency, written as the input
    /// holds them. A trade with no leg has no row, and a leg whose notional the input says is empty leaves that field
    /// empty: nothing stands in for it. A line ends in a line feed, the last one too. Refused, each with the trade or
    /// the leg it is said of where the input names one: a case, a trade or a leg without a field the input carries
    /// for every one, a value of another form or an empty text, a row that a comma, a quote or a line break would
    /// break, and a trade the case holds twice.
    /// </summary>
    /// <param name="case">A case of the accepted input, whose trades each say whether Before holds them, and After.</param>
    /// <param name="side">The side the request prices: its trades are written, and no other.</param>
    public static string OfficialPv(JsonNode @case, XvaRequestSide side)
    {
        var trades = Required(@case, "trades", ACase) as JsonArray ?? throw Refused($"{ACase} holds trades that are no list.");
        var held = side == XvaRequestSide.Before ? "before" : "after";
        var file = new StringBuilder("id,ccy,pv,notional\n");
        var given = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trade in trades)
        {
            var tradeId = Text(trade ?? throw Refused($"{ACase} holds nothing in a trade's place."), "tradeId", ATrade);
            if (!given.Add(tradeId))
                throw Refused($"{OfficialPvPath} cannot hold trade {tradeId} twice: a case holds a trade once.");
            if (!Flag(trade, held, $"Trade {tradeId}")) continue;
            var legs = Required(trade, "legs", $"Trade {tradeId}") as JsonArray ?? throw Refused($"Trade {tradeId}'s legs are no list.");
            for (var at = 0; at < legs.Count; at++)
            {
                var id = $"{tradeId}_{at.ToString(CultureInfo.InvariantCulture)}";
                var leg = legs[at] ?? throw Refused($"{id} is no leg: the accepted input carries nothing in its place.");
                // The input says a leg's notional even where it holds none (note 53): the name is there and its value empty.
                var fields = new[] { id, Text(leg, "currency", id), Number(leg, "pv", id), Held(leg, "notional", id) is null ? "" : Number(leg, "notional", id) };
                if (fields.Any(f => f.IndexOfAny([',', '"', '\r', '\n']) >= 0))
                    throw Refused($"{OfficialPvPath} cannot hold {id}: a comma, a quote or a line break would break its row.");
                file.AppendJoin(',', fields).Append('\n');
            }
        }
        return file.ToString();
    }

    static CapabilityFailure Refused(string message) => new("InvalidInput", message);

    // What a case, a trade or a leg holds under a name the accepted input carries for every one. The name not being
    // there is said, with what it is missing from; the value may be empty.
    static JsonNode? Held(JsonNode row, string field, string of) =>
        row is JsonObject fields && fields.TryGetPropertyValue(field, out var value) ? value : throw Missing(field, of);

    // The same, where the input holds no empty value under the name.
    static JsonNode Required(JsonNode row, string field, string of) => Held(row, field, of) ?? throw Missing(field, of);

    static CapabilityFailure Missing(string field, string of) => Refused($"{of} carries no {field}, which {OfficialPvPath} is written from.");

    static string Text(JsonNode row, string field, string of) =>
        Required(row, field, of) is not JsonValue value || !value.TryGetValue<string>(out var text) ? throw Refused($"{of} carries a {field} that is no text.")
        : string.IsNullOrWhiteSpace(text) ? throw Refused($"{of} carries an empty {field}.")
        : text;

    // A number as the input holds it: no grouping, no exponent, the decimals it was given.
    static string Number(JsonNode row, string field, string of) =>
        Required(row, field, of) is JsonValue value && value.TryGetValue<decimal>(out var number)
            ? number.ToString(CultureInfo.InvariantCulture) : throw Refused($"{of} carries a {field} that is no number.");

    static bool Flag(JsonNode row, string field, string of) =>
        Required(row, field, of) is JsonValue value && value.TryGetValue<bool>(out var said) ? said : throw Refused($"{of}'s {field} is neither true nor false.");
}
