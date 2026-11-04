using System.Text.Json;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

public sealed class FlowStoreTests
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

    /// <summary>Starts a build flow and runs it through its worktree action into the build judgment, answered.</summary>
    private static async Task<Flow> RunToAnsweredBuildAsync(FlowService flows, Guid parcelId)
    {
        CancellationToken none = CancellationToken.None;
        Flow flow = await flows.StartAsync(Owner, Project, parcelId, "build", none);
        await flows.RecordResultAsync(Owner, Project, flow.Id, "worktree", SoftwareActions.Worktree, Json("""{"verdict":"done","path":"/tmp/wt","reused":false}"""), none);
        await flows.StartStepAsync(Owner, Project, flow.Id, "build", none);
        Guid session = Ids.New();
        await flows.SpawnedAsync(Owner, Project, flow.Id, "build", "agenthash1", Model, none);
        await flows.SessionOpenedAsync(Owner, Project, flow.Id, "build", session, none);
        return await flows.RecordAnswerAsync(
            Owner, Project, flow.Id, "build", "build", session, "agenthash1", Model,
            Json("""{"verdict":"done","files":3,"notes":{"nested":true}}"""), Spend, none);
    }

    [Fact]
    public async Task A_flow_written_through_the_service_reads_back_through_the_daemon_after_the_store_is_reopened()
    {
        using var temp = new TemporaryHome();
        Guid parcelId;
        Guid flowId;

        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
            Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Owner, Project, "task", "Run once", null, CancellationToken.None);
            parcelId = parcel.Id;
            var service = new FlowService(store, TimeProvider.System);
            Flow flow = await RunToAnsweredBuildAsync(service, parcelId);
            flowId = flow.Id;

            // Paused, so the daemon's start leaves it where it was written rather than advancing it.
            await service.PauseAsync(Owner, Project, flowId, "Held for the read-back.", "Resume it.", CancellationToken.None);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);

        FlowView shown = await client.GetAsync<FlowView>($"/flows/{flowId}?project={Project}", CancellationToken.None);
        shown.ParcelId.Should().Be(parcelId);
        shown.ContourName.Should().Be("build");
        shown.ContourHash.Should().Be(ShippedContours.Build.Hash);
        shown.Status.Should().Be("paused");
        shown.CurrentStep.Should().Be("build");
        shown.Steps.Select(s => s.Step).Should().Equal("worktree", "build");

        FlowStepView build = shown.Steps[1];
        build.Kind.Should().Be("judgment");
        build.Performer.Should().Be("build");
        build.AgentHash.Should().Be("agenthash1");
        build.Model.Should().Be(Model);
        build.Measurement.Should().Be(Spend);
        build.Answer.HasValue.Should().BeTrue();
        build.Answer.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");
        shown.Steps[0].Result.GetValueOrDefault().GetProperty("path").GetString().Should().Be("/tmp/wt");

        List<FlowView> listed = await client.GetAsync<List<FlowView>>($"/flows?project={Project}&parcel={parcelId}", CancellationToken.None);
        listed.Should().ContainSingle(f => f.Id == flowId);

        ParcelView parcelView = await client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", CancellationToken.None);
        parcelView.LiveFlowId.Should().Be(flowId);

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_second_start_is_refused_while_the_first_flow_is_paused_and_accepted_once_it_is_stopped()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, Project, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowService flows = daemon.Services.GetRequiredService<FlowService>();
        CancellationToken none = CancellationToken.None;

        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "One at a time", null), none);
        parcel.LiveFlowId.Should().BeNull();

        Flow first = await flows.StartAsync(Owner, Project, parcel.Id, "build", none);
        await flows.PauseAsync(Owner, Project, first.Id, "Needs a decision.", "Answer the question in the journal.", none);

        ParcelView whilePaused = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        whilePaused.LiveFlowId.Should().Be(first.Id, "a paused flow is still live");

        await FluentActions.Awaiting(() => flows.StartAsync(Owner, Project, parcel.Id, "build", none))
            .Should().ThrowAsync<DomainException>().WithMessage("A parcel has at most one live flow;*");

        await flows.StopAsync(Owner, Project, first.Id, "Nobody will answer it.", none);
        ParcelView afterStop = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        afterStop.LiveFlowId.Should().BeNull();

        Flow second = await flows.StartAsync(Owner, Project, parcel.Id, "build", none);
        ParcelView other = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Someone else's", null), none);
        await flows.StartAsync(Owner, Project, other.Id, "build", none);
        ParcelView withSecond = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        withSecond.LiveFlowId.Should().Be(second.Id);

        List<FlowView> listed = await client.GetAsync<List<FlowView>>($"/flows?project={Project}&parcel={parcel.Id}", none);
        listed.Select(f => (f.Id, f.Status)).Should().Equal((first.Id, "stopped"), (second.Id, "running"));

        await daemon.StopAsync(none);
    }

    [Fact]
    public async Task An_ended_flow_releases_its_parcel()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        var parcels = new ParcelService(store, TimeProvider.System);
        var flows = new FlowService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;

        Parcel parcel = await parcels.AddAsync(Owner, Project, "task", "End it", null, none);
        Flow flow = await flows.StartAsync(Owner, Project, parcel.Id, "build", none);
        Flow ended = await flows.EndAsync(Owner, Project, flow.Id, ShippedContours.Build.Outcome, none);

        ended.Status.Should().Be(FlowStatus.Ended);
        Parcel? after = await parcels.ShowAsync(Owner, Project, parcel.Id, none);
        after.Should().NotBeNull();
        after.LiveFlowId.Should().BeNull();
    }

    [Fact]
    public async Task A_start_that_read_the_parcel_before_another_start_committed_fails_the_parcel_streams_version_check()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        var flows = new FlowService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;
        Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Owner, Project, "task", "Race", null, none);

        // The racing start reads the parcel with no live flow, then the service's start commits first.
        await using IDocumentSession racing = store.LightweightSession(Project);
        var stale = await racing.Events.FetchForWriting<Parcel>(parcel.Id, none);
        stale.Aggregate.Should().NotBeNull();
        stale.Aggregate.LiveFlowId.Should().BeNull();

        Flow winner = await flows.StartAsync(Owner, Project, parcel.Id, "build", none);

        stale.AppendOne(new ParcelFlowAttached(parcel.Id, Ids.New(), DateTimeOffset.UtcNow));
        await FluentActions.Awaiting(() => racing.SaveChangesAsync(none)).Should().ThrowAsync<JasperFx.ConcurrencyException>();

        Parcel? after = await new ParcelService(store, TimeProvider.System).ShowAsync(Owner, Project, parcel.Id, none);
        after.Should().NotBeNull();
        after.LiveFlowId.Should().Be(winner.Id);
    }

    [Fact]
    public async Task Resuming_and_stopping_a_flow_are_open_to_people_starting_it_and_recording_its_steps_to_every_role()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, CancellationToken.None);
        var flows = new FlowService(store, TimeProvider.System);
        var parcels = new ParcelService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;
        Parcel parcel = await parcels.AddAsync(Owner, Project, "task", "Roles", null, none);
        Parcel bySomeone = await parcels.AddAsync(Owner, Project, "task", "Started by a person", null, none);
        var agent = new Principal("build-session", Role.Agent);
        var member = new Principal("teammate", Role.Member);

        // A contour binding's start acts in the Agent role, so starting is open to it as well as to people.
        await flows.StartAsync(member, Project, bySomeone.Id, "build", none);
        Flow flow = await flows.StartAsync(agent, Project, parcel.Id, "build", none);
        await flows.RecordResultAsync(agent, Project, flow.Id, "worktree", SoftwareActions.Worktree, Json("""{"verdict":"done"}"""), none);
        await flows.StartStepAsync(agent, Project, flow.Id, "build", none);
        await flows.PauseAsync(agent, Project, flow.Id, "Asked a question.", "Answer it.", none);
        await FluentActions.Awaiting(() => flows.ResumeAsync(agent, Project, flow.Id, null, none))
            .Should().ThrowAsync<DomainException>().WithMessage("'flow.resume' is not open to the Agent role.");
        await FluentActions.Awaiting(() => flows.StopAsync(agent, Project, flow.Id, "Done with it.", none))
            .Should().ThrowAsync<DomainException>().WithMessage("'flow.stop' is not open to the Agent role.");

        IReadOnlyList<Flow> listed = await flows.ListAsync(agent, Project, parcel.Id, none);
        listed.Should().ContainSingle();
        Flow? shown = await flows.ShowAsync(agent, Project, flow.Id, none);
        shown.Should().NotBeNull();
    }

    [Fact]
    public async Task Flow_show_prints_the_contour_the_parcel_the_status_each_step_and_the_pause()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, Project, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowService flows = daemon.Services.GetRequiredService<FlowService>();
        CancellationToken none = CancellationToken.None;

        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Show me", null), none);
        Flow flow = await RunToAnsweredBuildAsync(flows, parcel.Id);
        await flows.PauseAsync(Owner, Project, flow.Id, "The build agent asked a question.", "Answer it in the journal.", none);

        FlowView view = await FlowLookup.FindAsync(client, Project, Ids.Short(flow.Id), none);
        IReadOnlyList<string> lines = FlowRendering.Show(view);

        lines.Should().Contain($"Contour build v1 ({ShippedContours.Build.Hash})");
        lines.Should().Contain($"Parcel  {parcel.Id}");
        lines.Should().Contain("Status  paused");
        lines.Should().Contain("Reason  The build agent asked a question.");
        lines.Should().Contain("Lever   Answer it in the journal.");
        int steps = lines.ToList().IndexOf("Steps");
        lines.Skip(steps + 1).Should().Equal(
            "  worktree  action worktree  verdict=done  path=/tmp/wt  reused=false",
            "  build  judgment build  verdict=done  files=3  tokens 1200, cost $0.42, 63s");

        List<FlowView> listed = await client.GetAsync<List<FlowView>>($"/flows?project={Project}&parcel={parcel.Id}", none);
        FlowView row = listed.Should().ContainSingle().Subject;
        FlowRendering.ListRow(row).Take(4).Should().Equal(Ids.Short(flow.Id), "build v1", "paused", "build");

        await daemon.StopAsync(none);
    }
}
