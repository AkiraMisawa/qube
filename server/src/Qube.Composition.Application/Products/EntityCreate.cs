using System.Text.Json.Nodes;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Products;

/// <summary>
/// <c>entity.create</c>, typed <c>new &lt;product&gt; &lt;terms&gt;</c> (ADR-0008 §1, §5; ADR-0016 note 36): one entity
/// of a described product in a workspace of the caller's, from the terms as a person types them. A command
/// (ADR-0005): it acts for the caller the server names, through the same service the page uses, and refuses every
/// missing or unreadable term by name. Its reading of a line fills the product's defaults and says the entity the
/// terms would make and where each default came from, and writes nothing.
/// </summary>
public sealed class EntityCreate(Func<ProductService> products) : ICapabilityHandler, ILineCommand
{
    public const string Id = "entity.create";
    public const string Request = "entity.create-request";
    public const string Created = "entity.created";
    public const string Version = "1.0.0";

    public CapabilityDescriptor Descriptor { get; } = new(Id, Version, "Create an entity of a described product",
        [new("request", Request, Version, Cardinality.One)],
        [new("entity", Created, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["new"], [new("product", CommandArgumentKind.Text, Required: true), new("terms", CommandArgumentKind.Text, Required: false)]));

    public LineRead Read(LineContext line)
    {
        var service = products();
        var creatable = service.Creatable(line.State.App).ToList();
        var names = creatable.Select(p => p.Words).ToList();
        if (creatable.Count == 0) throw new TableRefused("NoProduct", "new makes nothing in this workspace: no product is described for it.");
        var words = LineWords.Of(line.Text);
        if (words.Count == 0) throw new TableRefused("NoProduct", $"new takes a product: {TableWords.Or(names)}.", null, names);
        if (Named(creatable, [.. words.Select(w => w.Word)]) is not (var product, var own))
            throw new TableRefused("UnknownProduct", $"{words[0].Word} is no product new makes here: {TableWords.Or(names)}.", words[0].Word, names);
        var typed = line.Text[(words[own - 1].Start + words[own - 1].Word.Length)..].Trim();
        var (terms, reading, entity) = Would(service, line.State, product, typed);
        if (reading.Issues.Count > 0) throw new TableRefused(reading.Issues[0].Code, string.Join(" ", reading.Issues.Select(i => i.Message)));
        if (reading.Missing.Count > 0)
            throw new TableRefused("MissingTerm", reading.Missing.Count == 1
                ? $"{product.Words} still needs {reading.Missing[0]}: type it, or set a default for it."
                : $"{product.Words} still needs {TableWords.And(reading.Missing)}: type them, or set a default for each.");
        // Each term in the description's order, as its form writes it: the typed ones are the arguments, and the
        // rest are said with where they came from (ADR-0008 §4).
        var written = product.Fields.Where(f => reading.Values.GetValueOrDefault(f.Name) is not null)
            .Select(f => (Field: f, Text: FieldForms.Write(f.Spec, reading.Values[f.Name]))).ToList();
        var defaulted = written.Where(w => reading.Sources.ContainsKey(w.Field.Name)).Select(w => $"{w.Field.Name} {w.Text} from {reading.Sources[w.Field.Name]}").ToList();
        var request = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision, ["product"] = product.Id, ["terms"] = terms };
        return new(new JsonObject { ["request"] = request },
            [new("product", product.Words), .. written.Where(w => !reading.Sources.ContainsKey(w.Field.Name)).Select(w => new LineArgument(w.Field.Name, w.Text))],
            null, $"Makes {entity}: {product.Title}.{(defaulted.Count == 0 ? "" : $" Not typed: {TableWords.List(defaulted)}.")}");
    }

    // What the terms would make, with the terms as the request holds them: the row the entity is made under named
    // (note 48). What the product's rules refuse of complete terms is the line's refusal, in their words.
    static (string Terms, ProductReading Reading, string? Entity) Would(ProductService service, WorkspaceState state, ProductDescriptor product, string typed)
    {
        var terms = service.Terms(state, product, typed);
        try
        {
            var (reading, entity) = service.Would(state, product.Id, terms);
            return (terms, reading, entity);
        }
        catch (ProductTermsRefused refused)
        {
            throw new TableRefused("InvalidTerms", refused.Message);
        }
    }

    public LineCompletions Complete(LineContext line)
    {
        var creatable = products().Creatable(line.State.App).ToList();
        var (done, word, start) = LineWords.Typing(line.Text);
        if (Named(creatable, done) is (var product, var own))
        {
            // A value after `name=` is the user's to type: its field says what it is, and no list holds it.
            if (word.Contains('=')) return LineCompletions.None;
            return new([.. ProductReader.Offers(product, string.Join(' ', done.Skip(own)))
                .Where(o => o.Value.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Select(o => new LineCompletion(o.Value, start, o.Detail))]);
        }
        // A product's words half typed: the products whose words begin with them, written from the first of them.
        var typed = TableWords.Of(string.Join(' ', [.. done, word]));
        var from = LineWords.Of(line.Text) is [var first, ..] ? first.Start : start;
        return new([.. creatable.Where(p => TableWords.Of(p.Words).StartsWith(typed, StringComparison.Ordinal)).Select(p => new LineCompletion(p.Words, from, p.Title))]);
    }

    // The product the words begin with, by its own words in any case, the longest first, and how many words name it.
    static (ProductDescriptor Product, int Words)? Named(IReadOnlyList<ProductDescriptor> creatable, IReadOnlyList<string> words)
    {
        foreach (var product in creatable.OrderByDescending(p => p.Words.Length))
        {
            var own = TableWords.Of(product.Words).Split(' ');
            if (words.Count >= own.Length && own.Select((w, i) => words[i].Equals(w, StringComparison.OrdinalIgnoreCase)).All(same => same))
                return (product, own.Length);
        }
        return null;
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"]!;
        var product = request["product"]!.GetValue<string>();
        var workspaceId = request["workspaceId"]!.GetValue<string>();
        var text = request["terms"]?.GetValue<string>() ?? "";
        try
        {
            var service = products();
            var (state, entity, reading) = service.Create(workspaceId, request["revision"]?.GetValue<long>(), caller.UserId, product, text, caller.Origin);
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
            {
                ["entity"] = new JsonObject
                {
                    ["workspaceId"] = state.WorkspaceId, ["entity"] = entity, ["revision"] = state.Revision, ["product"] = product,
                    ["said"] = $"Made {entity}: {service.Of(product).Title}.",
                    // Flat, as every contract is: "field from source, …", as the edit log says it.
                    ["defaulted"] = string.Join(", ", reading.Sources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} from {p.Value}")),
                },
            });
        }
        catch (ProductTermsRefused refused)
        {
            throw new CapabilityFailure("InvalidTerms", refused.Message,
                [.. refused.Issues.Select(i => new CompositionIssue(i.Code, i.Field is null ? "/inputs/request/terms" : $"/inputs/request/terms/{i.Field}", i.Message))]);
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
        // A row above that the terms' first word could not say (note 48).
        catch (TableRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message, [new CompositionIssue(refused.Code, "/inputs/request/terms", refused.Message)]);
        }
    }
}

/// <summary>The kernel's own command for described products, and its contracts. Knows no product.</summary>
public sealed class ProductsModule(Func<ProductService> products) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new EntityCreate(products));
        builder.AddContract(new ContractDefinition(EntityCreate.Request, EntityCreate.Version, "Create an entity", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["product"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Product, such as rates.irs" },
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace" },
                ["terms"] = new JsonObject { ["type"] = "string", ["title"] = "Terms as typed after new <product>, such as 10y 1000 pay" },
                ["revision"] = CommandRequests.RevisionField(),
            },
            ["required"] = new JsonArray("product", "workspaceId"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition(EntityCreate.Created, EntityCreate.Version, "Entity created", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["entity"] = new JsonObject { ["type"] = "string", ["title"] = "Entity" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["product"] = new JsonObject { ["type"] = "string", ["title"] = "Product" },
                ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What was made, as the command line says it" },
                ["defaulted"] = new JsonObject { ["type"] = "string", ["title"] = "Fields filled by a default, and from where" },
            },
            ["required"] = new JsonArray("workspaceId", "entity", "revision", "product", "said", "defaulted"),
            ["additionalProperties"] = false,
        }));
    }
}
