using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Contracts;

namespace Qube.Samples.Portfolio;

// ADR-0016 §3 and its note 29 of 2026-11-11: the words the pricer's users type for a line of `set`. Each answers the
// line it stands for, and the kernel reads that line: what runs, what the log names and what `undo` takes back is
// `set`. None has behaviour of its own.

/// <summary>The pricer's short forms: <c>before</c> and <c>after</c> for a whole grid's B and A, and <c>currency</c> for the display currency.</summary>
public sealed class PortfolioShortForms : IShortFormCatalog
{
    public IEnumerable<IShortForm> ShortForms { get; } = [new Side("before"), new Side("after"), new Currency()];

    static readonly char[] Space = [' ', '\t'];

    // The word being typed, with where it starts: the last one, or an empty one after a space.
    static (IReadOnlyList<string> Before, string Typing, int Start) Typing(string text)
    {
        var words = LineWords.Of(text);
        return text.Length == 0 || char.IsWhiteSpace(text[^1]) ? ([.. words.Select(w => w.Word)], "", text.Length) : ([.. words.SkipLast(1).Select(w => w.Word)], words[^1].Word, words[^1].Start);
    }

    static LineCompletions Offer(IEnumerable<(string Value, string? Detail)> values, string typing, int start) =>
        new([.. values.Where(v => v.Value.StartsWith(typing, StringComparison.OrdinalIgnoreCase)).Select(v => new LineCompletion(v.Value, start, v.Detail))]);

    /// <summary>
    /// <c>before all</c>, <c>before none</c>, <c>after all</c>, <c>after none</c>, each with <c>new</c> or
    /// <c>existing</c> after it: <c>set existing before on</c>. With no grid typed it is the trade grid behind the line.
    /// </summary>
    sealed class Side(string side) : IShortForm
    {
        static readonly string[] Choices = ["all", "none"], Grids = ["new", "existing"];

        public ShortFormDescriptor Descriptor { get; } = new($"xva.{side}", $"Tick or clear {(side == "before" ? "Before" : "After")} on every trade of a grid",
            new([side], [new("which", CommandArgumentKind.Text, Required: true), new("grid", CommandArgumentKind.Text, Required: false)], PortfolioEditRules.App));

        public string Expand(LineContext line)
        {
            var words = line.Text.Split(Space, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) throw new TableRefused("NoChoice", $"{side} takes all or none, then new or existing.", null, Choices);
            var which = words[0].ToLowerInvariant();
            if (!Choices.Contains(which)) throw new TableRefused("UnknownChoice", $"{side} takes all or none, not {words[0]}.", words[0], Choices);
            var said = $"{side} {which}";
            if (words.Length > 2) throw new TableRefused("UnreadWords", $"{said} {words[1]} is the whole line: {words[2]} is not read.", words[2]);
            var grid = words.Length == 2 ? words[1].ToLowerInvariant()
                : line.View is { Table: PortfolioTables.Trades, Subset: { } shown } ? shown
                : throw new TableRefused("NoGrid", $"{said} takes new or existing, or the trade grid the keyboard is in.", null, Grids);
            if (!Grids.Contains(grid)) throw new TableRefused("UnknownGrid", $"{said} takes new or existing, not {words[1]}.", words[1], Grids);
            return $"set {grid} {side} {(which == "all" ? "on" : "off")}";
        }

        public LineCompletions Complete(LineContext line)
        {
            // No space after the form's own word: it is still being typed, and nothing follows yet.
            if (line.Text.Length == 0) return LineCompletions.None;
            var (before, typing, start) = Typing(line.Text);
            return before.Count switch
            {
                0 => Offer([("all", "tick every row"), ("none", "clear every row")], typing, start),
                1 => Offer(Grids.Select(g => (g, (string?)null)), typing, start),
                _ => LineCompletions.None,
            };
        }
    }

    /// <summary><c>currency USD</c>: the currency the figures are shown in, <c>set workspace currency USD</c>.</summary>
    sealed class Currency : IShortForm
    {
        public ShortFormDescriptor Descriptor { get; } = new("xva.currency", "Show the figures in a currency, such as USD",
            new(["currency"], [new("currency", CommandArgumentKind.Text, Required: true)], PortfolioEditRules.App));

        public string Expand(LineContext line)
        {
            var typed = line.Text.Trim();
            return typed.Length > 0 ? $"set {Entity.WorkspaceId} currency {typed.ToUpperInvariant()}"
                : throw new TableRefused("NoCurrency", $"currency takes the currency to show the figures in: {TableWords.List(PortfolioMarket.Currencies)}.", null, PortfolioMarket.Currencies);
        }

        public LineCompletions Complete(LineContext line)
        {
            if (line.Text.Length == 0) return LineCompletions.None;
            var (before, typing, start) = Typing(line.Text);
            return before.Count == 0 ? Offer(PortfolioMarket.Currencies.Select(c => (c, (string?)null)), typing, start) : LineCompletions.None;
        }
    }
}
