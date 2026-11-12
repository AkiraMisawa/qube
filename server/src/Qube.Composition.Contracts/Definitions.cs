using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Qube.Composition.Contracts;

// The wire vocabulary of contracts/composition.schema.json. Property names follow the schema through
// WireJson's camelCase policy; nothing here knows any financial domain.

/// <summary>An immutable revision of a retained dataset or artifact. Not a path and not a grant.</summary>
public sealed record TypedDataRef(string DataId, string ContractId, string ContractVersion, string Revision);

public enum Cardinality { One, Many }

public enum ExecutionKind { RequestResponse, Job, Subscription }

/// <summary>
/// What invoking a capability does. <c>EditWorkspace</c>, <c>Subscribe</c>, <c>ReadRun</c>, <c>SaveItem</c>,
/// <c>Execute</c> and <c>ReadWorkspace</c> make it a command (ADR-0005): invoked for a caller outside Runs, never as a
/// workflow step. <c>ReadRun</c> reads a retained Run's results for a caller who may see it, such as a term of its trace
/// (ADR-0011 §7); <c>SaveItem</c> keeps a saved item for its caller, such as a scenario set (C32, ADR-0010 §4);
/// <c>Execute</c> starts work for its caller that the command does not wait for, such as a Run submitted or a failed
/// result asked for again (ADR-0016 note 33): it writes no workspace. <c>ReadWorkspace</c> reads its caller's own
/// workspace, such as rows of one of its tables (ADR-0016 §7, note 38): it writes nothing and starts nothing.
/// <c>ReadSettings</c> reads its caller's own settings, and <c>EditSettings</c> writes them (ADR-0017 §2): neither
/// touches a workspace or a Run, so both are commands too. <c>Propose</c> answers a proposal its caller applies, or not,
/// such as an arrangement of the panels (ADR-0018 §8): it writes nothing and starts nothing, and is a command so that the
/// agent is offered it.
/// </summary>
public enum CapabilityEffect { ReadData, Compute, PersistAnalysis, EditWorkspace, Subscribe, ReadRun, SaveItem, Execute, ReadWorkspace, ReadSettings, EditSettings, Propose }

public sealed record Port(string Name, string ContractId, string ContractVersion, Cardinality Cardinality);

/// <summary>
/// What an argument of a typed command takes (ADR-0016 §4): rows of a registered table, as the one selector spells
/// them; values for fields, as <c>name=value</c> or one field and its value; one field by its words; or text as it is.
/// </summary>
public enum CommandArgumentKind { Rows, Assignments, Field, Text }

/// <summary>One argument of a command as a line types it, in the order it is typed, under the name the request holds it by.</summary>
public sealed record CommandArgument(string Name, CommandArgumentKind Kind, bool Required);

/// <summary>
/// How a command is typed (ADR-0016 §4): the words that name it, each a spelling of its own, and its arguments. The
/// command line builds its entry from this, and the server reads the line (§5); the client reads nothing itself.
/// </summary>
/// <param name="App">The application whose workspaces the command acts on (note 27 of step 3): a line of another's does not name it, and its page does not list it. None for a command of every application's workspaces.</param>
public sealed record CommandForm(IReadOnlyList<string> Words, IReadOnlyList<CommandArgument> Arguments, string? App = null);

/// <param name="Command">How it is typed on the command line; none for a capability no line names.</param>
public sealed record CapabilityDescriptor(
    string CapabilityId,
    string Version,
    string Title,
    IReadOnlyList<Port> InputPorts,
    IReadOnlyList<Port> OutputPorts,
    ExecutionKind ExecutionKind,
    IReadOnlyList<CapabilityEffect> Effects,
    CommandForm? Command = null);

/// <summary>
/// A verb of the screen as the catalog lists it (ADR-0016 §3, note 21 of step 2): its id, what it does, and how it is
/// typed. It is listed apart from the capabilities: nothing invokes it, and the agent's tools hold none.
/// </summary>
public sealed record ViewVerbDescriptor(string VerbId, string Title, CommandForm Command);

/// <summary>
/// A short form as the catalog lists it (ADR-0016 §3, note 29 of step 3): words an application's users type for a line
/// of a kernel verb, with its id, what it does, and how it is typed. It is listed apart from the capabilities: what
/// runs is the kernel command, and the agent's tools hold none.
/// </summary>
public sealed record ShortFormDescriptor(string FormId, string Title, CommandForm Command);

public sealed record CapabilityCatalog(string DocumentType, string SchemaVersion, IReadOnlyList<CapabilityDescriptor> Capabilities);

/// <summary>Where a step input, a map source or a workflow output takes its value from. No expressions.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(InputBinding), "input")]
[JsonDerivedType(typeof(ContextBinding), "context")]
[JsonDerivedType(typeof(ConstantBinding), "constant")]
[JsonDerivedType(typeof(OutputBinding), "output")]
[JsonDerivedType(typeof(ItemBinding), "item")]
[JsonDerivedType(typeof(DataReferenceBinding), "dataReference")]
public abstract record Binding;

public sealed record InputBinding(string Path) : Binding;
public sealed record ContextBinding(string Path) : Binding;
public sealed record ConstantBinding(JsonNode? Value) : Binding;
public sealed record OutputBinding(string StepId, string Port, string? Path = null) : Binding;
public sealed record ItemBinding(string Path) : Binding;
public sealed record DataReferenceBinding(TypedDataRef Reference) : Binding;

public enum FailurePolicy { Stop, Collect }

public enum ItemMode { Value, OutcomeValue }

public sealed record WorkflowStep(
    string StepId,
    string CapabilityId,
    string CapabilityVersion,
    IReadOnlyList<string> DependsOn,
    IReadOnlyDictionary<string, Binding> Bindings,
    FailurePolicy FailurePolicy,
    Binding? ForEach = null,
    int? MaxConcurrency = null,
    ItemMode? ItemMode = null,
    string? ItemKeyPath = null);

public sealed record WorkflowDefinition(
    string DocumentType,
    string SchemaVersion,
    string DefinitionId,
    string Version,
    string Title,
    string InputContractId,
    string InputContractVersion,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyDictionary<string, Binding> Outputs);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(WorkflowOutputData), "workflowOutput")]
[JsonDerivedType(typeof(DataReferenceData), "dataReference")]
public abstract record ViewData;

public sealed record WorkflowOutputData(string Name) : ViewData;
public sealed record DataReferenceData(TypedDataRef Reference) : ViewData;

public enum ColumnFormat { Text, Number, Currency, Percent, Date, Status }

public sealed record ViewColumn(string Field, string Label, ColumnFormat Format, string? CurrencyField = null);

public sealed record RowBinding(string Kind, string Field);

public sealed record ViewAction(string ActionId, string Label, IReadOnlyDictionary<string, RowBinding> Bindings);

public enum ComponentKind { Form, Choice, Grid, Chart, Metric, Progress, Details }

public sealed record ViewComponent(
    string Id,
    ComponentKind Kind,
    string Title,
    ViewData Data,
    IReadOnlyList<ViewColumn>? Columns = null,
    IReadOnlyList<ViewAction>? Actions = null);

public sealed record ViewLayout(int WideColumns, int NarrowColumns);

public sealed record ViewDefinition(
    string DocumentType,
    string SchemaVersion,
    string DefinitionId,
    string Version,
    string Title,
    ViewLayout Layout,
    IReadOnlyList<ViewComponent> Components);

public enum DatasetColumnType { String, Number, Integer, Boolean, Date, DataRef }

public sealed record DatasetColumn(string Field, DatasetColumnType Type, bool Nullable);

public enum ExecutionMode { Actual, Simulated }

public enum DataOrigin { Source, Synthetic, Mixed }

public sealed record DatasetProvenance(ExecutionMode ExecutionMode, DataOrigin DataOrigin, string ProducerInvocationId);

public sealed record Dataset(
    string DocumentType,
    string SchemaVersion,
    TypedDataRef Reference,
    string Title,
    IReadOnlyList<DatasetColumn> Columns,
    IReadOnlyList<JsonObject> Rows,
    DatasetProvenance Provenance);

public enum OutcomeStatus { Succeeded, Failed, Blocked }

public sealed record OutcomeError(string Code, string Message, string? CausedByItemKey = null);

/// <summary>One item of a mapped step: a value or an error, never both, keyed by a stable item key.</summary>
public sealed record Outcome(
    string ItemKey,
    IReadOnlyDictionary<string, string> Identity,
    OutcomeStatus Status,
    JsonNode? Value = null,
    OutcomeError? Error = null);

public sealed record OutcomeCollection(
    string DocumentType,
    string SchemaVersion,
    string ElementContractId,
    string ElementContractVersion,
    IReadOnlyList<Outcome> Items);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(OpenViewTarget), "openView")]
[JsonDerivedType(typeof(RunWorkflowTarget), "runWorkflow")]
public abstract record ActionTarget;

/// <summary>Shows a registered view over retained data: nothing executes.</summary>
public sealed record OpenViewTarget(string ViewId, string ViewVersion, string DataParameter) : ActionTarget;

/// <summary>Submits a registered workflow, its input built from the action's parameters.</summary>
public sealed record RunWorkflowTarget(string WorkflowId, string WorkflowVersion, IReadOnlyDictionary<string, string> InputFromParameters) : ActionTarget;

public enum ActionParameterKind { DataRef, Text }

public sealed record ActionParameter(string Name, ActionParameterKind Kind);

/// <summary>A registered action a view may offer on a row. Its parameters are bound from row fields.</summary>
public sealed record ActionDefinition(string ActionId, string Title, IReadOnlyList<ActionParameter> Parameters, ActionTarget Target);
