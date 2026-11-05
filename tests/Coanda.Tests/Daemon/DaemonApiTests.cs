using System.Net;
using System.Net.Http.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Tests.Daemon.FlowRuns;
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
        Assert.NotNull(found);
        Assert.Equal(daemon.Info.Port, found.Port);

        ParcelView added = await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest("coanda", "idea", "A lump of clay", null), CancellationToken.None);
        Assert.Equal("open", added.Stage);

        List<ParcelView> listed = await client.GetAsync<List<ParcelView>>("/parcels?project=coanda", CancellationToken.None);
        Assert.Single(listed, p => p.Id == added.Id);

        ParcelView shown = await client.GetAsync<ParcelView>($"/parcels/{added.Id}?project=coanda", CancellationToken.None);
        Assert.Equal("A lump of clay", shown.Title);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>(
                "/parcels/move", new ParcelMoveRequest("coanda", added.Id, "build"), CancellationToken.None));
        Assert.Contains("does not allow a move from 'open' to 'build'", exception.Message, StringComparison.Ordinal);

        ParcelView closed = await client.PostAsync<ParcelView>(
            "/parcels/close", new ParcelCloseRequest("coanda", added.Id, "archived", "Nothing came of it."), CancellationToken.None);
        Assert.Equal("archived", closed.Resolution);

        await client.StopAsync(CancellationToken.None);
        await daemon.WaitForShutdownAsync(CancellationToken.None);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath), "a clean stop removes the daemon's address");
    }

    [Fact]
    public async Task A_call_without_the_token_is_refused()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        using var http = new HttpClient();

        using HttpResponseMessage response = await http.GetAsync($"{daemon.Info.BaseAddress}/parcels?project=coanda", CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_move_or_close_of_a_parcel_a_live_flow_run_holds_is_refused_with_422_whatever_principal_the_request_carries()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();

        // A flow run the service starts is not advanced, and refine binds no flow, so the flow run stays live and alone.
        ParcelView parcel = await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest("coanda", "task", "Held by a flow run", null), CancellationToken.None);
        FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, "coanda", parcel.Id, "build", CancellationToken.None);
        ParcelView held = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project=coanda", CancellationToken.None);
        Assert.Equal(flowRun.Id, held.LiveFlowRunId);
        string refusal = $"*held by flow run {Ids.Short(flowRun.Id)}*(flow-run.stop)*";

        using var http = new HttpClient { BaseAddress = new Uri(daemon.Info.BaseAddress) };
        await RefusedAsync(http, daemon, "/parcels/move", new ParcelMoveRequest("coanda", parcel.Id, "build"), Environment.UserName, refusal + "before moving the parcel.");
        await RefusedAsync(
            http, daemon, "/parcels/close", new ParcelCloseRequest("coanda", parcel.Id, "completed", "Done by hand."), Environment.UserName, refusal + "before closing the parcel.");
        await RefusedAsync(
            http, daemon, "/parcels/move", new ParcelMoveRequest("coanda", parcel.Id, "build"), null, refusal + "before moving the parcel.");

        ParcelView after = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project=coanda", CancellationToken.None);
        // every refusal leaves the parcel as it was
        Assert.Equivalent(held, after, strict: true);

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_person_resumes_or_stops_the_flow_run_that_holds_a_parcel_through_the_api_and_a_stop_frees_the_parcel()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        using var client = new DaemonClient(temp.Home);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();

        ParcelView parcel = await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest("coanda", "task", "Held, then let go", null), CancellationToken.None);
        FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, "coanda", parcel.Id, "build", CancellationToken.None);
        await flowRuns.PauseAsync(Principal.LocalOwner, "coanda", flowRun.Id, "Waiting on a person.", "resume", CancellationToken.None);

        FlowRunView resumed = await client.PostAsync<FlowRunView>(
            $"/flow-runs/{flowRun.Id}/resume", new FlowRunResumeRequest("coanda", "Carry on."), CancellationToken.None);
        Assert.Equal("running", resumed.Status);

        // The stock executor holds no script, so the resumed flow run pauses again at its first agent step;
        // waiting for that keeps the stop below from racing the runner's own writes.
        await FlowRunDriver.UntilAsync(client, "coanda", flowRun.Id, "paused");

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<FlowRunView>(
                $"/flow-runs/{flowRun.Id}/stop", new FlowRunStopRequest("coanda", " "), CancellationToken.None));
        Assert.Equal("Stopping a flow run records why; give a reason.", exception.Message);

        FlowRunView stopped = await client.PostAsync<FlowRunView>(
            $"/flow-runs/{flowRun.Id}/stop", new FlowRunStopRequest("coanda", "Taking this by hand."), CancellationToken.None);
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal("Taking this by hand.", stopped.StopReason);

        ParcelView freed = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project=coanda", CancellationToken.None);
        // a stopped flow run releases its parcel
        Assert.Null(freed.LiveFlowRunId);
        ParcelView closed = await client.PostAsync<ParcelView>(
            "/parcels/close", new ParcelCloseRequest("coanda", parcel.Id, "abandoned", "Done by hand."), CancellationToken.None);
        Assert.Equal("abandoned", closed.Resolution);

        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<FlowRunView>(
                $"/flow-runs/{flowRun.Id}/resume", new FlowRunResumeRequest("coanda", null), CancellationToken.None));
        Assert.EndsWith("only a paused flow run resumes.", exception2.Message, StringComparison.Ordinal);

        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_call_holding_only_the_daemon_token_whose_principal_header_names_anyone_but_the_owner_is_refused_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(daemon.Info.BaseAddress) };
        string stranger = $"not-{Environment.UserName}";
        string refusal = $"The {DaemonHost.PrincipalHeader} header names '{stranger}', but a call holding only the daemon token acts as the node's owner, '{Environment.UserName}'; send '{Environment.UserName}' or no header.";

        // A command and a query are both refused, and so is an id differing from the owner's only in case.
        await RefusedAsync(http, daemon, "/parcels", new ParcelAddRequest("coanda", "idea", "Not the owner's", null), stranger, refusal);
        (HttpStatusCode queried, ErrorView? queryError) = await SendAsync<ErrorView>(http, daemon, HttpMethod.Get, "/parcels?project=coanda", null, stranger);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, queried);
        Assert.Equal(refusal, queryError?.Error);
        string shouted = Environment.UserName.ToUpperInvariant();
        if (!string.Equals(shouted, Environment.UserName, StringComparison.Ordinal))
        {
            (HttpStatusCode cased, _) = await SendAsync<ErrorView>(
                http, daemon, HttpMethod.Post, "/parcels", new ParcelAddRequest("coanda", "idea", "Not quite the owner's", null), shouted);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, cased);
        }

        using var client = new DaemonClient(temp.Home);
        Assert.Empty(await client.GetAsync<List<ParcelView>>("/parcels?project=coanda", CancellationToken.None));

        await daemon.StopAsync(CancellationToken.None);
    }

    public static TheoryData<string?> OwnersHeaders => [null, "", " ", Environment.UserName];

    [Theory]
    [MemberData(nameof(OwnersHeaders))]
    public async Task A_call_holding_only_the_daemon_token_records_as_the_owner_whose_id_is_the_account_the_daemon_runs_as(string? header)
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(daemon.Info.BaseAddress) };

        (HttpStatusCode status, ParcelView? added) = await SendAsync<ParcelView>(
            http, daemon, HttpMethod.Post, "/parcels", new ParcelAddRequest("coanda", "idea", "The owner's", null), header);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(Environment.UserName, added?.CreatedBy);

        await daemon.StopAsync(CancellationToken.None);
    }

    private static async Task RefusedAsync(HttpClient http, RunningDaemon daemon, string path, object body, string? principal, string message)
    {
        (HttpStatusCode status, ErrorView? error) = await SendAsync<ErrorView>(http, daemon, HttpMethod.Post, path, body, principal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(error);
        Assert.Matches(WildcardPattern.ToRegex(message), error.Error);
    }

    /// <summary>Sends a call holding the daemon token, carrying <paramref name="principal"/> in the principal header when it is given.</summary>
    private static async Task<(HttpStatusCode Status, T? Body)> SendAsync<T>(
        HttpClient http, RunningDaemon daemon, HttpMethod method, string path, object? body, string? principal)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        if (principal is not null)
        {
            request.Headers.Add(DaemonHost.PrincipalHeader, principal);
        }

        using HttpResponseMessage response = await http.SendAsync(request, CancellationToken.None);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<T>(CancellationToken.None));
    }
}
