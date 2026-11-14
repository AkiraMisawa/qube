using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

/// <summary>A package as the trades' Package cells write it (ADR-0016 note 90): its name, the new trades it adds and the existing trades it takes out of After.</summary>
public sealed record PackageView(string Name, IReadOnlyList<string> Adds, IReadOnlyList<string> TakesOut);

/// <summary>
/// The packages of iterative addition (ADR-0016 note 90 of 2026-11-14; owner, the same day: case creation treats
/// several trades as one package and names the trades it takes out). A trade's Package cell names the one package it
/// is in: the new trades of a package are added together in one case, and its existing trades are taken out of After
/// in that case alone. A New row with A ticked that is in no package is a case alone, as before. What the cells say is
/// read here, and is never mended for the user: a cell that cannot be read as a package is refused by the trade's name.
/// </summary>
public static class PortfolioPackages
{
    /// <summary>The field of a trade that names its package.</summary>
    public const string Field = "package";

    /// <summary>Why a value is not a package's name, or none where it is one: a name as written, with no comma; or empty for none.</summary>
    public static string? Problem(string? name) =>
        name is null || name.Length > 0 && name == name.Trim() && !name.Contains(',') ? null
            : "A package is a name with no comma, such as P1, or empty for none.";

    /// <summary>The packages the cells write, in the order of their first trade: the New rows first, then the existing.</summary>
    public static IReadOnlyList<PackageView> Written(IReadOnlyList<EffectiveTrade> trades)
    {
        var ordered = trades.Where(t => t.IsNew).Concat(trades.Where(t => !t.IsNew)).Where(t => t.Package is not null).ToList();
        return [.. ordered.Select(t => t.Package!).Distinct(StringComparer.Ordinal).Select(name => new PackageView(name,
            [.. ordered.Where(t => t.IsNew && t.Package == name).Select(t => t.TradeId)],
            [.. ordered.Where(t => !t.IsNew && t.Package == name).Select(t => t.TradeId)]))];
    }

    /// <summary>The first name of <c>P1</c>, <c>P2</c>, … that no trade's package is, whatever the case of its letters.</summary>
    public static string NextFree(IReadOnlyList<EffectiveTrade> trades)
    {
        var taken = trades.Select(t => t.Package).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(1, taken.Count + 1).Select(n => $"P{n}").First(name => !taken.Contains(name));
    }

    /// <summary>
    /// The set <c>add each</c> makes of the trades as the Base holds them (docs/03 §5, ADR-0016 §3 and note 90): a case
    /// per package and one per New row with A ticked that is in none, in the order of the rows, the New grid's first.
    /// Never cumulative: each case adds its own new trades alone. Refused by the trades' names, with nothing made: two
    /// packages whose names differ only in case, a New row of a package with A clear, a new trade to add that is in
    /// Before, and an existing trade of a package that the Base does not have in After; and a set of no case.
    /// </summary>
    public static IReadOnlyList<Iteration> Set(IReadOnlyList<EffectiveTrade> trades)
    {
        var news = trades.Where(t => t.IsNew).ToList();
        var existing = trades.Where(t => !t.IsNew).ToList();
        var packaged = news.Concat(existing).Where(t => t.Package is not null).ToList();
        if (packaged.GroupBy(t => t.Package!, StringComparer.OrdinalIgnoreCase).Select(g => g.Select(t => t.Package!).Distinct(StringComparer.Ordinal).ToList())
            .FirstOrDefault(names => names.Count > 1) is { } clash)
            throw new WorkspaceRefused("PackageNamesDiffer",
                $"The packages {TableWords.And(clash)} differ only in the case of their letters, and whether they are one is not guessed. Write one name on all of their trades.");
        if (news.Where(t => t.Package is not null && !t.After).ToList() is { Count: > 0 } clear)
        {
            var (has, them, their) = clear.Count == 1 ? ("has", "it", "its") : ("have", "them", "their");
            throw new WorkspaceRefused("CandidateNotTicked",
                $"{InPackages(clear)} and {has} A clear, and a package adds New rows with A ticked. Tick A on {them}, or clear {their} Package.");
        }
        if (news.Where(t => t.After && t.Before).ToList() is { Count: > 0 } inBefore)
        {
            var (are, them) = inBefore.Count == 1 ? ("is", "it") : ("are", "them");
            var ids = string.Join(", ", inBefore.Select(t => t.TradeId));
            // A trade alone may also be left out by clearing A; one in a package is then refused as above.
            throw new WorkspaceRefused("CandidateInBefore", inBefore.All(t => t.Package is null)
                ? $"{ids} {are} in Before, and a new trade is priced alone only out of Before. Clear B on {them}, or A to leave {them} out, then add each again."
                : $"{ids} {are} in Before, and add each adds a new trade only out of Before. Clear B on {them}, then add each again.");
        }
        if (existing.Where(t => t.Package is not null && !t.After).ToList() is { Count: > 0 } outside)
        {
            var (them, their) = outside.Count == 1 ? ("it", "its") : ("them", "their");
            throw new WorkspaceRefused("NotInAfter",
                $"{InPackages(outside)} and not in After in the Base, so no case takes {them} out. Tick A on {them}, or clear {their} Package.");
        }
        var set = new List<Iteration>();
        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in news.Concat(existing))
        {
            if (t.Package is { } name)
            {
                if (made.Add(name))
                    set.Add(new([.. news.Where(n => n.Package == name).Select(n => n.TradeId)], [.. existing.Where(e => e.Package == name).Select(e => e.TradeId)], name));
            }
            else if (t.IsNew && t.After) set.Add(new([t.TradeId], []));
        }
        return set.Count > 0 ? set
            : throw new WorkspaceRefused("NothingToAdd", "add each makes a case per package and per New row with A ticked, and no trade is in a package and no New row has A ticked.");
    }

    // The trades a refusal names and where they are: `DRAFT-3 is in the package P1`, or `DRAFT-3 (P1) and DRAFT-4 (P2)
    // are in packages`.
    static string InPackages(IReadOnlyList<EffectiveTrade> trades) => trades.Count == 1
        ? $"{trades[0].TradeId} is in the package {trades[0].Package}"
        : $"{TableWords.And(trades.Select(t => $"{t.TradeId} ({t.Package})"))} are in {(trades.Select(t => t.Package).Distinct().Count() == 1 ? "a package" : "packages")}";
}
