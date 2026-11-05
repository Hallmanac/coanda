using System.Net;
using Coanda.Daemon.Api;
using Coanda.Sdk.Client;

namespace Coanda.Tests.Sdk.Client;

/// <summary>
/// The SDK's client for the core's API presents the module token in the header the daemon reads,
/// and its options come either explicitly or from the variables the SDK declares.
/// </summary>
public sealed class CoreApiClientTests
{
    [Fact]
    public void The_token_header_is_the_one_the_daemon_reads()
    {
        Assert.Equal(DaemonHost.TokenHeader, CoreApi.TokenHeader);
    }

    [Fact]
    public async Task A_call_presents_the_module_token_at_the_core_address()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new CoreApiClient(http, new CoreApiOptions(new Uri("http://127.0.0.1:47123"), "module-token"));

        Assert.True(await client.IsHealthyAsync(CancellationToken.None));

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("http://127.0.0.1:47123/health"), request.RequestUri);
        Assert.Equal(["module-token"], request.Headers.GetValues(DaemonHost.TokenHeader));
    }

    [Fact]
    public async Task A_core_that_answers_with_failure_or_cannot_be_reached_is_not_healthy()
    {
        var options = new CoreApiOptions(new Uri("http://127.0.0.1:47123"), "module-token");
        using var failing = new HttpClient(new RecordingHandler(HttpStatusCode.ServiceUnavailable));
        using var unreachable = new HttpClient(new RecordingHandler(failWith: new HttpRequestException("refused")));
        using var silent = new HttpClient(new RecordingHandler(failWith: new TaskCanceledException("the client's timeout ran out")));

        Assert.False(await new CoreApiClient(failing, options).IsHealthyAsync(CancellationToken.None));
        Assert.False(await new CoreApiClient(unreachable, options).IsHealthyAsync(CancellationToken.None));
        Assert.False(await new CoreApiClient(silent, options).IsHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_health_call_is_cancelled_rather_than_unhealthy()
    {
        var options = new CoreApiOptions(new Uri("http://127.0.0.1:47123"), "module-token");
        using var http = new HttpClient(new RecordingHandler());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CoreApiClient(http, options).IsHealthyAsync(cancelled.Token));
    }

    [Fact]
    public void The_options_build_from_the_variables_the_sdk_declares()
    {
        string? address = Environment.GetEnvironmentVariable(CoreApi.AddressVariable);
        string? token = Environment.GetEnvironmentVariable(CoreApi.TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(CoreApi.AddressVariable, "http://127.0.0.1:47124");
            Environment.SetEnvironmentVariable(CoreApi.TokenVariable, "from-the-environment");

            CoreApiOptions options = CoreApiOptions.FromEnvironment();

            Assert.Equal(new Uri("http://127.0.0.1:47124"), options.Address);
            Assert.Equal("from-the-environment", options.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CoreApi.AddressVariable, address);
            Environment.SetEnvironmentVariable(CoreApi.TokenVariable, token);
        }
    }

    [Theory]
    [InlineData(null, "token", CoreApi.AddressVariable)]
    [InlineData("http://127.0.0.1:47124", " ", CoreApi.TokenVariable)]
    [InlineData("127.0.0.1:47124", "token", CoreApi.AddressVariable)]
    [InlineData("file:///tmp/core", "token", CoreApi.AddressVariable)]
    public void A_missing_or_malformed_variable_is_refused_by_name(string? address, string? token, string named)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CoreApi.AddressVariable] = address,
            [CoreApi.TokenVariable] = token,
        };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => CoreApiOptions.FromVariables(name => variables.GetValueOrDefault(name)));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, Exception? failWith = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return failWith is null
                ? Task.FromResult(new HttpResponseMessage(status))
                : Task.FromException<HttpResponseMessage>(failWith);
        }
    }
}
