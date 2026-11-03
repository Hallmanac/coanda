using Coanda.Cli;
using Coanda.Core;
using Coanda.Daemon.Api;

namespace Coanda.Tests.Daemon;

public sealed class DaemonApiTests
{
    [Fact]
    public async Task The_cli_client_creates_lists_shows_and_closes_a_parcel_through_the_running_daemon()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, CancellationToken.None);
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
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, CancellationToken.None);
        using var http = new HttpClient();

        using HttpResponseMessage response = await http.GetAsync($"{daemon.Info.BaseAddress}/parcels?project=coanda", CancellationToken.None);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        await daemon.StopAsync(CancellationToken.None);
    }
}
