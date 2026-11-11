using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Settings;

/// <summary>
/// The three commands of a user's settings (ADR-0017 §4): read, set and reset, for the caller alone, through the one
/// gateway, so the command line, the settings dialog and the AI share one declaration and one refusal.
/// </summary>
public static class SettingsContracts
{
    public const string Version = "1.0.0";
    public const string ReadRequest = "settings.read-request";
    public const string ReadAnswer = "settings.listed";
    public const string SetRequest = "settings.set-request";
    public const string ResetRequest = "settings.reset-request";
    public const string Changed = "settings.changed";

    static JsonObject Text(string title) => new() { ["type"] = "string", ["title"] = title };

    static JsonObject Texts(string title) => new() { ["type"] = "array", ["title"] = title, ["items"] = new JsonObject { ["type"] = "string" } };

    static JsonObject Flag(string title) => new() { ["type"] = "boolean", ["title"] = title };

    static JsonObject Record(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
        ["additionalProperties"] = false,
    };

    internal static void Register(CompositionRegistryBuilder builder)
    {
        const string Setting = "The setting, by its words (theme, display currency) or its key; a member of a family by the family's words and its name (widths rates/lines)";
        builder.AddContract(new ContractDefinition(ReadRequest, Version, "Read your settings", Record(new JsonObject
        {
            ["setting"] = Text($"{Setting}; every setting where left out"),
        })));
        builder.AddContract(new ContractDefinition(ReadAnswer, Version, "Your settings", Record(new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["type"] = "array",
                ["title"] = "Each setting: what it is, its value, and whether you set it",
                ["items"] = Record(new JsonObject
                {
                    ["key"] = Text("Its key"),
                    ["name"] = Text("What a line names it by"),
                    ["title"] = Text("What it is"),
                    ["words"] = Texts("The words a line names it by"),
                    ["kind"] = new JsonObject { ["type"] = "string", ["title"] = "A preference changes no figure; a default is copied into what you make", ["enum"] = new JsonArray("preference", "default") },
                    ["takes"] = Text("What it takes, in words"),
                    ["typed"] = Flag("A line types its value; otherwise its page writes it, as JSON"),
                    ["choices"] = Texts("The values it takes, where it takes one of a few"),
                    ["value"] = Text("The value that applies, as a line types it; JSON for a value its page writes; none for no value"),
                    ["registered"] = Text("The value when you set none"),
                    ["set"] = Flag("You set it; otherwise the registered value applies"),
                    ["stored"] = Text("What you set, where it is no longer a value the setting takes"),
                    ["unreadable"] = Text("Why what you set is no longer taken, where it is not"),
                    ["at"] = Text("When you set it"),
                }, "key", "name", "title", "words", "kind", "takes", "typed", "value", "set"),
            },
        }, "settings")));
        builder.AddContract(new ContractDefinition(SetRequest, Version, "Set one of your settings", Record(new JsonObject
        {
            ["setting"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = Setting },
            ["value"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "The value, as a line types it (light, USD, 10mm); JSON for a value its page writes" },
        }, "setting", "value")));
        builder.AddContract(new ContractDefinition(ResetRequest, Version, "Reset one of your settings to its registered value", Record(new JsonObject
        {
            ["setting"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = Setting },
        }, "setting")));
        builder.AddContract(new ContractDefinition(Changed, Version, "A setting changed", Record(new JsonObject
        {
            ["key"] = Text("Its key"),
            ["name"] = Text("What a line names it by"),
            ["title"] = Text("What it is"),
            ["value"] = Text("The value that now applies, as a line types it"),
            ["set"] = Flag("You set it; otherwise the registered value applies"),
            ["replaced"] = Text("The value it replaced, as a line types it"),
            ["wasSet"] = Flag("You had set the value it replaced"),
            ["said"] = Text("What was done, in words, with how to take it back"),
        }, "key", "name", "title", "value", "set", "replaced", "wasSet", "said")));
    }

    /// <summary>A setting as the read answers it.</summary>
    public static JsonObject Described(UserSettings settings, SettingRead read)
    {
        var definition = read.Definition;
        var described = new JsonObject
        {
            ["key"] = read.Key,
            ["name"] = read.Name,
            ["title"] = definition.Title,
            ["words"] = new JsonArray([.. definition.Words.Select(w => (JsonNode)w)]),
            ["kind"] = JsonNamingPolicy.CamelCase.ConvertName(definition.Kind.ToString()),
            ["takes"] = UserSettings.Takes(definition),
            ["typed"] = definition.Spec is not null,
            ["value"] = settings.Text(read),
            ["set"] = read.Set,
        };
        if (definition.Spec?.Choices is { } choices)
            described["choices"] = new JsonArray([.. choices.Values.Distinct().Select(v => (JsonNode)FieldForms.Write(definition.Spec, JsonValue.Create(v)))]);
        if (definition.Registered is { } registered) described["registered"] = settings.Text(read with { Value = registered });
        if (read.Unreadable is { } why)
        {
            described["stored"] = UserSettings.Stored(read);
            described["unreadable"] = why;
        }
        if (read.At is { } at) described["at"] = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return described;
    }

    /// <summary>A set or a reset as its answer says it: what applies now, what it replaced, and how to take it back.</summary>
    public static JsonObject Answer(UserSettings settings, SettingRead replaced, SettingRead now, bool reset)
    {
        var value = settings.Text(now);
        var before = replaced.Unreadable is null ? settings.Text(replaced) : UserSettings.Stored(replaced);
        var back = replaced.Set ? $"settings {replaced.Name} {before}" : $"settings reset {now.Name}";
        var said = reset
            ? replaced.Set ? $"{now.Definition.Title} is {value} again, as registered; it was {before}. To set it back: {back}." : $"{now.Definition.Title} was already {value}, as registered."
            : FieldForms.Same(replaced.Set ? replaced.Stored : null, now.Stored) ? $"{now.Definition.Title} was already {value}."
            : $"{now.Definition.Title} is {value}; it was {before}{(replaced.Set ? "" : ", as registered")}. To set it back: {back}.";
        return new JsonObject
        {
            ["key"] = now.Key,
            ["name"] = now.Name,
            ["title"] = now.Definition.Title,
            ["value"] = value,
            ["set"] = now.Set,
            ["replaced"] = before,
            ["wasSet"] = replaced.Set,
            ["said"] = said,
        };
    }

    /// <summary>A command of the settings answers its request for its caller, or is refused as the settings refuse: by name.</summary>
    internal static Task<IReadOnlyDictionary<string, JsonNode?>> Answered(CapabilityInvocation invocation, Func<JsonNode, string, JsonObject> act)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        try
        {
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = act(request, caller.UserId) });
        }
        catch (SettingRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary><c>settings.read</c>: the caller's settings, or one of them, with what each is and whether they set it.</summary>
public sealed class SettingsRead(Func<UserSettings> settings) : ICapabilityHandler
{
    public const string Id = "settings.read";

    public CapabilityDescriptor Descriptor { get; } = new(Id, SettingsContracts.Version, "Read your settings: each one's value, what it takes, and whether you set it",
        [new("request", SettingsContracts.ReadRequest, SettingsContracts.Version, Cardinality.One)],
        [new("answer", SettingsContracts.ReadAnswer, SettingsContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.UserSetting]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        SettingsContracts.Answered(invocation, (request, user) =>
        {
            var service = settings();
            IReadOnlyList<SettingRead> read = CommandRequests.Optional(request, "setting") is { } one ? [service.One(user, one)] : service.Read(user);
            return new JsonObject { ["settings"] = new JsonArray([.. read.Select(r => (JsonNode)SettingsContracts.Described(service, r))]) };
        });
}

/// <summary><c>settings.set</c>: sets one of the caller's settings from its value as typed, and says what it replaced.</summary>
public sealed class SettingsSet(Func<UserSettings> settings) : ICapabilityHandler
{
    public const string Id = "settings.set";

    public CapabilityDescriptor Descriptor { get; } = new(Id, SettingsContracts.Version, "Set one of your settings, such as the theme; it changes no workspace and no Run",
        [new("request", SettingsContracts.SetRequest, SettingsContracts.Version, Cardinality.One)],
        [new("answer", SettingsContracts.Changed, SettingsContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.UserSetting]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        SettingsContracts.Answered(invocation, (request, user) =>
        {
            var service = settings();
            var (replaced, now) = service.Set(user, CommandRequests.Text(request, "setting"), CommandRequests.Text(request, "value"));
            return SettingsContracts.Answer(service, replaced, now, reset: false);
        });
}

/// <summary><c>settings.reset</c>: returns one of the caller's settings to its registered value, and says what it replaced.</summary>
public sealed class SettingsReset(Func<UserSettings> settings) : ICapabilityHandler
{
    public const string Id = "settings.reset";

    public CapabilityDescriptor Descriptor { get; } = new(Id, SettingsContracts.Version, "Reset one of your settings to its registered value",
        [new("request", SettingsContracts.ResetRequest, SettingsContracts.Version, Cardinality.One)],
        [new("answer", SettingsContracts.Changed, SettingsContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.UserSetting]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        SettingsContracts.Answered(invocation, (request, user) =>
        {
            var service = settings();
            var (replaced, now) = service.Reset(user, CommandRequests.Text(request, "setting"));
            return SettingsContracts.Answer(service, replaced, now, reset: true);
        });
}

/// <summary>The kernel's commands over a user's settings (ADR-0017 §4), with their contracts. Knows no setting.</summary>
public sealed class SettingsModule(Func<UserSettings> settings) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new SettingsRead(settings));
        builder.AddCapability(new SettingsSet(settings));
        builder.AddCapability(new SettingsReset(settings));
        SettingsContracts.Register(builder);
    }
}
