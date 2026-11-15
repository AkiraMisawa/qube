using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

/// <summary>A package as the trades' Package cells write it (ADR-0016 note 90): its name, the new trades it adds and the existing trades it takes out of After.</summary>
public sealed record PackageView(string Name, IReadOnlyList<string> Adds, IReadOnlyList<string> TakesOut);

/// <summary>
/// The packages of iterative addition (ADR-0016 note 90 of 2026-11-14; owner, the same day: case creation treats
/// several trades as one package and names the trades it takes out). A trade's Package cell names the packages it is
/// in, one or more separated by commas (note 95; owner, 2026-11-15): the new trades of a package are added together in
/// one case, and its existing trades are taken out of After in that case alone. A New row with A ticked that is in no
/// package is a case alone, as before. What the cells say is read here, and is never mended for the user: a cell that
/// cannot be read as packages is refused by the trade's name.
/// </summary>
public static class PortfolioPackages
{
    /// <summary>The field of a trade that names its packages.</summary>
    public const string Field = "package";

    /// <summary>What a Package cell may hold, as a refusal says it.</summary>
    public const string Rule = "A Package cell names one package or more, separated by commas, such as P1 or P1, P2, or is empty for none.";

    /// <summary>
    /// The names a Package cell holds, in the order written, the spaces around each comma being the list's; none where
    /// the cell is empty. The cell is one the check let through (<see cref="Problem"/>).
    /// </summary>
    public static IReadOnlyList<string> Names(string? cell) => cell is null ? [] : [.. cell.Split(',').Select(name => name.Trim())];

    /// <summary>
    /// Why a text is not a Package cell, or none where it is one: one name or more, separated by commas, with no space at
    /// either end of the cell, none of them empty, and none written twice, whatever the case of its letters.
    /// </summary>
    public static string? Problem(string cell)
    {
        if (cell.Length == 0 || cell != cell.Trim()) return Rule;
        var names = Names(cell);
        if (names.Any(name => name.Length == 0)) return Rule;
        return names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice
            ? twice.Distinct(StringComparer.Ordinal).Count() == 1
                ? $"{twice.Key} is written twice in one Package cell; write each package once."
                : $"{TableWords.And(twice.Distinct(StringComparer.Ordinal))} are one name written twice in one Package cell, whatever the case of its letters; write each package once."
            : null;
    }

    /// <summary>Why a text is not one package's name, as a package given by name must be: no space at either end and no comma.</summary>
    public static string? NameProblem(string name) =>
        name.Length > 0 && name == name.Trim() && !name.Contains(',') ? null : "A package is one name with no comma, such as P1.";

    /// <summary>The packages the cells name, in the order of their first trade: the New rows first, then the existing.</summary>
    public static IReadOnlyList<PackageView> Named(IReadOnlyList<EffectiveTrade> trades)
    {
        var order = new List<string>();
        var adds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var takesOut = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in trades.Where(t => t.IsNew).Concat(trades.Where(t => !t.IsNew)))
            foreach (var name in t.Packages)
            {
                if (!adds.ContainsKey(name))
                {
                    order.Add(name);
                    adds[name] = [];
                    takesOut[name] = [];
                }
                (t.IsNew ? adds : takesOut)[name].Add(t.TradeId);
            }
        return [.. order.Select(name => new PackageView(name, adds[name], takesOut[name]))];
    }

    /// <summary>The first name of <c>P1</c>, <c>P2</c>, … that no cell names, whatever the case of its letters.</summary>
    public static string NextFree(IReadOnlyList<EffectiveTrade> trades)
    {
        var taken = trades.SelectMany(t => t.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(1, taken.Count + 1).Select(n => $"P{n}").First(name => !taken.Contains(name));
    }

    /// <summary>
    /// The set <c>add each</c> makes of the trades as the Base holds them (docs/03 §5, ADR-0016 §3 and note 90): a case
    /// per package and one per New row with A ticked that is in none, in the order of the rows, the New grid's first, and
    /// a row's packages in the order its cell writes them. A row in several packages is in each of their cases (note
    /// 95). Never cumulative: each case adds its own new trades alone. Refused by the trades' names, with nothing made: two
    /// packages whose names differ only in case, a New row of a package with A clear, a new trade to add that is in
    /// Before, and an existing trade of a package that the Base does not have in After; and a set of no case. An
    /// existing trade of a package in After alone is taken out too, so its case holds it in neither (ADR-0016 note 91).
    /// </summary>
    public static IReadOnlyList<Iteration> AdditionSet(IReadOnlyList<EffectiveTrade> trades) =>
        AdditionSetOrNone(trades) is { Count: > 0 } set ? set
            : throw new WorkspaceRefused("NothingToAdd", "add each makes a case per package and per New row with A ticked, and no trade is in a package and no New row has A ticked.");

    /// <summary>
    /// The set the cells say, as <see cref="AdditionSet"/> makes it, refusing what it refuses, but empty where the
    /// cells say no case rather than refused: Price compares it with the cases made (ADR-0016 note 92).
    /// </summary>
    public static IReadOnlyList<Iteration> AdditionSetOrNone(IReadOnlyList<EffectiveTrade> trades)
    {
        var news = trades.Where(t => t.IsNew).ToList();
        var existing = trades.Where(t => !t.IsNew).ToList();
        if (news.Concat(existing).SelectMany(t => t.Packages).Distinct(StringComparer.Ordinal).GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList()).FirstOrDefault(names => names.Count > 1) is { } clash)
            throw new WorkspaceRefused("PackageNamesDiffer",
                $"The packages {TableWords.And(clash)} differ only in the case of their letters, and whether they are one is not guessed. Write one name on all of their trades.");
        if (news.Where(t => t.Packages.Count > 0 && !t.After).ToList() is { Count: > 0 } clear)
        {
            var (them, their) = Pronouns(clear);
            throw new WorkspaceRefused("CandidateNotTicked",
                $"{InPackages(clear)} and {(clear.Count == 1 ? "has" : "have")} A clear, and a package adds New rows with A ticked. Tick A on {them}, or clear {their} Package.");
        }
        if (news.Where(t => t.After && t.Before).ToList() is { Count: > 0 } inBefore)
        {
            var (are, them) = inBefore.Count == 1 ? ("is", "it") : ("are", "them");
            var ids = string.Join(", ", inBefore.Select(t => t.TradeId));
            // A trade alone may also be left out by clearing A; one in a package is then refused as above.
            throw new WorkspaceRefused("CandidateInBefore", inBefore.All(t => t.Packages.Count == 0)
                ? $"{ids} {are} in Before, and a new trade is priced alone only out of Before. Clear B on {them}, or A to leave {them} out, then add each again."
                : $"{ids} {are} in Before, and add each adds a new trade only out of Before. Clear B on {them}, then add each again.");
        }
        if (existing.Where(t => t.Packages.Count > 0 && !t.After).ToList() is { Count: > 0 } outside)
        {
            var (them, their) = Pronouns(outside);
            throw new WorkspaceRefused("NotInAfter",
                $"{InPackages(outside)} and not in After in the Base, so no case takes {them} out. Tick A on {them}, or clear {their} Package.");
        }
        // A trade After holds and Before does not may be in a package: its case holds it in neither, as if it had not
        // been added (ADR-0016 note 91, the owner's choice of 2026-11-14 over refusing it).
        var packages = Named(trades).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var set = new List<Iteration>();
        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in news.Concat(existing))
        {
            foreach (var name in t.Packages)
                if (made.Add(name)) set.Add(new(packages[name].Adds, packages[name].TakesOut, name));
            if (t.Packages.Count == 0 && t.IsNew && t.After) set.Add(new([t.TradeId], []));
        }
        return set;
    }

    // How a refusal's sentence speaks of the trades it names: one, or several.
    static (string Them, string Their) Pronouns(IReadOnlyList<EffectiveTrade> trades) => trades.Count == 1 ? ("it", "its") : ("them", "their");

    // The trades a refusal names and where they are: `DRAFT-3 is in the package P1`, `DRAFT-3 is in the packages P1 and
    // P2`, or `DRAFT-3 (P1) and DRAFT-4 (P1, P2) are in packages`.
    static string InPackages(IReadOnlyList<EffectiveTrade> trades) => trades.Count == 1
        ? $"{trades[0].TradeId} is in the {(trades[0].Packages.Count == 1 ? "package" : "packages")} {TableWords.And(trades[0].Packages)}"
        : $"{TableWords.And(trades.Select(t => $"{t.TradeId} ({string.Join(", ", t.Packages)})"))} are in {(trades.SelectMany(t => t.Packages).Distinct().Count() == 1 ? "a package" : "packages")}";
}
