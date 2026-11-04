using System.Diagnostics;
using System.Net.Http.Json;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Store;

namespace Coanda.Cli;

/// <summary>
/// The CLI's only way to the record: the daemon's loopback API. Starts the daemon when it is not
/// running, so a user never has to. Inside an agent step's session, a client for the node home whose
/// daemon issued the session token presents that token instead of the daemon token, and never
/// starts a daemon that does not answer: the one that started the session is the only one that
/// resolves it. A client for any other home never sees the token and works as it would outside a
/// session.
/// </summary>
public sealed class DaemonClient : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    // One client for the process, so a long-running command never exhausts sockets by making one per call.
    private static readonly HttpClient Http = new();

    private readonly string? sessionToken;

    /// <summary>
    /// A client for <paramref name="home"/> that presents the session token the environment holds
    /// when the environment also names <paramref name="home"/> as the home that token belongs to.
    /// </summary>
    public DaemonClient(NodeHome home)
        : this(
            home,
            SessionTokenFor(
                home,
                Environment.GetEnvironmentVariable(SessionAccess.TokenVariable),
                Environment.GetEnvironmentVariable(SessionAccess.HomeVariable)))
    {
    }

    /// <summary>
    /// A client for <paramref name="home"/> that presents <paramref name="sessionToken"/> when one is
    /// given, whatever the environment holds, and the daemon token when it is null or blank.
    /// </summary>
    public DaemonClient(NodeHome home, string? sessionToken)
    {
        Home = home;
        this.sessionToken = string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken;
    }

    public NodeHome Home { get; }

    /// <summary>
    /// The session token a client for <paramref name="home"/> presents: <paramref name="token"/> when
    /// <paramref name="issuingHome"/> is the same home, and null otherwise, so a token is only ever
    /// sent to the daemon that issued it. Paths are compared in full, without regard to case on
    /// Windows.
    /// </summary>
    internal static string? SessionTokenFor(NodeHome home, string? token, string? issuingHome)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(issuingHome))
        {
            return null;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalized(home.Path), Normalized(issuingHome), comparison)
            ? token
            : null;
    }

    private static string Normalized(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

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

        if (sessionToken is not null)
        {
            throw new DomainException(
                $"The daemon that started this session is not running for the node home {Home.Path}, so nothing was done; a session's token works only with that daemon, and a new one is not started from inside a session.");
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
    /// then checked for a record from an older build and for a node configuration the daemon would
    /// refuse. The check opens the database, so the narrowing comes first.
    /// </summary>
    internal void PrepareToStart()
    {
        Home.EnsureExists();

        // Said here as well as by the daemon, which reports these only to its log.
        PreRenameStore.ThrowIfFound(Home);
        _ = NodeConfiguration.Load(Home);
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

    public async Task<T> PutAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        DaemonInfo info = await EnsureRunningAsync(cancellationToken);
        using HttpRequestMessage request = Request(HttpMethod.Put, info, path);
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

    private HttpRequestMessage Request(HttpMethod method, DaemonInfo info, string path)
    {
        var request = new HttpRequestMessage(method, $"{info.BaseAddress}{path}");
        request.Headers.Add(DaemonHost.TokenHeader, sessionToken ?? info.Token);
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
