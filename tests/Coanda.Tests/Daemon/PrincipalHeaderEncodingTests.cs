using System.Net;
using System.Net.Http.Json;
using System.Text;
using Coanda.Cli;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The principal header carries the id as its UTF-8 bytes: an owner id outside ASCII reaches the daemon
/// through the CLI's client and is recorded character for character, a stranger outside ASCII is refused
/// by name, and a value that is not valid UTF-8 is refused before any service runs.
/// </summary>
public sealed class PrincipalHeaderEncodingTests
{
    private const string Project = "coanda";
    private static readonly CancellationToken None = CancellationToken.None;

    // Ids outside ASCII, ASCII ids holding a percent sign or an interior space, and ids that read as an
    // encoding of "José": its percent-encoding, and its UTF-8 bytes read as Latin-1. Each is sent as it is
    // and recorded unchanged.
    public static TheoryData<string> OwnerIds => ["José", "山田太郎", "a%20b", "a b", "Jos%C3%A9", "Jos\u00C3\u00A9"];

    [Theory]
    [MemberData(nameof(OwnerIds))]
    public async Task A_call_through_the_cli_client_on_a_node_whose_owner_id_is_the_given_id_records_exactly_that_id(string ownerId)
    {
        using var temp = new TemporaryHome();
        NodeOwnerRecord.Write(temp.Home, ownerId);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home, null);
        Assert.Equal(ownerId, client.PrincipalId);

        ParcelView added = await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest(Project, "idea", "The owner's", null), None);

        Assert.True(string.Equals(ownerId, added.CreatedBy, StringComparison.Ordinal), $"Recorded '{added.CreatedBy}', not '{ownerId}'.");

        await daemon.StopAsync(None);
    }

    [Theory]
    [InlineData("Zoë")]
    [InlineData("山田花子")]
    public async Task A_call_holding_only_the_daemon_token_whose_principal_header_names_a_stranger_outside_ascii_is_refused_by_that_name(string stranger)
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var http = Client(daemon, DaemonClient.HeaderEncoding);
        string refusal = $"The {DaemonHost.PrincipalHeader} header names '{stranger}', but a call holding only the daemon token acts as the node's owner, '{Environment.UserName}'; send '{Environment.UserName}' or no header.";

        using HttpRequestMessage request = AddRequest(daemon, stranger);
        using HttpResponseMessage response = await http.SendAsync(request, None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ErrorView? error = await response.Content.ReadFromJsonAsync<ErrorView>(None);
        Assert.Equal(refusal, error?.Error);
        using var cli = new DaemonClient(temp.Home, null);
        Assert.Empty(await cli.GetAsync<List<ParcelView>>($"/parcels?project={Project}", None));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_call_holding_only_the_daemon_token_whose_principal_header_is_not_valid_utf8_is_refused_before_any_service_runs()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);

        // Latin-1 sends U+00FF as the lone byte 0xFF, which no UTF-8 sequence starts with.
        using var http = Client(daemon, (_, _) => Encoding.Latin1);
        using HttpRequestMessage request = AddRequest(daemon, "\u00FF");
        using HttpResponseMessage response = await http.SendAsync(request, None);

        int status = (int)response.StatusCode;
        Assert.True(status is >= 400 and < 500, $"The daemon answered {status}.");
        using var cli = new DaemonClient(temp.Home, null);
        Assert.Empty(await cli.GetAsync<List<ParcelView>>($"/parcels?project={Project}", None));

        await daemon.StopAsync(None);
    }

    [Fact]
    public void The_cli_sends_only_the_principal_header_as_utf8_and_an_ascii_id_as_the_same_bytes_it_always_sent()
    {
        Assert.Same(Encoding.UTF8, DaemonClient.HeaderEncoding(DaemonHost.PrincipalHeader, null));
        Assert.Null(DaemonClient.HeaderEncoding(DaemonHost.TokenHeader, null));

        const string ascii = "brian.hall-9 a%20b ~!";
        Assert.Equal(Encoding.ASCII.GetBytes(ascii), Encoding.UTF8.GetBytes(ascii));
    }

    private static HttpClient Client(RunningDaemon daemon, HeaderEncodingSelector<HttpRequestMessage> selector) =>
        new(new SocketsHttpHandler { RequestHeaderEncodingSelector = selector }) { BaseAddress = new Uri(daemon.Info.BaseAddress) };

    private static HttpRequestMessage AddRequest(RunningDaemon daemon, string principal)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/parcels")
        {
            Content = JsonContent.Create(new ParcelAddRequest(Project, "idea", "Not the owner's", null)),
        };
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        request.Headers.Add(DaemonHost.PrincipalHeader, principal);
        return request;
    }
}
