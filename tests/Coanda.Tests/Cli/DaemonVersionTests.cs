using System.Collections.Concurrent;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Tests.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Cli;

/// <summary>
/// What the CLI does with a running daemon whose daemon.json records a version other than its
/// own: every command is refused before it is sent, while status and stop still reach it.
/// </summary>
public sealed class DaemonVersionTests
{
    private const string OtherVersion = "0.0.1+0000000000000000000000000000000000000000";

    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_get_a_post_and_a_put_to_a_daemon_of_another_version_are_refused_and_never_sent()
    {
        await using var running = await MismatchedDaemon.StartAsync(OtherVersion, None);
        using var client = new DaemonClient(running.Home, sessionToken: null);
        string refusal = $"The running daemon is version {OtherVersion}, but this coa is version {DaemonHost.Version}, so nothing was sent to it; run coa daemon stop, then run this command again.";

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<List<ParcelView>>("/parcels?project=coanda", None));
        Assert.Equal(refusal, exception.Message);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("coanda", "idea", "Never sent", null), None));
        Assert.Equal(refusal, exception2.Message);
        DomainException exception3 = await Assert.ThrowsAsync<DomainException>(() => client.PutAsync<ProjectView>("/projects/coanda", new { }, None));
        Assert.Equal(refusal, exception3.Message);

        Assert.All(running.Recorder.Paths, path => Assert.True(path == "/health", "only the health probe reaches a daemon of another version"));
        Assert.Equal(3, running.Recorder.Paths.Count);
    }

    [Fact]
    public async Task A_daemon_that_recorded_no_version_is_refused_the_same_way()
    {
        await using var running = await MismatchedDaemon.StartAsync(version: null, None);
        using var client = new DaemonClient(running.Home, sessionToken: null);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<List<ParcelView>>("/parcels?project=coanda", None));
        Assert.Equal($"The running daemon recorded no version, but this coa is version {DaemonHost.Version}, so nothing was sent to it; run coa daemon stop, then run this command again.", exception.Message);

        Assert.Equal(["/health"], running.Recorder.Paths);
    }

    [Fact]
    public async Task A_session_is_told_that_a_person_must_restart_the_daemon()
    {
        await using var running = await MismatchedDaemon.StartAsync(OtherVersion, None);
        using var client = new DaemonClient(running.Home, sessionToken: "a-session-token");

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("coanda", "idea", "Never sent", null), None));
        Assert.Equal($"The running daemon is version {OtherVersion}, but this coa is version {DaemonHost.Version}, so nothing was sent to it; a person must restart the daemon, since a session cannot stop it.", exception.Message);

        Assert.Equal(["/health"], running.Recorder.Paths);
    }

    [Fact]
    public async Task Status_and_stop_still_reach_a_daemon_of_another_version()
    {
        await using var running = await MismatchedDaemon.StartAsync(OtherVersion, None);
        using var client = new DaemonClient(running.Home, sessionToken: null);

        DaemonStatusView? status = await client.StatusAsync(None);
        Assert.NotNull(status);
        // status reports the version the daemon's daemon.json records
        Assert.Equal(OtherVersion, status.Version);
        Assert.Equal(running.Daemon.Info.Pid, status.Pid);
        Assert.Equal(running.Home.Path, status.Home);

        await client.StopAsync(None);
        await running.Daemon.WaitForShutdownAsync(None);

        Assert.Equal(["/health", "/status", "/health", "/stop"], running.Recorder.Paths);
        Assert.False(File.Exists(running.Home.DaemonInfoPath), "the daemon stopped cleanly");
    }

    [Fact]
    public async Task The_start_check_names_both_versions_for_a_daemon_of_another_version_and_passes_one_of_the_same()
    {
        await using var running = await MismatchedDaemon.StartAsync(OtherVersion, None);
        using var client = new DaemonClient(running.Home, sessionToken: null);

        DaemonInfo? found = await client.FindRunningAsync(None);
        Assert.NotNull(found);

        Assert.Equal($"A daemon from another build is already running on {found.BaseAddress} (pid {found.Pid}): it is version {OtherVersion}, but this coa is version {DaemonHost.Version}. Run coa daemon stop first, then coa daemon start.", DaemonClient.RefusalToStartBeside(found));
        Assert.Null(DaemonClient.RefusalToStartBeside(found with { Version = DaemonHost.Version }));
    }

    [Fact]
    public async Task A_command_to_a_daemon_of_the_same_version_is_sent_and_answered_as_before()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, "coanda", None);
        using var client = new DaemonClient(temp.Home, sessionToken: null);

        Assert.Equal(DaemonHost.Version, daemon.Info.Version);
        ParcelView added = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest("coanda", "idea", "Sent", null), None);
        ParcelView shown = await client.GetAsync<ParcelView>($"/parcels/{added.Id}?project=coanda", None);
        Assert.Equal("Sent", shown.Title);

        await daemon.StopAsync(None);
    }

    /// <summary>
    /// The in-process daemon behind a recorder, with its daemon.json rewritten to point at the
    /// recorder and to record <c>version</c>, so the client finds a daemon of another build.
    /// </summary>
    private sealed class MismatchedDaemon : IAsyncDisposable
    {
        private readonly TemporaryHome temp;

        private MismatchedDaemon(TemporaryHome temp, RunningDaemon daemon, RecordingForwarder recorder)
        {
            this.temp = temp;
            Daemon = daemon;
            Recorder = recorder;
        }

        public Coanda.Daemon.NodeHome Home => temp.Home;

        public RunningDaemon Daemon { get; }

        public RecordingForwarder Recorder { get; }

        public static async Task<MismatchedDaemon> StartAsync(string? version, CancellationToken cancellationToken)
        {
            var temp = new TemporaryHome();
            RunningDaemon daemon = await Daemons.StartAsync(temp.Home, cancellationToken);
            RecordingForwarder recorder = await RecordingForwarder.StartAsync(daemon.Info.BaseAddress, cancellationToken);

            // A null version is what an older daemon.json without the field deserializes to.
            (daemon.Info with { Port = recorder.Port, Version = version! }).Save(temp.Home);
            return new MismatchedDaemon(temp, daemon, recorder);
        }

        public async ValueTask DisposeAsync()
        {
            await Recorder.DisposeAsync();
            await Daemon.DisposeAsync();
            temp.Dispose();
        }
    }

    /// <summary>A loopback server that records the path of every request it receives and forwards it to the daemon.</summary>
    private sealed class RecordingForwarder : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly HttpClient forward;
        private readonly ConcurrentQueue<string> paths;

        private RecordingForwarder(WebApplication app, HttpClient forward, ConcurrentQueue<string> paths, int port)
        {
            this.app = app;
            this.forward = forward;
            this.paths = paths;
            Port = port;
        }

        public int Port { get; }

        public IReadOnlyList<string> Paths => [.. paths];

        public static async Task<RecordingForwarder> StartAsync(string target, CancellationToken cancellationToken)
        {
            var paths = new ConcurrentQueue<string>();
            var forward = new HttpClient();
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            app.Run(async context =>
            {
                paths.Enqueue(context.Request.Path.Value ?? string.Empty);
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"{target}{context.Request.Path}{context.Request.QueryString}");
                foreach ((string name, Microsoft.Extensions.Primitives.StringValues values) in context.Request.Headers)
                {
                    if (name.StartsWith("X-Coanda-", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Add(name, values.ToArray());
                    }
                }

                if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                {
                    using var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                    request.Content = new ByteArrayContent(body.ToArray());
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }

                using HttpResponseMessage response = await forward.SendAsync(request, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            });
            await app.StartAsync(cancellationToken);
            IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The recorder has no address.");
            int port = new Uri(addresses.Addresses.Single()).Port;
            return new RecordingForwarder(app, forward, paths, port);
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            forward.Dispose();
        }
    }
}
