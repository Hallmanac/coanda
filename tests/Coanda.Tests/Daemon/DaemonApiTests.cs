using System.Net;
using System.Net.Http.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Flows;
using Coanda.Tests.Daemon.Flows;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

public sealed class DaemonApiTests
{
    [Fact]
    public async Task The_cli_client_creates_lists_shows_and_closes_a_parcel_through_the_running_daemon()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        using var client = new DaemonClient(temp.Home);

        DaemonInfo? found = await client.FindRunningAsync(CancellationToken.None);
        found.Should().NotBeNull();
        found.Port.Should().Be(daemon.Info.Port);

        ParcelView added = await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest("coanda", "idea", "A lump of clay", null), CancellationToken.None);
        added.State.Should().Be("open");

        List<ParcelView> listed = await client.GetAsync<List<ParcelView>>("/parcels?project=coanda", CancellationToken.None);
        listed.Should().ContainSingle(p => p.Id == added.Id);

        ParcelView shown = await client.GetAsync<ParcelView>($"/parcels/{added.Id}?project=coanda", CancellationToken.None);
        shown.Title.Should().Be("A lump of clay");

        await FluentActions.Awaiting(() => client.PostAsync<ParcelView>(
                "/parcels/move", new ParcelMoveRequest("coanda", added.Id, "build"), CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("*does not allow a move from 'open' to 'build'*");

        ParcelView closed = await client.PostAsync<ParcelView>(
            "/parcels/close", new ParcelCloseRequest("coanda", added.Id, "archived", "Nothing came of it."), CancellationToken.None);
        closed.Resolution.Should().Be("archived");

        await client.StopAsync(CancellationToken.None);
        await daemon.WaitForShutdownAsync(CancellationToken.None);
        File.Exists(temp.Home.DaemonInfoPath).Should().BeFalse("a clean stop removes the daemon's address");
    }

    [Fact]
    public async Task A_call_without_the_token_is_refused()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        using var http = new HttpClient();

        using HttpResponseMessage response = await http.GetAsync($"{daemon.Info.BaseAddress}/parcels?project=coanda", CancellationToken.None);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_move_or_close_of_a_parcel_a_live_flow_holds_is_refused_with_422_whatever_principal_the_request_carries()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowService flows = daemon.Services.GetRequiredService<FlowService>();

        // A flow the service starts is not advanced, and refine binds no contour, so the flow stays live and alone.
        ParcelView parcel = await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest("coanda", "task", "Held by a flow", null), CancellationToken.None);
        Flow flow = await flows.StartAsync(Principal.LocalOwner, "coanda", parcel.Id, "build", CancellationToken.None);
        ParcelView held = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project=coanda", CancellationToken.None);
        held.LiveFlowId.Should().Be(flow.Id);
        string refusal = $"*held by flow {Ids.Short(flow.Id)}*(flow.stop)*";

        using var http = new HttpClient { BaseAddress = new Uri(daemon.Info.BaseAddress) };
        await RefusedAsync(http, daemon, "/parcels/move", new ParcelMoveRequest("coanda", parcel.Id, "build"), "brian", refusal + "before moving the parcel.");
        await RefusedAsync(
            http, daemon, "/parcels/close", new ParcelCloseRequest("coanda", parcel.Id, "completed", "Done by hand."), "brian", refusal + "before closing the parcel.");
        await RefusedAsync(
            http, daemon, "/parcels/move", new ParcelMoveRequest("coanda", parcel.Id, "build"), FlowRunner.PrincipalFor(flow.Id).Id, refusal + "before moving the parcel.");

        ParcelView after = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project=coanda", CancellationToken.None);
        after.Should().BeEquivalentTo(held, "every refusal leaves the parcel as it was");

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_person_resumes_or_stops_the_flow_that_holds_a_parcel_through_the_api_and_a_stop_frees_the_parcel()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowService flows = daemon.Services.GetRequiredService<FlowService>();

        ParcelView parcel = await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest("coanda", "task", "Held, then let go", null), CancellationToken.None);
        Flow flow = await flows.StartAsync(Principal.LocalOwner, "coanda", parcel.Id, "build", CancellationToken.None);
        await flows.PauseAsync(Principal.LocalOwner, "coanda", flow.Id, "Waiting on a person.", "resume", CancellationToken.None);

        FlowView resumed = await client.PostAsync<FlowView>(
            $"/flows/{flow.Id}/resume", new FlowResumeRequest("coanda", "Carry on."), CancellationToken.None);
        resumed.Status.Should().Be("running");

        // The stock executor holds no script, so the resumed flow pauses again at its first judgment;
        // waiting for that keeps the stop below from racing the runner's own writes.
        await Flowing.UntilAsync(client, "coanda", flow.Id, "paused");

        await FluentActions.Awaiting(() => client.PostAsync<FlowView>(
                $"/flows/{flow.Id}/stop", new FlowStopRequest("coanda", " "), CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("Stopping a flow records why; give a reason.");

        FlowView stopped = await client.PostAsync<FlowView>(
            $"/flows/{flow.Id}/stop", new FlowStopRequest("coanda", "Taking this by hand."), CancellationToken.None);
        stopped.Status.Should().Be("stopped");
        stopped.StopReason.Should().Be("Taking this by hand.");

        ParcelView freed = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project=coanda", CancellationToken.None);
        freed.LiveFlowId.Should().BeNull("a stopped flow releases its parcel");
        ParcelView closed = await client.PostAsync<ParcelView>(
            "/parcels/close", new ParcelCloseRequest("coanda", parcel.Id, "abandoned", "Done by hand."), CancellationToken.None);
        closed.Resolution.Should().Be("abandoned");

        await FluentActions.Awaiting(() => client.PostAsync<FlowView>(
                $"/flows/{flow.Id}/resume", new FlowResumeRequest("coanda", null), CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("*only a paused flow resumes.");

        await daemon.StopAsync(CancellationToken.None);
    }

    private static async Task RefusedAsync(HttpClient http, RunningDaemon daemon, string path, object body, string principal, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        request.Headers.Add(DaemonHost.PrincipalHeader, principal);

        using HttpResponseMessage response = await http.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorView? error = await response.Content.ReadFromJsonAsync<ErrorView>(CancellationToken.None);
        error.Should().NotBeNull();
        error.Error.Should().Match(message);
    }
}
