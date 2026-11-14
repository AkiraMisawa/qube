using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

/// <summary>
/// A workspace's project name (ADR-0010 §1's note of 2026-11-14): the name its Runs are listed under in the Pricing DB,
/// which tells apart the Runs made on the dummy counterparty, where the counterparty says nothing of what was priced.
/// A workspace on the dummy needs one before it is made and before it prices.
/// </summary>
public static class ProjectNames
{
    public const int MostCharacters = 80;

    public const string Rule = "A project name is 1 to 80 characters on one line, with no space at either end, or empty for none.";

    /// <summary>What a move onto the dummy says where the workspace has no project, before Enter.</summary>
    public const string NeededOnTheDummy = "On it the workspace prices only under a project: name one with `project <name>`.";

    const string Why = "A workspace on the dummy counterparty is listed in the Pricing DB by its project, since the counterparty says nothing of what was priced";

    /// <summary>Whether a name is one: not empty, on one line, no longer than <see cref="MostCharacters"/>, and as typed with nothing trimmed off.</summary>
    public static bool Holds(string name) =>
        name.Length is > 0 and <= MostCharacters && name == name.Trim() && !name.Any(char.IsControl);

    /// <summary>The refusal of a workspace on the dummy made with no project, of the field it names.</summary>
    public static WorkspaceRefused MissingAtMaking(EditTarget field) => new("ProjectRequired", $"{Why}: name the project as the workspace is made.", [field]);

    /// <summary>
    /// The refusal of a Price on the dummy with no project. It names no field: a line lists a refusal's fields after its
    /// sentence, and this one says where the project is named.
    /// </summary>
    public static WorkspaceRefused MissingAtPrice() =>
        new("ProjectRequired", $"{Why}: name the project beside the counterparty, or with `project <name>`, to price.");
}
