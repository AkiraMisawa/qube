using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application.Products;

// ADR-0008: one generic create command, and products that are descriptions. The kernel reads what was typed against a
// product's description and asks the product's module only for what a description cannot say: its defaults, its
// mapping onto the entity, and whether the terms make economic sense. Nothing here knows a currency, a leg or a rate.

/// <param name="Name">The field's name in a reading, and what <c>name=value</c> takes.</param>
/// <param name="Aliases">Other names <c>name=value</c> takes (<c>fixed</c> for a fixed leg's frequency).</param>
/// <param name="Unnamed">Whether a word may fill it without its name, by its form alone (ADR-0008 §3).</param>
/// <param name="Sequence">
/// Fields of one sequence take unnamed words of their form in the order declared (a cross-currency swap's two
/// currencies). Unnamed fields that share a form and no sequence are ambiguous, and such a word is refused.
/// </param>
/// <param name="Choices">For <see cref="FieldForm.Choice"/>: each word the user may type, lower case, and the value it reads as.</param>
/// <param name="Examples">
/// Values a line offers for an unnamed field whose form names none, such as a product's usual periods (ADR-0016 note
/// 36). They are offered, never filled in: a field left out is filled by a default with a source, or is missing.
/// </param>
public sealed record ProductField(
    string Name,
    string Title,
    FieldForm Form,
    bool Required = false,
    bool Unnamed = false,
    string? Sequence = null,
    IReadOnlyDictionary<string, string>? Choices = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Examples = null)
{
    /// <summary>How the field's values are read and written, as every reader of the field takes it.</summary>
    public FieldSpec Spec => new(Form, Choices: Choices);
}

/// <summary>
/// A product the workbench can create (ADR-0008 §7): data, from a catalog. <see cref="App"/> and <see cref="Scope"/>
/// say where its entities live; <see cref="Words"/> is what follows <c>new</c> on the command line.
/// </summary>
/// <param name="Table">The registered table its entities are rows of, where it says one (ADR-0016 note 48).</param>
/// <param name="Under">
/// The field of its terms that names the row its entity is made under, where its table is under another (ADR-0016 note
/// 48): a line is made under an order. The first word after the product's own is read as that row where it names one.
/// </param>
public sealed record ProductDescriptor(
    string Id,
    string Version,
    string App,
    string Scope,
    string Words,
    string Title,
    IReadOnlyList<ProductField> Fields,
    string? Table = null,
    string? Under = null);

/// <summary>
/// Where descriptors come from (ADR-0008 §7). A module's own catalog in this repository; an adapter over the analytics
/// engine's product catalog at the company, with no kernel or UI change.
/// </summary>
public interface IProductCatalog
{
    IEnumerable<ProductDescriptor> Products { get; }
}

/// <summary>A value the user did not type, and where it came from (ADR-0008 §4): <c>profile:…</c>, <c>workspace</c>, <c>C47</c>.</summary>
public sealed record ProductDefault(JsonNode? Value, string Source);

/// <summary>
/// What a description cannot say, from the product's module (ADR-0008 §7): the defaults it may supply, and creating
/// the entity, which maps the terms onto its fields and checks that they make economic sense.
/// </summary>
public interface IProductRules
{
    string ProductId { get; }

    /// <summary>
    /// The values this product supplies for fields the user left out, given what was typed and the workspace. Only
    /// from a confirmed template, the workspace's own defaults or a confirmed decision; never from another entity,
    /// the last input or a market feed (ADR-0008 §4).
    /// </summary>
    IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given);

    /// <summary>
    /// Creates the entity from complete terms at the revision named (the current one when null), or refuses with every
    /// reason, writing nothing (<see cref="ProductTermsRefused"/>).
    /// </summary>
    (WorkspaceState State, string Entity) Create(string workspaceId, long? revision, string user, IReadOnlyDictionary<string, JsonNode?> terms,
        IReadOnlyDictionary<string, string> sources, RunOrigin origin);

    /// <summary>
    /// The entity <see cref="Create"/> would make of complete terms in a workspace as it stands, or its refusal with
    /// every reason (<see cref="ProductTermsRefused"/>). It writes nothing: what a line says before Enter (ADR-0016
    /// note 36).
    /// </summary>
    string WouldCreate(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> terms);

    /// <summary>
    /// The changes <see cref="Create"/> writes for complete terms on a workspace as it stands, and the entity they
    /// make (ADR-0016 note 54): what a draft folds without writing, refused as <see cref="Create"/> refuses. Null for
    /// a product whose making is more than a batch of changes: it is not made in a draft, and a draft says so.
    /// </summary>
    (IReadOnlyList<WorkspaceChange> Changes, string Entity)? Planned(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> terms, IReadOnlyDictionary<string, string> sources) => null;
}

/// <summary>Terms that do not make the product: every reason, by field where there is one. Nothing was written.</summary>
public sealed class ProductTermsRefused(IReadOnlyList<ProductIssue> issues) : Exception(string.Join(" ", issues.Select(i => i.Message)))
{
    public IReadOnlyList<ProductIssue> Issues { get; } = issues;
}

/// <param name="Field">The field it is about, or null for a word that fills none or the terms as a whole.</param>
public sealed record ProductIssue(string Code, string? Field, string Message);

/// <summary>What was typed, read against a description: each field's value, which came from a default and from where, and every issue.</summary>
public sealed record ProductReading(
    IReadOnlyDictionary<string, JsonNode?> Values,
    IReadOnlyDictionary<string, string> Sources,
    IReadOnlyList<string> Missing,
    IReadOnlyList<ProductIssue> Issues)
{
    public bool Complete => Missing.Count == 0 && Issues.Count == 0;
}

/// <summary>
/// Reads words against the terms something takes (ADR-0008 §3): a product's, and a template's (ADR-0016 note 50), which
/// are typed alike. Pure: the same words and terms read the same.
/// </summary>
public static class ProductReader
{
    /// <summary>The term a name typed before <c>=</c> is: by its own name or another of its names, in any case; none where the product has no such term.</summary>
    public static ProductField? Named(ProductDescriptor product, string name) => Named(product.Fields, name);

    /// <summary>As <see cref="Named(ProductDescriptor, string)"/>, among the terms given.</summary>
    public static ProductField? Named(IReadOnlyList<ProductField> fields, string name) =>
        fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (f.Aliases?.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? false));

    /// <summary>Splits the typed terms into words, and reads each by name or by form.</summary>
    public static (Dictionary<string, JsonNode?> Values, List<ProductIssue> Issues) Read(ProductDescriptor product, string text) =>
        Read(product.Words, product.Fields, text, Parse);

    /// <summary>
    /// As <see cref="Read(ProductDescriptor, string)"/>, for the terms of whatever <paramref name="words"/> names, each
    /// word read as <paramref name="parse"/> reads it: a product's as <see cref="Parse"/>, a template's as <see cref="Written"/>.
    /// </summary>
    public static (Dictionary<string, JsonNode?> Values, List<ProductIssue> Issues) Read(string words, IReadOnlyList<ProductField> fields, string text,
        Func<ProductField, string, Parsed> parse)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var issues = new List<ProductIssue>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = word.IndexOf('=');
            if (eq > 0)
            {
                var name = word[..eq];
                var field = Named(fields, name);
                if (field is null)
                {
                    issues.Add(new("UnknownTerm", null, fields.Count == 0 ? $"{words} takes no term; {name} is not read."
                        : $"{words} has no term named {name}; it takes {string.Join(", ", fields.Select(NameOf))}."));
                    continue;
                }
                Fill(field, word[(eq + 1)..]);
                continue;
            }
            var candidates = fields.Where(f => f.Unnamed && !values.ContainsKey(f.Name) && parse(f, word).Ok).ToList();
            if (candidates.Count == 0)
            {
                var amountMistake = fields.FirstOrDefault(f => f.Unnamed && f.Form == FieldForm.Amount && !values.ContainsKey(f.Name)
                    && FieldForms.Read(new FieldSpec(FieldForm.Amount), word).Problem == FieldForms.Problem.FullAmount);
                issues.Add(amountMistake is not null
                    ? new("InvalidTerm", amountMistake.Name, parse(amountMistake, word).Message!)
                    : new("UnreadWord", null, fields.Count == 0 ? $"{words} takes no term; '{word}' is not read."
                        : $"'{word}' is no term of {words} left to fill; name it as name=value."));
                continue;
            }
            var sequence = candidates[0].Sequence;
            if (candidates.Count > 1 && (sequence is null || candidates.Any(c => c.Sequence != sequence)))
            {
                issues.Add(new("AmbiguousWord", null, $"'{word}' could be {string.Join(" or ", candidates.Select(c => c.Name))}; write it as name=value."));
                continue;
            }
            Fill(candidates[0], word);
        }
        return (values, issues);

        void Fill(ProductField field, string text)
        {
            if (values.ContainsKey(field.Name))
            {
                issues.Add(new("RepeatedTerm", field.Name, $"{field.Name} is given twice."));
                return;
            }
            var parsed = parse(field, text);
            if (parsed.Ok) values[field.Name] = parsed.Value;
            else issues.Add(new("InvalidTerm", field.Name, parsed.Message!));
        }
    }

    /// <summary>
    /// Reads the words, fills what the product's defaults supply, and lists every required field still empty by name.
    /// </summary>
    public static ProductReading Complete(ProductDescriptor product, string text, Func<IReadOnlyDictionary<string, JsonNode?>, IReadOnlyDictionary<string, ProductDefault>> defaults)
    {
        var (values, issues) = Read(product, text);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, supplied) in defaults(values))
        {
            if (values.ContainsKey(name)) continue;
            if (product.Fields.All(f => f.Name != name))
                throw new InvalidOperationException($"The rules of {product.Id} default {name}, which its description does not have.");
            values[name] = supplied.Value?.DeepClone();
            sources[name] = supplied.Source;
        }
        var missing = product.Fields.Where(f => f.Required && (!values.TryGetValue(f.Name, out var v) || v is null)).Select(f => f.Name).ToList();
        return new(values, sources, missing, issues);
    }

    /// <summary>
    /// What the next word could be after the words done (ADR-0016 note 36), in the description's order: each value of
    /// an unnamed choice not yet filled, as its form writes it, where a sequence offers its next field alone; an
    /// unnamed field's examples; and <c>name=</c> for each field typed by its name. Each with the field's title.
    /// </summary>
    public static IReadOnlyList<(string Value, string Detail)> Offers(ProductDescriptor product, string done) => Offers(product.Words, product.Fields, done, Parse);

    /// <summary>As <see cref="Offers(ProductDescriptor, string)"/>, for the terms of whatever <paramref name="words"/> names.</summary>
    public static IReadOnlyList<(string Value, string Detail)> Offers(string words, IReadOnlyList<ProductField> fields, string done, Func<ProductField, string, Parsed> parse)
    {
        var filled = Read(words, fields, done, parse).Values;
        var offers = new List<(string Value, string Detail)>();
        void Offer(string value, ProductField field)
        {
            if (offers.All(o => o.Value != value)) offers.Add((value, field.Title));
        }
        foreach (var field in fields.Where(f => !filled.ContainsKey(f.Name)))
        {
            if (!field.Unnamed) Offer($"{field.Name}=", field);
            else if (field.Sequence is null || fields.First(f => f.Sequence == field.Sequence && !filled.ContainsKey(f.Name)) == field)
                foreach (var value in field.Choices is { } choices ? choices.Values.Distinct().Select(v => FieldForms.Write(field.Spec, v)) : field.Examples ?? [])
                    Offer(value, field);
        }
        return offers;
    }

    static string NameOf(ProductField f) => f.Name;

    public readonly record struct Parsed(bool Ok, JsonNode? Value, string? Message);

    static Parsed Fail(string message) => new(false, null, message);
    static Parsed Take(JsonNode? value) => new(true, value, null);

    /// <summary>
    /// One word as the field's form reads it (<see cref="FieldForms"/>, the one reader), with what a product adds: its
    /// amount is more than nothing, its numbers travel as doubles, and its sentences name the field.
    /// </summary>
    public static Parsed Parse(ProductField field, string text)
    {
        var t = text.Trim();
        var read = FieldForms.Read(field.Spec, t);
        if (read.Problem == FieldForms.Problem.None)
        {
            if (read.Value is not JsonValue value || !value.TryGetValue<decimal>(out var n)) return Take(read.Value);
            return field.Form == FieldForm.Amount && n == 0 ? Fail(AnAmount(field)) : Take((double)n);
        }
        return Fail(Unread(field, t, read));
    }

    /// <summary>
    /// One word as the field's form reads it and a table's field holds it (ADR-0016 note 50): a number stays the
    /// number read, and an amount of nothing is an amount. The sentences are <see cref="Parse"/>'s.
    /// </summary>
    public static Parsed Written(ProductField field, string text)
    {
        var t = text.Trim();
        var read = FieldForms.Read(field.Spec, t);
        return read.Problem == FieldForms.Problem.None ? Take(read.Value) : Fail(Unread(field, t, read));
    }

    // Why a word is not the field's value, by the field's name.
    static string AnAmount(ProductField field) => $"{field.Name} is an amount in millions (1000 is a billion), or with its scale: 3bn, 500mm.";

    static string Unread(ProductField field, string t, FieldForms.Reading read) => field.Form switch
    {
        FieldForm.Amount when read.Problem == FieldForms.Problem.FullAmount =>
            $"{field.Name} is in millions, so {t} would be {decimal.Parse(t.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture):#,0} million; write it with its scale, such as 1bn.",
        FieldForm.Amount => AnAmount(field),
        FieldForm.Choice => $"{field.Name} is one of {string.Join(", ", (field.Choices ?? throw new InvalidOperationException($"{field.Name} is a choice with no choices.")).Keys)}.",
        FieldForm.Number => $"{field.Name} is a number.",
        _ => $"{field.Name} is {read.What}.",
    };
}

/// <summary>
/// The products the workbench can create, and creating one (ADR-0008 §1): read what was typed, fill the defaults,
/// refuse every missing or unreadable term by name, then let the product's rules write one <c>Create</c> edit.
/// </summary>
public sealed class ProductService
{
    readonly Dictionary<string, ProductDescriptor> products;
    readonly Dictionary<string, IProductRules> rules;
    readonly WorkspaceService workspaces;
    readonly TableRegister? tables;

    /// <param name="tables">The registered tables, which a product made under a row is read with (ADR-0016 note 48).</param>
    public ProductService(IEnumerable<IProductCatalog> catalogs, IEnumerable<IProductRules> rules, WorkspaceService workspaces, TableRegister? tables = null)
    {
        this.tables = tables;
        products = [];
        foreach (var p in catalogs.SelectMany(c => c.Products))
        {
            if (!products.TryAdd(p.Id, p)) throw new InvalidOperationException($"Product {p.Id} is described twice.");
            if (p.Fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Fields.Count)
                throw new InvalidOperationException($"Product {p.Id} names a field twice.");
            if (p.Fields.Any(f => f.Form == FieldForm.Choice && f.Choices is not { Count: > 0 }))
                throw new InvalidOperationException($"Product {p.Id} has a choice with nothing to choose.");
            // A product made under a row (ADR-0016 note 48) names that row by a term of its own, and says its table.
            if (p.Under is { } under)
            {
                if (p.Fields.All(f => f.Name != under)) throw new InvalidOperationException($"Product {p.Id} is made under the row its term {under} names, and has no such term.");
                if (p.Table is not { } table) throw new InvalidOperationException($"Product {p.Id} is made under a row and does not say the table its rows are of.");
                if (tables is null) throw new InvalidOperationException($"Product {p.Id} is made under a row, and the products were given no tables to read it in.");
                // Which table that one is under is a workspace's to describe, so it is checked where the terms are read.
                if (!tables.Registers(p.App, table)) throw new InvalidOperationException($"Product {p.Id} makes rows of {table}, which no table of {p.App} registers.");
            }
        }
        if (products.Values.GroupBy(p => (p.App, p.Words)).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"Products {string.Join(" and ", twice.Select(p => p.Id))} are both '{twice.Key.Words}'.");
        this.rules = rules.ToDictionary(r => r.ProductId);
        foreach (var id in this.rules.Keys.Where(id => !products.ContainsKey(id)))
            throw new InvalidOperationException($"Rules are registered for {id}, which no catalog describes.");
        this.workspaces = workspaces;
    }

    /// <summary>The products of an app that can be created here: described, with rules to create them.</summary>
    public IEnumerable<ProductDescriptor> Creatable(string? app = null) =>
        products.Values.Where(p => rules.ContainsKey(p.Id) && (app is null || p.App == app)).OrderBy(p => p.Id, StringComparer.Ordinal);

    /// <summary>A described product by its id; one no catalog describes is refused.</summary>
    public ProductDescriptor Of(string productId) =>
        products.TryGetValue(productId, out var product) ? product : throw new WorkspaceRefused("UnknownProduct", $"No product {productId} is described.");

    /// <summary>
    /// What the words would make in a workspace as it stands (ADR-0016 note 36): the reading with the product's defaults
    /// filled, and, where it is complete, the entity the product's rules would make of it. Nothing is written. Terms the
    /// rules refuse are refused here as <see cref="Create"/> would refuse them (<see cref="ProductTermsRefused"/>).
    /// <c>Terms</c> are the words as a request holds them, the row the entity is made under named (<see cref="Terms"/>).
    /// </summary>
    public (string Terms, ProductReading Reading, string? Entity) Would(WorkspaceState state, string productId, string text)
    {
        var (product, rule) = Described(productId, state);
        var terms = Terms(state, product, text);
        var reading = ProductReader.Complete(product, terms, given => rule.Defaults(state, given));
        return (terms, reading, reading.Complete ? rule.WouldCreate(state, reading.Values) : null);
    }

    /// <summary>Creates one entity of the product from what was typed, at the revision the caller saw (the current one when null).</summary>
    public (WorkspaceState State, string Entity, ProductReading Reading) Create(string workspaceId, long? revision, string user, string productId, string text, RunOrigin origin)
    {
        var (product, rule, state) = Resolve(workspaceId, user, productId);
        var reading = Whole(state, product, rule, text);
        var (created, entity) = rule.Create(workspaceId, revision, user, reading.Values, reading.Sources, origin);
        return (created, entity, reading);
    }

    /// <summary>
    /// The changes making one entity of the product from what was typed would write on a workspace as it stands, and
    /// the entity (ADR-0016 note 54): what a draft folds, and nothing written. Refused as <see cref="Create"/> refuses,
    /// and for a product that is not made in a draft.
    /// </summary>
    public (IReadOnlyList<WorkspaceChange> Changes, string Entity) Planned(WorkspaceState state, string productId, string text)
    {
        var (product, rule) = Described(productId, state);
        var reading = Whole(state, product, rule, text);
        return rule.Planned(state, reading.Values, reading.Sources)
            ?? throw new WorkspaceRefused("NotOfADraft", $"A {product.Words} is not made in a draft: make it with new outside one.");
    }

    // What was typed with the product's defaults filled, refused where a term is still missing or cannot be read.
    ProductReading Whole(WorkspaceState state, ProductDescriptor product, IProductRules rule, string text)
    {
        var reading = ProductReader.Complete(product, Terms(state, product, text), given => rule.Defaults(state, given));
        return reading.Complete ? reading
            : throw new ProductTermsRefused([.. reading.Issues, .. reading.Missing.Select(m => new ProductIssue("MissingTerm", m, $"{m} is missing: type it, or set a default for it."))]);
    }

    /// <summary>
    /// The terms as typed with the row the entity is made under named (ADR-0016 note 48). The row is said by the term
    /// that names it, typed by a name of its own (<c>station=s1</c>), or else by the terms' first word where that names
    /// one of the rows of the table above. Either is read as that table's ids are read (whole, or by how it ends, in
    /// any case) and written by the term's name and the row's whole key, so what is invoked and logged names the row
    /// exactly. Words that name no row are answered as they are and read as any terms, so the product's rules refuse a
    /// row that is not there; words whose end names several rows are refused with them, as a selector refuses them
    /// (<see cref="TableRefused"/>).
    /// </summary>
    public string Terms(WorkspaceState state, ProductDescriptor product, string text)
    {
        if (product.Under is not { } under) return text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return text;
        // The term typed by its name wins over the first word, which is then a term like any other.
        var named = Array.FindIndex(words, w => w.IndexOf('=') is > 0 and var eq && ProductReader.Named(product, w[..eq])?.Name == under);
        var said = named < 0 ? words[0] : words[named][(words[named].IndexOf('=') + 1)..];
        var bound = (tables ?? throw new InvalidOperationException($"Product {product.Id} is made under a row, and the products were given no tables to read it in.")).Of(state);
        var own = bound.FirstOrDefault(t => t.Descriptor.TableId == product.Table) ?? throw new InvalidOperationException($"Product {product.Id} makes rows of {product.Table}, which this workspace has not.");
        var above = bound.Single(t => t.Descriptor.TableId == (own.Descriptor.Under ?? throw new InvalidOperationException($"Product {product.Id} is made under a row, and {product.Table} is under no table.")));
        return Selectors.RowsNamed(said, new Selectors.RowIndex(above, above.Table.Rows(state))) switch
        {
            [var one] when named < 0 => string.Join(' ', [$"{under}={one.Key}", .. words.Skip(1)]),
            [var one] => string.Join(' ', words.Select((w, i) => i == named ? $"{under}={one.Key}" : w)),
            [] => text,
            var several => throw Selectors.AmbiguousRow(said, several, above.Word),
        };
    }

    (ProductDescriptor, IProductRules, WorkspaceState) Resolve(string workspaceId, string user, string productId)
    {
        var state = workspaces.Find(workspaceId);
        var (product, rule) = Described(productId, state);
        if (state is null) throw new WorkspaceRefused("UnknownWorkspace", $"No {product.App} workspace {workspaceId}.");
        if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
        return (product, rule, state);
    }

    // The product and its rules, for a workspace of its application where one is named.
    (ProductDescriptor, IProductRules) Described(string productId, WorkspaceState? state)
    {
        var product = Of(productId);
        if (!rules.TryGetValue(productId, out var rule)) throw new WorkspaceRefused("NotCreatable", $"{product.Title} is described, but nothing here can create it yet.");
        if (state is not null && state.App != product.App) throw new WorkspaceRefused("UnknownWorkspace", $"No {product.App} workspace {state.WorkspaceId}.");
        return (product, rule);
    }
}
