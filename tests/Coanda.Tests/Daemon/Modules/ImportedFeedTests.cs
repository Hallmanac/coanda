using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The module feed delivers no flow run ended or stopped event another node recorded, since what that flow run
/// left behind is on its own node, and delivers every other event the import appended as it delivers this node's own.
/// </summary>
public sealed class ImportedFeedTests
{
    private const string Project = "alpha";
    private const string Watcher = "watcher";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly CoreEventKind[] Kinds = [CoreEventKind.FlowRunEnded, CoreEventKind.FlowRunStopped, CoreEventKind.FlowRunPaused];

    [Fact]
    public async Task A_peers_flow_run_end_and_stop_are_not_delivered_and_its_pause_is()
    {
        using var temp = new TemporaryHome();
        // The module's feed runs once first, so its position is written before the history below is.
        await using (RunningDaemon first = await StartAsync(temp, new FeedModule(Kinds)))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            await Publishing.FlowAsync(first, Project, RunningSystemStep.FlowNamed());
            await first.Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, Project, [Watcher], None);
            await first.StopAsync(None);
        }

        Guid ended;
        Guid stopped;
        Guid paused;
        Guid own;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var parcels = new ParcelService(store, TimeProvider.System);
            var flowRuns = new FlowRunService(store, TimeProvider.System);
            string flow = RunningSystemStep.FlowNamed().Name;
            async Task<FlowRun> StartAsync(string title) =>
                await flowRuns.StartAsync(Principal.LocalOwner, Project, (await parcels.AddAsync(Principal.LocalOwner, Project, "task", title, null, None)).Id, flow, None);

            ended = (await flowRuns.EndAsync(Principal.LocalOwner, Project, (await StartAsync("Ended by a peer")).Id, "dispatched", None)).Id;
            stopped = (await flowRuns.StopAsync(Principal.LocalOwner, Project, (await StartAsync("Stopped by a peer")).Id, "Not needed.", None)).Id;
            paused = (await flowRuns.PauseAsync(Principal.LocalOwner, Project, (await StartAsync("Paused by a peer")).Id, "Look first.", "resume", None)).Id;
            await MarkImportedAsync(store, Ids.New(), ended, stopped, paused);

            // This node's own stop, recorded after the peer's events, so its delivery says the feed read past them.
            own = (await flowRuns.StopAsync(Principal.LocalOwner, Project, (await StartAsync("Stopped here")).Id, "Not needed.", None)).Id;
        }

        var watcher = new FeedModule(Kinds);
        await using RunningDaemon daemon = await StartAsync(temp, watcher);
        await FlowRunDriver.UntilAsync(
            () => watcher.Events.Any(e => e is FeedEvent.FlowRunStopped s && s.FlowRunId == own), "this node's own stop is delivered");

        Assert.DoesNotContain(watcher.Events, e => e is FeedEvent.FlowRunEnded end && end.FlowRunId == ended);
        Assert.DoesNotContain(watcher.Events, e => e is FeedEvent.FlowRunStopped stop && stop.FlowRunId == stopped);
        Assert.Contains(watcher.Events, e => e is FeedEvent.FlowRunPaused pause && pause.FlowRunId == paused);
        await daemon.StopAsync(None);
    }

    private static Task<RunningDaemon> StartAsync(TemporaryHome temp, FeedModule watcher) =>
        Daemons.StartAsync(temp.Home, new DaemonOptions { Modules = new Dictionary<string, IModule> { [Watcher] = watcher }, SystemSteps = [new RunningSystemStep()] }, None);

    /// <summary>Gives every event of <paramref name="streams"/> the record the import keeps for a line of <paramref name="origin"/>.</summary>
    private static async Task MarkImportedAsync(IDocumentStore store, Guid origin, params Guid[] streams)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        long sequence = 0;
        foreach (Guid stream in streams)
        {
            foreach (IEvent entry in await session.Events.FetchStreamAsync(stream, token: None))
            {
                session.Store(new LedgerLine { Id = entry.Id, Origin = origin, Sequence = ++sequence });
            }
        }

        await session.SaveChangesAsync(None);
    }
}
