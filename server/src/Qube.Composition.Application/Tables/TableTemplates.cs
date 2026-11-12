using System.Text.Json.Nodes;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application.Tables;

/// <summary>
/// A template as <c>set</c> says it after its rows (ADR-0016 note 50): <c>as &lt;template&gt; [terms]</c>. The template
/// is named by its words, the longest run that names one; its terms are read as a product's are, by name or by form in
/// any order (<see cref="ProductReader"/>). A refusal names the word at fault where it is about one, and writes nothing.
/// </summary>
static class Templates
{
    /// <summary>The word that says a template follows.</summary>
    public const string Word = SelectorReader.AsWord;

    /// <param name="Says">The template and its terms as a reading and a log name them: its name, then each term given as <c>name=value</c> in the order declared.</param>
    public sealed record Said(TableTemplate Template, IReadOnlyDictionary<string, JsonNode?> Terms, string Says);

    /// <summary>Whether what follows the rows says a template: its first word is <c>as</c>, in any case.</summary>
    public static bool Leads(string set) => After(set) is not null;

    /// <summary>Whether the words after <c>as</c> name a template of the table, so the text is meant for it.</summary>
    public static bool Names(string set, BoundTable table) =>
        After(set) is { } words && table.Descriptor.Templates is { } templates && Named(templates, words) is not null;

    /// <summary>The template the text names on the table, and its terms as read; refused by name where it names none, or its terms do not read.</summary>
    public static Said Read(string set, BoundTable table)
    {
        var words = After(set) ?? throw new InvalidOperationException($"'{set}' says no template.");
        var templates = table.Descriptor.Templates ?? [];
        if (templates.Count == 0)
            throw new TableRefused("NoTemplates", $"{table.Word} has no template: set takes a field and its value.", Word);
        IReadOnlyList<string> names = [.. templates.Select(t => t.Name)];
        if (words.Length == 0)
            throw new TableRefused("UnknownTemplate", $"as takes a template of {table.Word}: {TableWords.Or(names)}.", Word, names);
        if (Named(templates, words) is not var (template, own))
            throw new TableRefused("UnknownTemplate", $"{table.Word} has no template {words[0]}; its templates are {TableWords.And(names)}.", words[0], names);

        var (values, issues) = ProductReader.Read(template.Name, template.Takes, string.Join(' ', words.Skip(own)), ProductReader.Written);
        if (issues.Count > 0) throw new TableRefused("InvalidTerms", string.Join(" ", issues.Select(i => i.Message)));
        var missing = template.Takes.Where(f => f.Required && !values.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        if (missing.Count > 0)
            throw new TableRefused("MissingTerm", $"{template.Name} still needs {TableWords.And(missing)}: type {(missing.Count == 1 ? "it" : "them")} after it.");
        var given = template.Takes.Where(f => values.ContainsKey(f.Name)).Select(f => $"{f.Name}={FieldForms.Write(f.Spec, values[f.Name])}");
        return new(template, values, string.Join(' ', [template.Name, .. given]));
    }

    /// <summary>
    /// What a template writes over a row, as its table answers for the terms given: each field it declares, in the order
    /// declared, and no other. An answer that names another field, or leaves one out, is the registration's defect.
    /// </summary>
    public static IReadOnlyList<(TableField Field, JsonNode? Value)> Writes(WorkspaceState state, BoundTable table, Said said)
    {
        var answer = table.Table.Template(state, said.Template, said.Terms);
        var (declared, answered) = (said.Template.Writes.ToHashSet(StringComparer.Ordinal), answer.Keys.ToHashSet(StringComparer.Ordinal));
        if (!declared.SetEquals(answered))
            throw new InvalidOperationException($"Template {said.Template.Name} of {table.Descriptor.TableId} declares {TableWords.List(said.Template.Writes)} "
                + $"and its table answered {TableWords.List(answer.Keys)}.");
        return [.. said.Template.Writes.Select(name => (table.Descriptor.Fields.Single(f => f.Name == name), answer[name]))];
    }

    // The words after `as`, where the text leads with it.
    static string[]? After(string set)
    {
        var words = set.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && SelectorReader.Is(words[0], Word) ? words[1..] : null;
    }

    // The template the words begin with, by any of its words in any case, the longest run first, and how many words name it.
    static (TableTemplate Template, int Words)? Named(IReadOnlyList<TableTemplate> templates, IReadOnlyList<string> words)
    {
        var said = templates.SelectMany(t => t.Words.Append(t.Name).Select(TableWords.Of).Distinct().Select(w => (Template: t, Own: w.Split(' '))));
        foreach (var (template, own) in said.OrderByDescending(x => x.Own.Length))
            if (words.Count >= own.Length && own.Select((w, i) => words[i].Equals(w, StringComparison.OrdinalIgnoreCase)).All(same => same))
                return (template, own.Length);
        return null;
    }
}
