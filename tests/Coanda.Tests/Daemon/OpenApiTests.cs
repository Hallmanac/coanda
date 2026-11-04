using System.Net;
using System.Text.Json;
using Coanda.Daemon.Api;

namespace Coanda.Tests.Daemon;

public sealed class OpenApiTests
{
    [Fact]
    public async Task The_daemon_serves_its_openapi_document_behind_the_token()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None);
        using var http = new HttpClient();
        string address = $"{daemon.Info.BaseAddress}/openapi/v1.json";

        using (HttpResponseMessage refused = await http.GetAsync(address, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Add(DaemonHost.TokenHeader, daemon.Info.Token);
        using HttpResponseMessage response = await http.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using Stream body = await response.Content.ReadAsStreamAsync(CancellationToken.None);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: CancellationToken.None);
        List<string> paths = [.. document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name)];
        string[] expected = ["/parcels", "/flow-runs", "/flows", "/agents", "/types", "/status"];
        Assert.All(expected, path => Assert.Contains(path, paths));

        await daemon.StopAsync(CancellationToken.None);
    }
}
