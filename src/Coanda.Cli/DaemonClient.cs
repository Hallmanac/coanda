using System.Diagnostics;
using System.Net.Http.Json;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Store;

namespace Coanda.Cli;

/// <summary>
/// The CLI's only way to the record: the daemon's loopback API. Starts the daemon when it is not
/// running, so a user never has to.
/// </summary>
public sealed class DaemonClient(NodeHome home) : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    // One client for the process, so a long-running command never exhausts sockets by making one per call.
    private static readonly HttpClient Http = new();

    public NodeHome Home { get; } = home;

    public async Task<DaemonInfo?> FindRunningAsync(CancellationToken cancellationToken)
    {
        DaemonInfo? info = DaemonInfo.Load(Home);
        if (info is null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await Http.GetAsync($"{info.BaseAddress}/health", cancellationToken);
            return response.IsSuccessStatusCode ? info : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<DaemonInfo> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        DaemonInfo? running = await FindRunningAsync(cancellationToken);
        if (running is not null)
        {
            return running;
        }

        PrepareToStart();
        DaemonInfo.Delete(Home);
        StartDetached();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken);
            running = await FindRunningAsync(cancellationToken);
            if (running is not null)
            {
                return running;
            }
        }

        throw new DomainException($"The daemon did not start within {StartTimeout.TotalSeconds:0} seconds; see {Home.LogPath}.");
    }

    /// <summary>
    /// What happens before a detached start: the home is created or narrowed to owner-only access,
    /// then checked for a record from an older build. The check opens the database, so the
    /// narrowing comes first.
    /// </summary>
    internal void PrepareToStart()
    {
        Home.EnsureExists();

        // Said here as well as by the daemon, which reports it only to its log.
        PreRenameStore.ThrowIfFound(Home);
    }

    private void StartDetached()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The CLI cannot find its own executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Home.Path,
        };
        start.ArgumentList.Add("daemon");
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--log-to-file");
        start.Environment[NodeHome.EnvironmentVariable] = Home.Path;
        Process.Start(start);
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        DaemonInfo info = await EnsureRunningAsync(cancellationToken);
        using HttpRequestMessage request = Request(HttpMethod.Get, info, path);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        DaemonInfo info = await EnsureRunningAsync(cancellationToken);
        using HttpRequestMessage request = Request(HttpMethod.Post, info, path);
        request.Content = JsonContent.Create(body ?? new { });
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        DaemonInfo? info = await FindRunningAsync(cancellationToken);
        if (info is null)
        {
            throw new DomainException("No daemon is running for this node home.");
        }

        using HttpRequestMessage request = Request(HttpMethod.Post, info, "/stop");
        request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage Request(HttpMethod method, DaemonInfo info, string path)
    {
        var request = new HttpRequestMessage(method, $"{info.BaseAddress}{path}");
        request.Headers.Add(DaemonHost.TokenHeader, info.Token);
        request.Headers.Add(DaemonHost.PrincipalHeader, Environment.UserName);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            return value ?? throw new DomainException("The daemon answered with nothing.");
        }

        ErrorView? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ErrorView>(cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        throw new DomainException(error?.Error ?? $"The daemon answered {(int)response.StatusCode}.");
    }

    /// <summary>
    /// Kept so every caller's <c>using</c> still reads naturally; the shared client outlives every
    /// instance, so there is nothing of this instance's own to release.
    /// </summary>
    public void Dispose()
    {
    }
}
