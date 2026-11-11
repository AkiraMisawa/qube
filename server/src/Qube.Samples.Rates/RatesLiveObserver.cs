using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Rates;

/// <summary>
/// Keeps a workspace's live subscription on the revision its last accepted command left (ADR-0016 note 35), whoever
/// wrote it: a cell of the page, a kernel verb, <c>undo</c>, or the AI. Nothing is built where nothing ticks.
/// </summary>
public sealed class RatesLiveObserver(RatesLiveService live, ConventionRegistry conventions, IRatesPricingProvider provider) : IWorkspaceObserver
{
    public string App => RatesEditRules.App;

    public void Accepted(WorkspaceState state)
    {
        if (live.OnWorkspace(state.WorkspaceId).Count == 0) return;
        live.WorkspaceChanged(state.WorkspaceId, RatesAcceptedInput.Of(state, conventions, provider));
    }
}
