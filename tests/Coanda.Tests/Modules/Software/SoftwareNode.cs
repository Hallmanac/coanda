using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// A running daemon hosting the software module, for a test that dispatches one of the module's steps
/// directly: the module runs its commands on <see cref="Runner"/> and reaches GitHub through the fake
/// <see cref="GitHub"/>, and reads and writes through the daemon's API as the software module. The
/// projects it registers have the module switched on, and each holds <see cref="Steps"/>, a flow with
/// one step per software step, so a flow run can sit in a step and hold its dispatch.
/// </summary>
internal sealed class SoftwareNode : IAsyncDisposable
{
    public const string Project = "alpha";

    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp;

    private SoftwareNode(TemporaryHome temp, RunningDaemon daemon, ScriptedCommandRunner runner, FakeGitHub github)
    {
        this.temp = temp;
        Daemon = daemon;
        Runner = runner;
        GitHub = github;
    }

    /// <summary>A flow whose steps are the module's five, each named as the build and merge flows name it, every one ending on any verdict it answers.</summary>
    public static Flow Steps { get; } = new()
    {
        Name = "steps",
        Accepts = ["task"],
        Outcome = "stepped",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            Step("worktree", SoftwareSteps.Worktree, "done"),
            Step("gates", SoftwareSteps.Gates, "done", "failed"),
            Step("open-pr", SoftwareSteps.OpenPr, "opened"),
            Step("gated-merge", SoftwareSteps.GatedMerge, "merged", "pending", "behind"),
            Step("rebase", SoftwareSteps.Rebase, "done"),
        ],
    };

    public RunningDaemon Daemon { get; }

    public ScriptedCommandRunner Runner { get; }

    public FakeGitHub GitHub { get; }

    public NodeHome Home => temp.Home;

    public IServiceProvider Services => Daemon.Services;

    public IDocumentStore Store => Services.GetRequiredService<IDocumentStore>();

    public ParcelService Parcels => Services.GetRequiredService<ParcelService>();

    public ProjectService Projects => Services.GetRequiredService<ProjectService>();

    /// <summary>A flow run service of the test's own on the daemon's store, which hands nothing to the daemon's advancer.</summary>
    public FlowRunService FlowRuns => new(Store, TimeProvider.System);

    /// <summary>The daemon's workers, as the worktree step waits on them through the interim seam.</summary>
    public FlowRunWorkers Workers => Services.GetRequiredService<FlowRunWorkers>();

    /// <summary>The daemon's side of the interim seam, which removes a flow run's worktree through the module.</summary>
    public InterimWorktreeLink Worktrees => Services.GetRequiredService<InterimWorktreeLink>();

    /// <summary>
    /// Starts a node whose module runs commands on <paramref name="runner"/> and reaches GitHub through
    /// <paramref name="github"/>, or through <paramref name="handler"/> when one is given, with the
    /// projects <paramref name="projects"/> (<see cref="Project"/> when none) registered.
    /// </summary>
    public static async Task<SoftwareNode> StartAsync(
        ScriptedCommandRunner? runner = null,
        FakeGitHub? github = null,
        Func<HttpMessageHandler>? handler = null,
        TimeSpan? gatesCommandLimit = null,
        params string[] projects)
    {
        var temp = new TemporaryHome();
        runner ??= new ScriptedCommandRunner();
        github ??= new FakeGitHub();
        RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home,
            new DaemonOptions
            {
                CommandRunner = runner,
                GitHubHandler = handler ?? github.Handler,
                GatesCommandLimit = gatesCommandLimit ?? DaemonOptions.DefaultGatesCommandLimit,
            },
            None);
        foreach (string project in projects.Length == 0 ? [Project] : projects)
        {
            await RegisteredProjects.AddAsync(daemon, project, None);
            await Publishing.FlowAsync(daemon, project, Steps);
        }

        return new SoftwareNode(temp, daemon, runner, github);
    }

    public async Task<Parcel> TaskAsync(string title, string project = Project) =>
        await Parcels.AddAsync(Principal.LocalOwner, project, "task", title, null, None);

    /// <summary>
    /// Starts a flow run of <see cref="Steps"/> on the parcel, moves it into <paramref name="step"/>, and
    /// records a dispatch of that step, which the flow run then holds; nothing advances the flow run.
    /// </summary>
    public async Task<(Guid FlowRunId, Guid DispatchId)> HeldAsync(Guid parcelId, string step, string project = Project)
    {
        FlowRunService flowRuns = FlowRuns;
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, project, parcelId, Steps.Name, None);
        if (started.CurrentStep != step)
        {
            await flowRuns.StartStepAsync(Principal.LocalOwner, project, started.Id, step, None);
        }

        FlowRun dispatched = await flowRuns.DispatchedAsync(Principal.LocalOwner, project, started.Id, step, None);
        return (started.Id, dispatched.Current?.DispatchId ?? throw new InvalidOperationException("The flow run holds no dispatch."));
    }

    /// <summary>Stops the flow run, as a person would, so another can start on its parcel.</summary>
    public Task StopAsync(Guid flowRunId, string project = Project) =>
        FlowRuns.StopAsync(Principal.LocalOwner, project, flowRunId, "the test is done with it", None);

    /// <summary>
    /// Dispatches the module's step <paramref name="step"/> for the flow run <paramref name="flowRunId"/>
    /// on the parcel <paramref name="parcelId"/>, under <paramref name="dispatchId"/> or a new id, and
    /// gives its answer as the daemon records it.
    /// </summary>
    public async Task<SystemStepAnswer> DispatchAsync(
        string step, Guid parcelId, Guid flowRunId, Guid? dispatchId = null, string project = Project, CancellationToken? cancellationToken = null)
    {
        var request = new DispatchRequest(
            project, parcelId, flowRunId, step, Visit: 1, dispatchId ?? Ids.New(), Resumed: false,
            Home.FlowRunWorkPath(project, flowRunId), new Dictionary<string, JsonElement>());
        DispatchAnswer answer = await Services.GetRequiredService<SoftwareModule>().DispatchAsync(request, cancellationToken ?? None);
        return answer switch
        {
            DispatchAnswer.Done done => new SystemStepAnswer.Done(JsonSerializer.SerializeToElement(done.Result, ModuleJson.Options)),
            DispatchAnswer.Paused paused => new SystemStepAnswer.Paused(paused.Reason, paused.Lever),
            DispatchAnswer.Running => new SystemStepAnswer.Running(),
            _ => throw new InvalidOperationException($"The module answered {answer.GetType().Name}."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Daemon.StopAsync(None);
        await Daemon.DisposeAsync();
        temp.Dispose();
    }

    private static FlowStep Step(string name, string systemStep, params string[] verdicts) => new()
    {
        Name = name,
        SystemStep = systemStep,
        Transitions = [.. verdicts.Select(v => new FlowTransition { Field = "verdict", Value = FlowValue.Of(v), VisitCap = 1, Target = Flow.End })],
    };
}
