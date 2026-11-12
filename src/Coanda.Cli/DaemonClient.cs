using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Executors.ClaudeCode;
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
    /// <summary>
    /// How long the CLI waits for a daemon it started to answer. The bound is long because a start makes
    /// every repo's clone and waits for its modules to be healthy before it writes the address file the CLI
    /// waits on; a daemon that exits before then is reported as soon as it exits, not at this bound.
    /// </summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    // One client for the process, so a long-running command never exhausts sockets by making one per call.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { RequestHeaderEncodingSelector = HeaderEncoding });

    /// <summary>
    /// The encoding a request header's value is sent in: UTF-8 for the principal header, so an id outside ASCII
    /// goes as its UTF-8 bytes, which Kestrel decodes by default, and an id of printable ASCII goes byte for byte
    /// as it always has. Every other header keeps the handler's default, which sends ASCII only.
    /// </summary>
    internal static Encoding? HeaderEncoding(string name, HttpRequestMessage? request) =>
        string.Equals(name, DaemonHost.PrincipalHeader, StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8
            : null;

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

        // Where the log and the output file stood before the start, so a report reads only what this start wrote.
        long logStart = LengthOf(Home.LogPath);
        long outputStart = LengthOf(Home.DaemonOutputPath);
        using DetachedProcess daemon = StartDetached();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken);
            running = await FindRunningAsync(cancellationToken);
            if (running is not null)
            {
                return running;
            }

            if (daemon.HasExited)
            {
                throw new DomainException(ExitedBeforeReady(daemon.ExitCode, logStart, outputStart));
            }
        }

        throw new DomainException($"The daemon did not start within {StartTimeout.TotalSeconds:0} seconds; see {Home.LogPath}.");
    }

    /// <summary>
    /// The running daemon a command may send to: the one <see cref="EnsureRunningAsync"/> finds or
    /// starts, refused before anything is sent when its daemon.json records a version other than
    /// this build's, since a daemon from another build may not know this build's words and routes.
    /// </summary>
    private async Task<DaemonInfo> SameVersionAsync(CancellationToken cancellationToken)
    {
        DaemonInfo info = await EnsureRunningAsync(cancellationToken);
        if (IsOtherVersion(info))
        {
            string restart = sessionToken is null
                ? "run coa daemon stop, then run this command again"
                : "a person must restart the daemon, since a session cannot stop it";
            throw new DomainException(
                $"The running daemon {VersionOf(info)}, but this coa is version {DaemonHost.Version}, so nothing was sent to it; {restart}.");
        }

        return info;
    }

    /// <summary>
    /// What <c>coa daemon start</c> says when it finds <paramref name="running"/> already running:
    /// null when that daemon is this build's, and otherwise a refusal naming both versions.
    /// </summary>
    public static string? RefusalToStartBeside(DaemonInfo running) =>
        IsOtherVersion(running)
            ? $"A daemon from another build is already running on {running.BaseAddress} (pid {running.Pid}): it {VersionOf(running)}, but this coa is version {DaemonHost.Version}. Run coa daemon stop first, then coa daemon start."
            : null;

    /// <summary>True when <paramref name="info"/> records no version or one not exactly this build's.</summary>
    private static bool IsOtherVersion(DaemonInfo info) => !string.Equals(info.Version, DaemonHost.Version, StringComparison.Ordinal);

    private static string VersionOf(DaemonInfo info) =>
        string.IsNullOrWhiteSpace(info.Version)
            ? "recorded no version"
            : $"is version {info.Version}";

    /// <summary>
    /// What happens before a detached start: the home is created or narrowed to owner-only access,
    /// then checked for a record from an older build or one holding a shape an earlier build wrote,
    /// and for a node configuration the daemon would refuse. The check opens the database, so the
    /// narrowing comes first.
    /// </summary>
    internal void PrepareToStart()
    {
        Home.EnsureExists();

        // Said here as well as by the daemon, which reports these only to its log.
        PreRenameStore.ThrowIfFound(Home);
        EarlierShapes.ThrowIfFound(Home);
        _ = NodeConfiguration.Load(Home);
    }

    /// <summary>
    /// Starts <c>coa daemon run --log-to-file</c> apart from this process: in a process group of its
    /// own, without this process's console, reading the null device and printing to the home's
    /// daemon output file, so it holds none of the caller's handles and outlives the caller's
    /// process group and terminal. It gets the caller's whole environment with the home named in it.
    /// </summary>
    private DetachedProcess StartDetached()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The CLI cannot find its own executable.");
        Dictionary<string, string> environment = OperatingSystem.IsWindows()
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable is { Key: string name, Value: string value })
            {
                environment[name] = value;
            }
        }

        environment[NodeHome.EnvironmentVariable] = Home.Path;
        using var input = new FileStream(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", FileMode.Open, FileAccess.Read, Inherited(FileShare.ReadWrite));
        using FileStream output = OwnerOnly.Open(Home.DaemonOutputPath, FileMode.Append, FileAccess.Write, Inherited(FileShare.ReadWrite | FileShare.Delete));
        try
        {
            return DetachedProcess.Start(
                executable, ["daemon", "run", "--log-to-file"], environment, Home.Path, input.SafeFileHandle, output.SafeFileHandle, output.SafeFileHandle);
        }
        catch (Exception failure) when (failure is Win32Exception or IOException)
        {
            throw new DomainException($"The daemon could not be started from {executable}: {failure.Message}");
        }
    }

    /// <summary>A handle a process is started with must be inheritable on Windows; elsewhere the spawn hands it over itself.</summary>
    private static FileShare Inherited(FileShare share) => OperatingSystem.IsWindows() ? share | FileShare.Inheritable : share;

    /// <summary>
    /// The report of a daemon that exited before it answered: what it appended to its log and what
    /// it printed outside it during this start, or, when it wrote neither, its exit code and the log's path.
    /// </summary>
    private string ExitedBeforeReady(int? exitCode, long logStart, long outputStart)
    {
        string exit = exitCode is { } code ? $" with code {code}" : string.Empty;
        string logged = ReadFrom(Home.LogPath, logStart);
        string printed = ReadFrom(Home.DaemonOutputPath, outputStart);
        string intro = $"The daemon exited{exit} before it was ready.";
        return (logged.Length > 0, printed.Length > 0) switch
        {
            (true, true) => $"{intro} It wrote this to its log at {Home.LogPath}:{Environment.NewLine}{logged}{Environment.NewLine}"
                + $"It printed this outside its log, to {Home.DaemonOutputPath}:{Environment.NewLine}{printed}",
            (true, false) => $"{intro} It wrote this to its log at {Home.LogPath}:{Environment.NewLine}{logged}",
            (false, true) => $"{intro} It printed this outside its log, to {Home.DaemonOutputPath}:{Environment.NewLine}{printed}",
            (false, false) => $"{intro} It wrote nothing to its log at {Home.LogPath} and printed nothing outside it.",
        };
    }

    /// <summary>The length of the file at <paramref name="path"/>, or zero when there is no file there.</summary>
    private static long LengthOf(string path) => new FileInfo(path) is { Exists: true } file ? file.Length : 0;

    /// <summary>What the file at <paramref name="path"/> holds from <paramref name="offset"/> on, trimmed; empty when there is no file to read.</summary>
    private static string ReadFrom(string path, long offset)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Min(offset, file.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd().Trim();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        DaemonInfo info = await SameVersionAsync(cancellationToken);
        using HttpRequestMessage request = Request(HttpMethod.Get, info, path);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        DaemonInfo info = await SameVersionAsync(cancellationToken);
        using HttpRequestMessage request = Request(HttpMethod.Post, info, path);
        request.Content = JsonContent.Create(body ?? new { });
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    public async Task<T> PutAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        DaemonInfo info = await SameVersionAsync(cancellationToken);
        using HttpRequestMessage request = Request(HttpMethod.Put, info, path);
        request.Content = JsonContent.Create(body ?? new { });
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    /// <summary>
    /// The running daemon's status, or null when none answers. Unlike every other read, it is sent
    /// to a daemon of any version, so a person can see which build is running before stopping it.
    /// </summary>
    public async Task<DaemonStatusView?> StatusAsync(CancellationToken cancellationToken)
    {
        DaemonInfo? info = await FindRunningAsync(cancellationToken);
        if (info is null)
        {
            return null;
        }

        using HttpRequestMessage request = Request(HttpMethod.Get, info, "/status");
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        return await ReadAsync<DaemonStatusView>(response, cancellationToken);
    }

    /// <summary>Asks the running daemon to stop, whatever its version.</summary>
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

    /// <summary>
    /// The principal the CLI names in its header: the node's owner id record (<see cref="NodeOwnerRecord"/>), read
    /// at each call so a join or a registration takes effect at once, or the operating system account's name on a
    /// node that has none.
    /// </summary>
    internal string PrincipalId => NodeOwnerRecord.Read(Home) ?? Environment.UserName;

    private HttpRequestMessage Request(HttpMethod method, DaemonInfo info, string path)
    {
        var request = new HttpRequestMessage(method, $"{info.BaseAddress}{path}");
        request.Headers.Add(DaemonHost.TokenHeader, sessionToken ?? info.Token);
        // The id is sent as its UTF-8 bytes (HeaderEncoding), unescaped, so ASCII ids are unchanged on the wire.
        request.Headers.Add(DaemonHost.PrincipalHeader, PrincipalId);
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
