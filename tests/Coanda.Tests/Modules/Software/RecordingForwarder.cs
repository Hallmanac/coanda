using System.Collections.Concurrent;
using System.Text;
using Coanda.Sdk.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// A loopback server standing between a module and the core's API: it forwards every call to the daemon
/// and records what arrived (the method, the path and query, the token presented, the body) and the
/// status the daemon answered.
/// </summary>
internal sealed class RecordingForwarder : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly HttpClient forward;
    private readonly ConcurrentQueue<Call> calls;

    private RecordingForwarder(WebApplication app, HttpClient forward, ConcurrentQueue<Call> calls, Uri address)
    {
        this.app = app;
        this.forward = forward;
        this.calls = calls;
        Address = address;
    }

    /// <summary>One call as it arrived and as the daemon answered it.</summary>
    public sealed record Call(string Method, string PathAndQuery, string Token, string Body, int Status);

    public Uri Address { get; }

    public IReadOnlyList<Call> Calls => [.. calls];

    public static async Task<RecordingForwarder> StartAsync(string target, CancellationToken cancellationToken)
    {
        var calls = new ConcurrentQueue<Call>();
        var forward = new HttpClient();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.Run(async context =>
        {
            string pathAndQuery = $"{context.Request.Path}{context.Request.QueryString}";
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"{target}{pathAndQuery}");
            string token = context.Request.Headers[CoreApi.TokenHeader].ToString();
            request.Headers.Add(CoreApi.TokenHeader, token);
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            if (body.Length > 0)
            {
                request.Content = new ByteArrayContent(body.ToArray());
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }

            using HttpResponseMessage response = await forward.SendAsync(request, context.RequestAborted);
            calls.Enqueue(new Call(context.Request.Method, pathAndQuery, token, Encoding.UTF8.GetString(body.ToArray()), (int)response.StatusCode));
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.StartAsync(cancellationToken);
        IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The forwarder has no address.");
        return new RecordingForwarder(app, forward, calls, new Uri(addresses.Addresses.Single()));
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        forward.Dispose();
    }
}
