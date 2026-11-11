using System.Text.RegularExpressions;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Tables;

/// <summary>
/// What could follow the last word of a line over registered tables, or complete it where it is half typed (ADR-0016
/// §5, note 15). The rows are read by the selector's one reader, as far as they go, and what is offered is what that
/// reading awaits: ways to say rows, the verb's fields, a condition's fields and operators, and the values a field has
/// to choose from. Offering is not checking: a line made of what was offered is still read, and may be refused.
/// </summary>
static partial class TableCompletions
{
    // Rows are offered a few at a time; a table may have ten thousand.
    const int RowsListed = 20;

    /// <param name="afterRows">What the verb takes after its rows: values for fields, one field, or nothing.</param>
    public static LineCompletions Of(string text, IReadOnlyList<BoundTable> tables, WorkspaceState state, CommandArgumentKind? afterRows)
    {
        var words = WordPattern().Matches(text).Select(m => (Text: m.Value, Start: m.Index)).ToList();
        // No space after the command's own words: they are still being typed, and nothing follows yet.
        if (text.Length == 0) return LineCompletions.None;
        var typing = !char.IsWhiteSpace(text[^1]);
        var last = typing ? words[^1] : (Text: "", Start: text.Length);
        var done = typing ? words[..^1] : words;
        try
        {
            return new Offer(tables, state, afterRows, done, last.Text, last.Start).All();
        }
        catch (TableRefused)
        {
            // What stands before the last word cannot be read: the refusal says so, and nothing is offered after it.
            return LineCompletions.None;
        }
    }

    sealed class Offer(IReadOnlyList<BoundTable> tables, WorkspaceState state, CommandArgumentKind? afterRows, IReadOnlyList<(string Text, int Start)> done, string typed, int at)
    {
        readonly List<LineCompletion> items = [];
        readonly Dictionary<BoundTable, IReadOnlyList<string>> keys = [];
        int more;

        public LineCompletions All()
        {
            var words = done.Select(w => w.Text).ToArray();
            var scan = SelectorReader.Scan(words, [.. tables.Select(t => t.Descriptor)]);
            var selector = scan.Selector;
            var said = selector.Table is { } id ? tables.First(t => t.Descriptor.TableId == id) : null;
            var main = tables.FirstOrDefault(t => t.Descriptor.Main);

            if (scan.Open is { } open)
            {
                if ((said ?? main) is { } table) InCondition(table, open);
            }
            else if (scan.Taken < words.Length)
            {
                // Words after the rows are the verb's own.
                var pending = done.Skip(scan.Taken).ToList();
                foreach (var table in TablesOf(selector, said, main, string.Join(' ', pending.Select(w => w.Text)))) AfterRows(table, pending);
            }
            else if (selector.Where.Count > 0)
            {
                var condition = selector.Where[^1];
                var table = said ?? main ?? throw new InvalidOperationException("A condition was read against a table.");
                // A list after `in` goes on after a comma.
                if (condition.Operator == SelectorReader.InWord && (words[^1].EndsWith(',') || typed.Contains(',')))
                    Values(TableRegister.Field(table, condition.Field), [.. condition.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
                else
                {
                    Keyword(SelectorReader.AndWord);
                    AfterRows(table, []);
                }
            }
            else if (selector.Ids.Count > 0 && (words[^1].EndsWith(',') || typed.StartsWith(',')))
            {
                Rows(said is null ? null : [said], selector.Ids);
            }
            else if (selector.Ids.Count > 0)
            {
                AfterRows(Selectors.Resolve(selector, tables, state).Table, []);
            }
            else if (selector.All || selector.Subset is not null)
            {
                AfterRows(said ?? main ?? throw new InvalidOperationException("All and a subset were read against a table."), []);
                Keyword(SelectorReader.WhereWord);
            }
            else if (said is { Descriptor.SingleRow: true })
            {
                AfterRows(said, []);
            }
            else
            {
                RowsAwaited(said, main);
            }
            return new(items, more);
        }

        // Where the rows are still to be said: for a table that was named, its own ways; else the main table's, the
        // tables, the fields of a table of one row, which need no row, and rows of every table.
        void RowsAwaited(BoundTable? said, BoundTable? main)
        {
            // An id list that is typed with no space goes on in the same word: `R1,R`.
            if (typed.Contains(','))
            {
                Rows(said is null ? null : [said], [.. typed.Split(',')[..^1].Where(i => i.Length > 0)]);
                return;
            }
            Keyword(SelectorReader.AllWord);
            foreach (var subset in (said ?? main)?.Descriptor.Subsets ?? []) Keyword(subset.Name, TableWords.Names(subset));
            Keyword(SelectorReader.WhereWord);
            if (said is null)
            {
                foreach (var table in tables) Keyword(table.Word, TableWords.Names(table.Descriptor));
                if (afterRows is not null)
                    foreach (var table in tables.Where(t => t.Descriptor.SingleRow))
                        foreach (var field in Fields(table, [], editable: true)) items.Add(new(TableWords.Word(field), at, table.Word));
            }
            Rows(said is null ? null : [said], []);
        }

        // Rows by their ids, as far as typed: an id starts or ends with it, as the selector reads one. With no table
        // named, a row two tables have is offered with its table, since its id alone is refused; in a list, the rows of
        // the table the first id is in.
        void Rows(IReadOnlyList<BoundTable>? within, IReadOnlyList<string> listed)
        {
            var lead = typed.LastIndexOf(',') is >= 0 and var comma ? typed[..(comma + 1)] : "";
            var id = typed[lead.Length..];
            var searched = within ?? (listed.Count > 0 ? [Selectors.Resolve(new RowSelector(Ids: listed), tables, state).Table] : [.. tables.Where(t => !t.Descriptor.SingleRow)]);
            // An id that another table has too is not one a line can say alone.
            var elsewhere = within is not null ? null
                : tables.SelectMany(Keys).GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var taken = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
            var found = 0;
            foreach (var table in searched)
                foreach (var key in Keys(table))
                {
                    if (taken.Contains(key) || !(key.StartsWith(id, StringComparison.OrdinalIgnoreCase) || key.EndsWith(id, StringComparison.OrdinalIgnoreCase))) continue;
                    var shared = elsewhere?.Contains(key) == true;
                    // In a list the table is already said by its first row, and a row another table has cannot join it.
                    if (shared && (listed.Count > 0 || lead.Length > 0)) continue;
                    if (++found > RowsListed) continue;
                    items.Add(shared ? new($"{table.Word} {key}", at) : new(lead + key, at, within is null && listed.Count == 0 && lead.Length == 0 ? table.Word : null));
                }
            more += Math.Max(0, found - RowsListed);
        }

        // After the rows, what the verb takes: for values, a field and then what it has to choose from; for a field, one.
        void AfterRows(BoundTable table, IReadOnlyList<(string Text, int Start)> pending)
        {
            if (afterRows is null) return;
            var said = pending.Select(w => w.Text).ToList();
            if (afterRows == CommandArgumentKind.Assignments && (typed.Contains('=') || said.Any(w => w.Contains('='))))
            {
                Signed(table, said);
                return;
            }
            // The words name one field: its value is awaited, where the verb takes one.
            if (Assignments.Fields(table, string.Join(' ', said)) is [var one])
            {
                if (afterRows == CommandArgumentKind.Assignments) Values(one, []);
                return;
            }
            // No word yet, or words that start a field's name or name several: the fields they could be, written from their first word.
            foreach (var field in Fields(table, said, editable: true)) items.Add(new(TableWords.Word(field), pending.Count > 0 ? pending[0].Start : at));
        }

        // Values with their signs: after a sign the field's values, and between pairs the fields not given yet.
        void Signed(BoundTable table, IReadOnlyList<string> said)
        {
            var sign = typed.IndexOf('=');
            if (sign < 0)
            {
                // A value before this word is taken to be whole; a field's name starts here.
                if (said.Count > 0 && said[^1].EndsWith('=')) return;
                var given = said.Where(w => w.IndexOf('=') > 0).SelectMany(w => Assignments.Fields(table, w[..w.IndexOf('=')])).ToHashSet();
                foreach (var field in Fields(table, [], editable: true).Where(f => !given.Contains(f))) items.Add(new($"{TableWords.Word(field)}=", at));
                return;
            }
            // The name is the longest run of words before the sign that names a field, as the values are read.
            var before = said.AsEnumerable().Reverse().TakeWhile(w => !w.Contains('=')).Reverse().Append(typed[..sign]).Where(w => w.Length > 0).ToList();
            var named = Enumerable.Range(0, before.Count).Select(from => Assignments.Fields(table, string.Join(' ', before.Skip(from)))).FirstOrDefault(f => f.Count > 0);
            if (named is [var one]) Values(one, [], lead: typed[..(sign + 1)]);
        }

        // In a condition: its field, then the operators the field takes, then its values.
        void InCondition(BoundTable table, OpenCondition open)
        {
            var sign = SelectorReader.OperatorPattern().Match(typed);
            if (open.Operator is null && sign.Success)
            {
                // The operator is in the word being typed: `unit=`, or `=` after the field's words.
                var name = string.Join(' ', sign.Index > 0 ? open.Field.Append(typed[..sign.Index]) : open.Field);
                if (Assignments.Fields(table, name) is [var compared]) Values(compared, [], lead: typed[..(sign.Index + sign.Length)]);
                return;
            }
            var named = Assignments.Fields(table, string.Join(' ', open.Field));
            if (open.Operator is not null)
            {
                if (named is [var compared]) Values(compared, []);
            }
            else if (named is [var one])
            {
                foreach (var op in Conditions.OperatorsOf(one)) Keyword(op);
            }
            else
            {
                // The field's words so far are the last the line has before the word being typed.
                foreach (var field in Fields(table, open.Field, editable: false)) items.Add(new(TableWords.Word(field), open.Field.Count > 0 ? done[^open.Field.Count].Start : at));
            }
        }

        // What a field has to choose from: a flag's two words, a choice's values as they are written, the rows of the
        // table it points into, and `empty` where it may be empty. A field of any other form is typed freely.
        void Values(TableField field, IReadOnlyList<string> listed, string lead = "")
        {
            if (lead.Length == 0 && typed.LastIndexOf(',') is >= 0 and var comma) lead = typed[..(comma + 1)];
            var value = typed[lead.Length..];
            IEnumerable<string> values = field.Form switch
            {
                FieldForm.Flag => ["on", "off"],
                FieldForm.Choice when field.Choices is { } choices => choices.Values.Distinct().Select(v => FieldForms.Write(field.Spec, v)),
                _ when field.Table is { } pointed => tables.Where(t => t.Descriptor.TableId == pointed).SelectMany(Keys),
                _ => [],
            };
            if (field.Optional) values = values.Append("empty");
            var taken = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
            var found = 0;
            foreach (var one in values.Where(v => !taken.Contains(v) && v.StartsWith(value, StringComparison.OrdinalIgnoreCase)))
                if (++found <= RowsListed) items.Add(new(lead + one, at));
            more += Math.Max(0, found - RowsListed);
        }

        // A word of the line's own, or one a registration gives, where any of the names it answers to starts with what is typed.
        void Keyword(string word, IEnumerable<string>? names = null)
        {
            if ((names ?? [word]).Any(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase))) items.Add(new(word, at));
        }

        // The fields a name could be, as far as it is typed: whole words before, and the start of a word after them.
        IEnumerable<TableField> Fields(BoundTable table, IEnumerable<string> before, bool editable)
        {
            var whole = TableWords.Of(string.Join(' ', before));
            var start = whole.Length == 0 ? typed.ToLowerInvariant() : $"{whole} {typed.ToLowerInvariant()}";
            return table.Descriptor.Fields.Where(f => (f.Editable || !editable) && TableWords.Names(f).Any(n => n.StartsWith(start, StringComparison.Ordinal) || (typed.Length == 0 && n == whole)));
        }

        IReadOnlyList<string> Keys(BoundTable table) => keys.TryGetValue(table, out var read) ? read : keys[table] = [.. table.Table.Rows(state).Select(r => r.Key)];

        // The table the verb's words are read against: the one the rows say, or, with no rows, each table of one row that has the field they start with.
        IEnumerable<BoundTable> TablesOf(RowSelector selector, BoundTable? said, BoundTable? main, string pending)
        {
            if (said is not null) return [said];
            if (selector.Ids.Count > 0) return [Selectors.Resolve(selector with { Where = [] }, tables, state).Table];
            if (!selector.SaysNoRows) return main is null ? [] : [main];
            return tables.Where(t => t.Descriptor.SingleRow && Assignments.StartsWithAFieldOf(pending, t));
        }
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordPattern();
}
