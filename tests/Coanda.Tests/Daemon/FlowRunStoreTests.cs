using System.Text.Json;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Modules.Software;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

public sealed class FlowRunStoreTests
{
    private const string Project = "alpha";
    private static readonly Principal Owner = Principal.LocalOwner;
    private static readonly ModelSpec Model = new("anthropic", "claude-opus-5-5", "high");
    private static readonly Measurement Spend = new(1200, 0.42m, 3, TimeSpan.FromSeconds(63));

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Starts a build flow run and runs it through its worktree system step into the build agent step, answered.</summary>
    private static async Task<FlowRun> RunToAnsweredBuildAsync(FlowRunService flowRuns, Guid parcelId)
    {
        CancellationToken none = CancellationToken.None;
        FlowRun flowRun = await flowRuns.StartAsync(Owner, Project, parcelId, "build", none);
        await flowRuns.RecordResultAsync(Owner, Project, flowRun.Id, "worktree", SoftwareSteps.Worktree, Json("""{"verdict":"done","path":"/tmp/wt","reused":false}"""), none);
        await flowRuns.StartStepAsync(Owner, Project, flowRun.Id, "build", none);
        Guid session = Ids.New();
        await flowRuns.SpawnedAsync(Owner, Project, flowRun.Id, "build", "agenthash1", Model, none);
        await flowRuns.SessionOpenedAsync(Owner, Project, flowRun.Id, "build", session, none);
        return await flowRuns.RecordAnswerAsync(
            Owner, Project, flowRun.Id, "build", "build", session, "agenthash1", Model,
            Json("""{"verdict":"done","files":3,"notes":{"nested":true}}"""), Spend, none);
    }

    [Fact]
    public async Task A_flow_run_written_through_the_service_reads_back_through_the_daemon_after_the_store_is_reopened()
    {
        using var temp = new TemporaryHome();
        Guid parcelId;
        Guid flowRunId;

        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
            Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Owner, Project, "task", "Run once", null, CancellationToken.None);
            parcelId = parcel.Id;
            var service = new FlowRunService(store, TimeProvider.System);
            FlowRun flowRun = await RunToAnsweredBuildAsync(service, parcelId);
            flowRunId = flowRun.Id;

            // Paused, so the daemon's start leaves it where it was written rather than advancing it.
            await service.PauseAsync(Owner, Project, flowRunId, "Held for the read-back.", "Resume it.", CancellationToken.None);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);

        FlowRunView shown = await client.GetAsync<FlowRunView>($"/flow-runs/{flowRunId}?project={Project}", CancellationToken.None);
        Assert.Equal(parcelId, shown.ParcelId);
        Assert.Equal("build", shown.FlowName);
        Assert.Equal(SoftwareFlows.Build.Hash, shown.FlowHash);
        Assert.Equal("paused", shown.Status);
        Assert.Equal("build", shown.CurrentStep);
        Assert.Equal(["worktree", "build"], shown.Steps.Select(s => s.Step));

        FlowRunStepView build = shown.Steps[1];
        Assert.Equal("agent-step", build.Kind);
        Assert.Equal("build", build.Performer);
        Assert.Equal("agenthash1", build.AgentHash);
        Assert.Equal(Model, build.Model);
        Assert.Equal(Spend, build.Measurement);
        Assert.True(build.Answer.HasValue);
        Assert.Equal("done", build.Answer.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Equal("/tmp/wt", shown.Steps[0].Result.GetValueOrDefault().GetProperty("path").GetString());

        List<FlowRunView> listed = await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Project}&parcel={parcelId}", CancellationToken.None);
        Assert.Single(listed, f => f.Id == flowRunId);

        ParcelView parcelView = await client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", CancellationToken.None);
        Assert.Equal(flowRunId, parcelView.LiveFlowRunId);

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_second_start_is_refused_while_the_first_flow_run_is_paused_and_accepted_once_it_is_stopped()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, Project, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        CancellationToken none = CancellationToken.None;

        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "One at a time", null), none);
        Assert.Null(parcel.LiveFlowRunId);

        FlowRun first = await flowRuns.StartAsync(Owner, Project, parcel.Id, "build", none);
        await flowRuns.PauseAsync(Owner, Project, first.Id, "Needs a decision.", "Answer the question in the journal.", none);

        ParcelView whilePaused = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        // a paused flow run is still live
        Assert.Equal(first.Id, whilePaused.LiveFlowRunId);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => flowRuns.StartAsync(Owner, Project, parcel.Id, "build", none));
        Assert.StartsWith("A parcel has at most one live flow run;", exception.Message, StringComparison.Ordinal);

        await flowRuns.StopAsync(Owner, Project, first.Id, "Nobody will answer it.", none);
        ParcelView afterStop = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        Assert.Null(afterStop.LiveFlowRunId);

        FlowRun second = await flowRuns.StartAsync(Owner, Project, parcel.Id, "build", none);
        ParcelView other = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Someone else's", null), none);
        await flowRuns.StartAsync(Owner, Project, other.Id, "build", none);
        ParcelView withSecond = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        Assert.Equal(second.Id, withSecond.LiveFlowRunId);

        List<FlowRunView> listed = await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Project}&parcel={parcel.Id}", none);
        Assert.Equal([(first.Id, "stopped"), (second.Id, "running")], listed.Select(f => (f.Id, f.Status)));

        await daemon.StopAsync(none);
    }

    [Fact]
    public async Task An_ended_flow_run_releases_its_parcel()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        var parcels = new ParcelService(store, TimeProvider.System);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;

        Parcel parcel = await parcels.AddAsync(Owner, Project, "task", "End it", null, none);
        FlowRun flowRun = await flowRuns.StartAsync(Owner, Project, parcel.Id, "build", none);
        FlowRun ended = await flowRuns.EndAsync(Owner, Project, flowRun.Id, SoftwareFlows.Build.Outcome, none);

        Assert.Equal(FlowRunStatus.Ended, ended.Status);
        Parcel? after = await parcels.ShowAsync(Owner, Project, parcel.Id, none);
        Assert.NotNull(after);
        Assert.Null(after.LiveFlowRunId);
    }

    [Fact]
    public async Task A_start_that_read_the_parcel_before_another_start_committed_fails_the_parcel_streams_version_check()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;
        Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Owner, Project, "task", "Race", null, none);

        // The racing start reads the parcel with no live flow run, then the service's start commits first.
        await using IDocumentSession racing = store.LightweightSession(Project);
        var stale = await racing.Events.FetchForWriting<Parcel>(parcel.Id, none);
        Assert.NotNull(stale.Aggregate);
        Assert.Null(stale.Aggregate.LiveFlowRunId);

        FlowRun winner = await flowRuns.StartAsync(Owner, Project, parcel.Id, "build", none);

        stale.AppendOne(new ParcelFlowRunAttached(parcel.Id, Ids.New(), DateTimeOffset.UtcNow));
        await Assert.ThrowsAnyAsync<JasperFx.ConcurrencyException>(() => racing.SaveChangesAsync(none));

        Parcel? after = await new ParcelService(store, TimeProvider.System).ShowAsync(Owner, Project, parcel.Id, none);
        Assert.NotNull(after);
        Assert.Equal(winner.Id, after.LiveFlowRunId);
    }

    [Fact]
    public async Task Resuming_and_stopping_a_flow_run_are_open_to_people_starting_it_and_recording_its_steps_to_every_role()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        var parcels = new ParcelService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;
        Parcel parcel = await parcels.AddAsync(Owner, Project, "task", "Roles", null, none);
        Parcel bySomeone = await parcels.AddAsync(Owner, Project, "task", "Started by a person", null, none);
        var agent = new Principal("build-session", Role.Agent);
        var member = new Principal("teammate", Role.Member);

        // A flow binding's start acts in the Agent role, so starting is open to it as well as to people.
        await flowRuns.StartAsync(member, Project, bySomeone.Id, "build", none);
        FlowRun flowRun = await flowRuns.StartAsync(agent, Project, parcel.Id, "build", none);
        await flowRuns.RecordResultAsync(agent, Project, flowRun.Id, "worktree", SoftwareSteps.Worktree, Json("""{"verdict":"done"}"""), none);
        await flowRuns.StartStepAsync(agent, Project, flowRun.Id, "build", none);
        await flowRuns.PauseAsync(agent, Project, flowRun.Id, "Asked a question.", "Answer it.", none);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => flowRuns.ResumeAsync(agent, Project, flowRun.Id, null, none));
        Assert.Equal("'flow-run.resume' is not open to the Agent role.", exception.Message);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => flowRuns.StopAsync(agent, Project, flowRun.Id, "Done with it.", none));
        Assert.Equal("'flow-run.stop' is not open to the Agent role.", exception2.Message);

        IReadOnlyList<FlowRun> listed = await flowRuns.ListAsync(agent, Project, parcel.Id, none);
        Assert.Single(listed);
        FlowRun? shown = await flowRuns.ShowAsync(agent, Project, flowRun.Id, none);
        Assert.NotNull(shown);
    }

    [Fact]
    public async Task Flow_run_show_prints_the_flow_the_parcel_the_status_each_step_and_the_pause()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, Project, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        CancellationToken none = CancellationToken.None;

        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Show me", null), none);
        FlowRun flowRun = await RunToAnsweredBuildAsync(flowRuns, parcel.Id);
        await flowRuns.PauseAsync(Owner, Project, flowRun.Id, "The build agent asked a question.", "Answer it in the journal.", none);

        FlowRunView view = await FlowRunLookup.FindAsync(client, Project, Ids.Short(flowRun.Id), none);
        IReadOnlyList<string> lines = FlowRunRendering.Show(view);

        Assert.Contains($"Flow    build v1 ({SoftwareFlows.Build.Hash})", lines);
        Assert.Contains($"Parcel  {parcel.Id}", lines);
        Assert.Contains("Status  paused", lines);
        Assert.Contains("Reason  The build agent asked a question.", lines);
        Assert.Contains("Lever   Answer it in the journal.", lines);
        int steps = lines.ToList().IndexOf("Steps");
        Assert.Equal(["  worktree  system-step software/worktree  verdict=done  path=/tmp/wt  reused=false", "  build  agent-step build  verdict=done  files=3  tokens 1200, cost $0.42, 63s"], lines.Skip(steps + 1));

        List<FlowRunView> listed = await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Project}&parcel={parcel.Id}", none);
        FlowRunView row = Assert.Single(listed);
        Assert.Equal([Ids.Short(flowRun.Id), "build v1", "paused", "build"], FlowRunRendering.ListRow(row).Take(4));

        await daemon.StopAsync(none);
    }
}
