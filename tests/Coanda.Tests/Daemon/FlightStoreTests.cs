using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flights;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Flights;
using Coanda.Daemon.Parcels;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

public sealed class FlightStoreTests
{
    private const string Project = "alpha";
    private static readonly Principal Owner = Principal.LocalOwner;
    private static readonly ModelTriple Model = new("anthropic", "claude-opus-5-5", "high");
    private static readonly Measurement Spend = new(1200, 0.42m, 3, TimeSpan.FromSeconds(63));

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Starts a build flight and runs it through its worktree action into the build judgment, answered.</summary>
    private static async Task<Flight> RunToAnsweredBuildAsync(FlightService flights, Guid parcelId)
    {
        CancellationToken none = CancellationToken.None;
        Flight flight = await flights.StartAsync(Owner, Project, parcelId, "build", none);
        await flights.RecordResultAsync(Owner, Project, flight.Id, "worktree", SoftwareActions.Worktree, Json("""{"verdict":"done","path":"/tmp/wt","reused":false}"""), none);
        await flights.StartStepAsync(Owner, Project, flight.Id, "build", none);
        return await flights.RecordAnswerAsync(
            Owner, Project, flight.Id, "build", "build", "agenthash1", Model,
            Json("""{"verdict":"done","files":3,"notes":{"nested":true}}"""), Spend, none);
    }

    [Fact]
    public async Task A_flight_written_through_the_service_reads_back_through_the_daemon_after_the_store_is_reopened()
    {
        using var temp = new TemporaryHome();
        Guid parcelId;
        Guid flightId;

        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinition.All))
        {
            Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Owner, Project, "task", "Fly once", null, CancellationToken.None);
            parcelId = parcel.Id;
            Flight flight = await RunToAnsweredBuildAsync(new FlightService(store, TimeProvider.System), parcelId);
            flightId = flight.Id;
        }

        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);

        FlightView shown = await client.GetAsync<FlightView>($"/flights/{flightId}?project={Project}", CancellationToken.None);
        shown.ParcelId.Should().Be(parcelId);
        shown.Flow.Should().Be("build");
        shown.FlowHash.Should().Be(ShippedFlows.Build.Hash);
        shown.Status.Should().Be("running");
        shown.CurrentStep.Should().Be("build");
        shown.Steps.Select(s => s.Step).Should().Equal("worktree", "build");

        FlightStepView build = shown.Steps[1];
        build.Kind.Should().Be("judgment");
        build.Performer.Should().Be("build");
        build.AgentHash.Should().Be("agenthash1");
        build.Model.Should().Be(Model);
        build.Measurement.Should().Be(Spend);
        build.Answer.HasValue.Should().BeTrue();
        build.Answer.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");
        shown.Steps[0].Result.GetValueOrDefault().GetProperty("path").GetString().Should().Be("/tmp/wt");

        List<FlightView> listed = await client.GetAsync<List<FlightView>>($"/flights?project={Project}&parcel={parcelId}", CancellationToken.None);
        listed.Should().ContainSingle(f => f.Id == flightId);

        ParcelView parcelView = await client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", CancellationToken.None);
        parcelView.LiveFlightId.Should().Be(flightId);

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_second_start_is_refused_while_the_first_flight_is_parked_and_accepted_once_it_is_stopped()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlightService flights = daemon.Services.GetRequiredService<FlightService>();
        CancellationToken none = CancellationToken.None;

        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "One at a time", null), none);
        parcel.LiveFlightId.Should().BeNull();

        Flight first = await flights.StartAsync(Owner, Project, parcel.Id, "build", none);
        await flights.ParkAsync(Owner, Project, first.Id, "Needs a decision.", "Answer the question in the journal.", none);

        ParcelView whileParked = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        whileParked.LiveFlightId.Should().Be(first.Id, "a parked flight is still live");

        await FluentActions.Awaiting(() => flights.StartAsync(Owner, Project, parcel.Id, "build", none))
            .Should().ThrowAsync<DomainException>().WithMessage("A parcel has at most one live flight;*");

        await flights.StopAsync(Owner, Project, first.Id, "Nobody will answer it.", none);
        ParcelView afterStop = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        afterStop.LiveFlightId.Should().BeNull();

        Flight second = await flights.StartAsync(Owner, Project, parcel.Id, "build", none);
        ParcelView other = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Someone else's", null), none);
        await flights.StartAsync(Owner, Project, other.Id, "build", none);
        ParcelView withSecond = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", none);
        withSecond.LiveFlightId.Should().Be(second.Id);

        List<FlightView> listed = await client.GetAsync<List<FlightView>>($"/flights?project={Project}&parcel={parcel.Id}", none);
        listed.Select(f => (f.Id, f.Status)).Should().Equal((first.Id, "stopped"), (second.Id, "running"));

        await daemon.StopAsync(none);
    }

    [Fact]
    public async Task An_ended_flight_releases_its_parcel()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinition.All);
        var parcels = new ParcelService(store, TimeProvider.System);
        var flights = new FlightService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;

        Parcel parcel = await parcels.AddAsync(Owner, Project, "task", "End it", null, none);
        Flight flight = await flights.StartAsync(Owner, Project, parcel.Id, "build", none);
        Flight ended = await flights.EndAsync(Owner, Project, flight.Id, ShippedFlows.Build.Outcome, none);

        ended.Status.Should().Be(FlightStatus.Ended);
        Parcel? after = await parcels.ShowAsync(Owner, Project, parcel.Id, none);
        after.Should().NotBeNull();
        after.LiveFlightId.Should().BeNull();
    }

    [Fact]
    public async Task A_start_that_read_the_parcel_before_another_start_committed_fails_the_parcel_streams_version_check()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinition.All);
        var flights = new FlightService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;
        Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Owner, Project, "task", "Race", null, none);

        // The racing start reads the parcel with no live flight, then the service's start commits first.
        await using IDocumentSession racing = store.LightweightSession(Project);
        var stale = await racing.Events.FetchForWriting<Parcel>(parcel.Id, none);
        stale.Aggregate.Should().NotBeNull();
        stale.Aggregate.LiveFlightId.Should().BeNull();

        Flight winner = await flights.StartAsync(Owner, Project, parcel.Id, "build", none);

        stale.AppendOne(new ParcelFlightAttached(parcel.Id, Ids.New(), DateTimeOffset.UtcNow));
        await FluentActions.Awaiting(() => racing.SaveChangesAsync(none)).Should().ThrowAsync<JasperFx.ConcurrencyException>();

        Parcel? after = await new ParcelService(store, TimeProvider.System).ShowAsync(Owner, Project, parcel.Id, none);
        after.Should().NotBeNull();
        after.LiveFlightId.Should().Be(winner.Id);
    }

    [Fact]
    public async Task Writing_a_flight_is_open_to_people_and_reading_one_to_every_role()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinition.All);
        var flights = new FlightService(store, TimeProvider.System);
        CancellationToken none = CancellationToken.None;
        Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Owner, Project, "task", "Roles", null, none);
        var agent = new Principal("build-session", Role.Agent);
        var member = new Principal("teammate", Role.Member);

        await FluentActions.Awaiting(() => flights.StartAsync(agent, Project, parcel.Id, "build", none))
            .Should().ThrowAsync<DomainException>().WithMessage("'flight.start' is not open to the Agent role.");

        Flight flight = await flights.StartAsync(member, Project, parcel.Id, "build", none);
        await FluentActions.Awaiting(() => flights.RecordResultAsync(agent, Project, flight.Id, "worktree", SoftwareActions.Worktree, Json("{}"), none))
            .Should().ThrowAsync<DomainException>().WithMessage("'flight.result' is not open to the Agent role.");

        IReadOnlyList<Flight> listed = await flights.ListAsync(agent, Project, parcel.Id, none);
        listed.Should().ContainSingle();
        Flight? shown = await flights.ShowAsync(agent, Project, flight.Id, none);
        shown.Should().NotBeNull();
    }
}
