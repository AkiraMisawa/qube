using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;

namespace Qube.Composition.Tests;

/// <summary>What a scenario command left: the workspace, the scenarios it made and those of the same name it replaced.</summary>
sealed record ScenarioAnswer(WorkspaceState State, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced);

/// <summary>
/// The pricer's service as its tests drive it. The scenario commands are each planned on the workspace at the revision
/// named and written as one command, which is what the commands of the line do through
/// <see cref="PortfolioWorkspaces.WriteScenarios"/> (ADR-0016 note 32).
/// </summary>
static class PricerSteps
{
    /// <summary>Price as the pricer's own page asks for it: the Run's origin is the pricer.</summary>
    public static SubmissionResult Price(this PortfolioWorkspaces service, string workspaceId, long revision, string user, string idempotencyKey) =>
        service.Price(workspaceId, revision, user, idempotencyKey, RunOrigin.Pricer);

    public static ScenarioAnswer DefineScenario(this PortfolioWorkspaces service, string workspaceId, long revision, string user, string name, string shifts) =>
        Made(service, workspaceId, revision, user, raw => service.PlanScenario(raw, revision, name, shifts));

    public static ScenarioAnswer GenerateScenarios(this PortfolioWorkspaces service, string workspaceId, long revision, string user, string generator, string text) =>
        Made(service, workspaceId, revision, user, raw => service.PlanGenerate(raw, revision, generator, text));

    public static WorkspaceState IncludeScenario(this PortfolioWorkspaces service, string workspaceId, long revision, string user, string name, bool included) =>
        Made(service, workspaceId, revision, user, raw => service.PlanInclude(raw, name, included)).State;

    public static WorkspaceState RemoveScenario(this PortfolioWorkspaces service, string workspaceId, long revision, string user, string name) =>
        Made(service, workspaceId, revision, user, raw => service.PlanRemoveScenario(raw, name)).State;

    public static WorkspaceState ClearScenarios(this PortfolioWorkspaces service, string workspaceId, long revision, string user) =>
        Made(service, workspaceId, revision, user, service.PlanClearScenarios).State;

    public static ScenarioAnswer DuplicateScenario(this PortfolioWorkspaces service, string workspaceId, long revision, string user, string name, string copy) =>
        Made(service, workspaceId, revision, user, raw => service.PlanDuplicate(raw, revision, name, copy));

    public static ScenarioAnswer LoadScenarios(this PortfolioWorkspaces service, string workspaceId, long revision, string user, string set) =>
        Made(service, workspaceId, revision, user, raw => service.PlanLoad(raw, revision, user, set));

    static ScenarioAnswer Made(PortfolioWorkspaces service, string workspaceId, long revision, string user, Func<WorkspaceState, ScenarioPlan> planned)
    {
        var (state, plan) = service.WriteScenarios(workspaceId, revision, user, planned);
        return new(state, plan.Made, plan.Replaced);
    }
}
