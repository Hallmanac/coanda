using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
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

    /// <summary>The daemon's workers, which the module feed holds a flow run's ended or stopped delivery on.</summary>
    public FlowRunWorkers Workers => Services.GetRequiredService<FlowRunWorkers>();

    /// <summary>
    /// Delivers to the module, as the module feed would, that the flow run <paramref name="flowRunId"/>
    /// ended, for a flow run the store need not hold, so the module removes its worktree.
    /// </summary>
    public Task DeliverEndAsync(Guid flowRunId, string project = Project) =>
        Services.GetRequiredService<SoftwareModule>().FeedAsync(new FeedDelivery([new FeedEvent.FlowRunEnded(project, 1, flowRunId)]), None);

    /// <summary>
    /// Starts a node whose module runs commands on <paramref name="runner"/> and reaches GitHub through
    /// <paramref name="github"/>, or through <paramref name="handler"/> when one is given, with the
    /// projects <paramref name="projects"/> (<see cref="Project"/> when none) registered, its log also
    /// going to <paramref name="log"/> when one is given. Registering a project makes the core's clone of
    /// its repo through <paramref name="runner"/>; those commands are forgotten once the node is started,
    /// so a test reads only what its own steps run, against the clone the core made.
    /// </summary>
    public static async Task<SoftwareNode> StartAsync(
        ScriptedCommandRunner? runner = null,
        FakeGitHub? github = null,
        Func<HttpMessageHandler>? handler = null,
        TimeSpan? gatesCommandLimit = null,
        RecordingLogger? log = null,
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
                LoggerProvider = log,
            },
            None);
        foreach (string project in projects.Length == 0 ? [Project] : projects)
        {
            await RegisteredProjects.AddAsync(daemon, project, None);
            await Publishing.FlowAsync(daemon, project, Steps);
        }

        runner.ForgetCommands();
        return new SoftwareNode(temp, daemon, runner, github);
    }

    /// <summary>Gives the software module's setting <paramref name="name"/> the value <paramref name="value"/> in <paramref name="project"/>, as the owner would.</summary>
    public Task<ModuleSettingsShown> SetAsync(string project, string name, object value) =>
        Services.GetRequiredService<ModuleSettingsService>().SetAsync(
            Principal.LocalOwner, project, SoftwareSteps.Module, name, JsonSerializer.SerializeToElement(value), None);

    public async Task<Parcel> TaskAsync(string title, string project = Project) =>
        await Parcels.AddAsync(Principal.LocalOwner, project, "task", title, null, None);

    /// <summary>
    /// Starts a flow run of <see cref="Steps"/> on the parcel, moves it into <paramref name="step"/>, and
    /// records a dispatch of that step, which the flow run then holds; nothing advances the flow run. A
    /// parcel id the project holds no parcel by is given a task first, as a dispatch does.
    /// </summary>
    public async Task<(Guid FlowRunId, Guid DispatchId)> HeldAsync(Guid parcelId, string step, string project = Project)
    {
        await EnsureTaskAsync(project, parcelId);
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
        await EnsureTaskAsync(project, parcelId);
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

    /// <summary>
    /// Writes a task with the id <paramref name="parcelId"/> straight to the store when the project holds
    /// no parcel by that id, as a test that names a parcel by an id of its own expects one to exist: the
    /// module's steps read the task to find the repo it works in. A project the store holds no record of
    /// is left alone, so the step meets the core's own refusal.
    /// </summary>
    private async Task EnsureTaskAsync(string project, Guid parcelId)
    {
        if (await ProjectService.FindAsync(Store, project, None) is null)
        {
            return;
        }

        await using IDocumentSession session = Store.LightweightSession(project);
        if (await session.LoadAsync<Parcel>(parcelId, None) is not null)
        {
            return;
        }

        ParcelCreated created = ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, project, "Named by the test", null, Principal.LocalOwner.Id, DateTimeOffset.UtcNow)
            with { ParcelId = parcelId };
        session.Events.StartStream<Parcel>(parcelId, created);
        await session.SaveChangesAsync(None);
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
