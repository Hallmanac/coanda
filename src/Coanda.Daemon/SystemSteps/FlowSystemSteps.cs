using Coanda.Core.Flows;
using Coanda.Sdk.Commands;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.GitHub;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// The registered system steps, one implementation per name, and the one place a system step is
/// dispatched, asked a dispatch's status, or told to stop one: it finds the step's system step and
/// turns a missing or ill-kinded argument into a pause.
/// </summary>
public sealed class FlowSystemSteps
{
    private readonly Dictionary<string, IFlowSystemStep> systemSteps = new(StringComparer.Ordinal);

    public FlowSystemSteps(IEnumerable<IFlowSystemStep> registered)
    {
        foreach (IFlowSystemStep systemStep in registered)
        {
            if (!systemSteps.TryAdd(systemStep.Name, systemStep))
            {
                throw new InvalidOperationException($"The system step '{systemStep.Name}' is registered twice; each name has one implementation.");
            }
        }
    }

    /// <summary>The names of the registered system steps.</summary>
    public IReadOnlySet<string> Names => systemSteps.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>These system steps and <paramref name="more"/> beside them; a name registered twice is refused.</summary>
    public FlowSystemSteps With(IEnumerable<IFlowSystemStep> more) => new([.. systemSteps.Values, .. more]);

    /// <summary>
    /// The system steps the daemon ships: every name in the core catalog and the software catalog. The
    /// worktree, gates, open-pr, and rebase system steps run their commands through <paramref name="runner"/>, the
    /// gates system step ending each of its commands after <paramref name="gatesCommandLimit"/>; open-pr and
    /// gated-merge read their token through it too and reach GitHub through the client
    /// <paramref name="github"/> gives them for each dispatch. Gated-merge and rebase read the head
    /// the flow run's chain recorded as pushed, and the worktree system step the head last pushed for the
    /// parcel and the status of a flow run whose worktree holds its branch, through <paramref name="flowRuns"/>,
    /// a service of their own, since the daemon's own flow run service hands resumes to the runner that
    /// dispatches these system steps; for the same reason the worktree system step waits on another flow run's worker
    /// through <paramref name="workers"/>, which the runner publishes to. Derive-parcels is still a placeholder.
    /// </summary>
    public static FlowSystemSteps Shipped(
        ParcelService parcels,
        ProjectService projects,
        ProjectClones clones,
        ICommandRunner runner,
        NodeHome home,
        TimeProvider clock,
        TimeSpan gatesCommandLimit,
        Func<GitHubClient> github,
        FlowRunService flowRuns,
        FlowRunWorkers workers) => new(
    [
        new MoveParcelSystemStep(parcels),
        new TimerSystemStep(clock),
        new WaitForHumanSystemStep(),
        new WaitForExternalEventSystemStep(),
        new EmitOutcomeSystemStep(),
        new PlaceholderSystemStep(CoreSystemSteps.DeriveParcels),
        new WorktreeSystemStep(projects, clones, flowRuns, workers),
        new GatesSystemStep(projects, runner, home, gatesCommandLimit),
        new OpenPrSystemStep(parcels, projects, clones, new GitHubToken(runner, home), github),
        new GatedMergeSystemStep(parcels, projects, flowRuns, new GitHubToken(runner, home), github),
        new RebaseSystemStep(projects, clones, flowRuns),
    ]);

    public async ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        string name = request.SystemStep;
        if (!systemSteps.TryGetValue(name, out IFlowSystemStep? systemStep))
        {
            return new SystemStepAnswer.Paused(Unimplemented(request.Step.Name, name), FlowRunLevers.PerformAgain(request.Step.Name));
        }

        try
        {
            return await systemStep.DispatchAsync(request, cancellationToken);
        }
        catch (SystemStepArgumentException refused)
        {
            return new SystemStepAnswer.Paused(refused.Message, FlowRunLevers.PerformAgain(refused.Step));
        }
    }

    /// <summary>
    /// Asks the system step <paramref name="name"/> of the flow step <paramref name="step"/> where its
    /// dispatch <paramref name="dispatchId"/> stands; a name this daemon has no implementation of
    /// pauses, as its dispatch would.
    /// </summary>
    public async ValueTask<SystemStepStatus> StatusAsync(string step, string name, Guid dispatchId, CancellationToken cancellationToken) =>
        systemSteps.TryGetValue(name, out IFlowSystemStep? systemStep)
            ? await systemStep.StatusAsync(dispatchId, cancellationToken)
            : new SystemStepStatus.Paused(Unimplemented(step, name), FlowRunLevers.PerformAgain(step));

    /// <summary>
    /// Tells the system step <paramref name="name"/> to stop its dispatch <paramref name="dispatchId"/>;
    /// a name this daemon has no implementation of has nothing running here to stop.
    /// </summary>
    public async ValueTask StopAsync(string name, Guid dispatchId, CancellationToken cancellationToken)
    {
        if (systemSteps.TryGetValue(name, out IFlowSystemStep? systemStep))
        {
            await systemStep.StopAsync(dispatchId, cancellationToken);
        }
    }

    private static string Unimplemented(string step, string name) =>
        $"Step '{step}' names the system step '{name}', which this daemon has no implementation of.";
}
