using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flows;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// Runs each judgment as a headless Claude Code session: a <c>claude</c> process in print mode with
/// stream-json output, started in the flow's work directory, given its prompt on standard input and
/// nothing after it. A resume starts another process on the same session id with the same
/// arguments, working directory, and environment. Every process's stream output is appended, as it
/// is printed, to the session's transcript in the flow's transcript directory. Collecting waits for
/// the process to end and reads its result.
/// </summary>
/// <remarks>
/// Each process carries a spend cap, <c>--max-budget-usd</c>: the spawn's cost ceiling for the
/// spawn, and that ceiling less the cost already collected for the session for a resume, because
/// the CLI does not count an earlier process's spend against a resumed process's cap. The CLI
/// checks its cap between turns, so a single turn can overshoot it. The adapter sets no time limit;
/// the daemon's wall-clock limit is authoritative. Stopping a session kills its process tree, and
/// so does the daemon's stop, through <see cref="StopAllAsync"/>: the adapter cannot reattach to a
/// session across a restart, so one left running would go on spending, and editing the work
/// directory, beside the session the next start launches there. A stopped session is let go at once;
/// only its id is kept, so a second stop of it still succeeds.
/// </remarks>
public sealed class ClaudeCodeExecutor : IAgentExecutor
{
    /// <summary>The executable started when the node configuration names none: <c>claude</c>, looked up on the PATH.</summary>
    public const string DefaultExecutable = "claude";

    /// <summary>How much of the process's error output, and of an error result's text, a failure quotes.</summary>
    public const int QuotedLength = 500;

    /// <summary>How long, once a process has exited, its output may take to reach its end before the read gives up.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly NodeHome home;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Session> sessions = [];
    private readonly HashSet<Guid> stopped = [];
    private bool closed;

    /// <param name="executable">The configured path of the claude executable, or a bare name looked up on the PATH.</param>
    /// <param name="home">The node home, which says where each flow's transcripts go.</param>
    /// <param name="environment">The environment a session's processes start from, before the adapter removes and sets what it does.</param>
    public ClaudeCodeExecutor(string executable, NodeHome home, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(environment);
        Executable = executable;
        this.home = home;
        this.environment = environment;
    }

    public string Executable { get; }

    /// <summary>The environment of the daemon's own process, read once, as the base a session's processes start from.</summary>
    public static IReadOnlyDictionary<string, string> CurrentEnvironment()
    {
        var variables = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry is { Key: string name, Value: string value })
            {
                variables[name] = value;
            }
        }

        return variables;
    }

    public async ValueTask<SessionHandle> SpawnAsync(
        AgentDefinition agent,
        string context,
        string workingDirectory,
        SpawnCaps caps,
        string answerSchema,
        CredentialProfile? credentialProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(caps);
        cancellationToken.ThrowIfCancellationRequested();
        ClaudeCodeInvocation.Require(agent);
        string prompt = ClaudeCodeInvocation.Prompt(context, answerSchema);
        IReadOnlyDictionary<string, string> processEnvironment = ClaudeCodeInvocation.Environment(environment, credentialProfile);
        string executable = ClaudeExecutable.Resolve(Executable, processEnvironment);
        if (caps.CostCeilingUsd <= 0m)
        {
            throw new InvalidOperationException(
                $"The spawn's cost ceiling of {Dollars(caps.CostCeilingUsd)} leaves nothing to spend, so no claude process was started.");
        }

        RefuseOnceClosed();
        string persona = ClaudeCodeInvocation.Persona(agent.Persona);
        SessionFiles.Write(workingDirectory, SessionFiles.PersonaFile, persona);
        var handle = new SessionHandle(Ids.New(), agent.Name);
        var session = new Session(
            handle,
            executable,
            workingDirectory,
            persona,
            ClaudeCodeInvocation.SessionArguments(agent, credentialProfile),
            processEnvironment,
            caps.CostCeilingUsd,
            Path.Combine(home.TranscriptDirectoryOf(workingDirectory), $"{handle.Id}.jsonl"));

        Attempt first = await StartAsync(
            session,
            ClaudeCodeInvocation.SpawnArguments(handle.Id, session.Arguments, caps.CostCeilingUsd),
            prompt,
            cancellationToken);
        lock (gate)
        {
            if (!closed)
            {
                session.Current = first;
                sessions.Add(handle.Id, session);
                return handle;
            }
        }

        // The daemon began stopping while the process started; nothing would hold it after the stop.
        first.Kill();
        throw Closed();
    }

    public async ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        Session session = RequireLive(handle);
        Attempt attempt = session.Current ?? throw new InvalidOperationException($"Session {handle.Id} has no process to collect.");
        if (attempt.Collected is { } already)
        {
            return already;
        }

        await attempt.Ended.WaitAsync(cancellationToken);
        lock (gate)
        {
            if (attempt.Collected is { } collected)
            {
                return collected;
            }

            ClaudeResult result = attempt.Stream.Result ?? throw new InvalidOperationException(NoResult(attempt));
            if (result.IsError)
            {
                // Not collected: the attempt stays in progress, so the stop the judgment makes after
                // this failure reports its usage.
                string subtype = result.Subtype is null or "success" ? string.Empty : $" ({result.Subtype})";
                throw new InvalidOperationException(
                    $"The claude CLI ended the session's process with an error result{subtype}: {Quoted(result.ErrorText)}");
            }

            Measurement usage = result.Usage(session.ReportedTotalCostUsd);
            session.CollectedCostUsd += usage.CostUsd;
            session.ReportedTotalCostUsd = result.TotalCostUsd ?? session.ReportedTotalCostUsd + usage.CostUsd;
            attempt.Collected = new CollectedAnswer(ClaudeAnswer.Trailing(result.Text), session.TranscriptPath, usage);
            return attempt.Collected;
        }
    }

    public async ValueTask ResumeAsync(SessionHandle handle, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session session = RequireLive(handle);
        decimal remaining;
        lock (gate)
        {
            if (session.Current is { Collected: null })
            {
                throw new InvalidOperationException($"Session {handle.Id} still has a process whose answer was not collected, so it cannot be resumed.");
            }

            remaining = session.CeilingUsd - session.CollectedCostUsd;
        }

        if (remaining <= 0m)
        {
            throw new InvalidOperationException(
                $"Session {handle.Id} has already cost {Dollars(session.CollectedCostUsd)} of its cost ceiling of {Dollars(session.CeilingUsd)}, so no claude process was started to resume it.");
        }

        // The resumed process reads the persona file again, so it is written fresh rather than left
        // as the session's earlier process may have rewritten it.
        SessionFiles.Write(session.WorkingDirectory, SessionFiles.PersonaFile, session.Persona);
        Attempt next = await StartAsync(
            session,
            ClaudeCodeInvocation.ResumeArguments(handle.Id, session.Arguments, remaining),
            message,
            cancellationToken);
        lock (gate)
        {
            if (sessions.ContainsKey(handle.Id))
            {
                session.Current = next;
                return;
            }
        }

        // The session was stopped, by a stop or the daemon's, while this process started.
        next.Kill();
        throw new InvalidOperationException($"Session {handle.Id} has been stopped.");
    }

    /// <summary>Refuses every call: sending into a live session is not supported by this executor yet.</summary>
    public ValueTask SendAsync(SessionHandle handle, string message, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The claude-code executor does not support sending into a live session yet.");

    /// <summary>
    /// Kills the session's process together with every process it started, and reports its attempt
    /// in progress: the result's usage when the process printed one that no collect returned (an
    /// error result), otherwise the tokens and count of the assistant messages it printed, with no
    /// cost, and its duration up to the stop. Stopping a session whose process has already ended,
    /// or one already stopped, succeeds.
    /// </summary>
    public async ValueTask<StopReport?> StopAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Session session;
        Attempt? attempt;
        lock (gate)
        {
            if (stopped.Contains(handle.Id))
            {
                return null;
            }

            if (!sessions.Remove(handle.Id, out Session? held))
            {
                throw new InvalidOperationException($"The claude-code executor holds no session {handle.Id}.");
            }

            stopped.Add(handle.Id);
            session = held;
            attempt = held.Current;
        }

        if (attempt is null || attempt.Collected is not null)
        {
            return null;
        }

        attempt.Kill();
        await attempt.Ended.WaitAsync(cancellationToken);
        lock (gate)
        {
            if (attempt.Stream.Result is { } result)
            {
                return new StopReport(result.Usage(session.ReportedTotalCostUsd));
            }

            return attempt.Stream.MessageCount == 0
                ? null
                : new StopReport(new Measurement(attempt.Stream.MessageTokens, 0m, attempt.Stream.MessageCount, attempt.Elapsed), CostReported: false);
        }
    }

    public ValueTask<IReadOnlyList<SessionHandle>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<SessionHandle> live = [.. sessions.Values.Select(s => s.Handle)];
            return ValueTask.FromResult(live);
        }
    }

    /// <summary>
    /// Returns the handle only for a session this process still holds and has not stopped. Reattach
    /// across a restart is not implemented: the daemon's stop kills every session it holds, so a
    /// handle from before a restart names a session that has already ended.
    /// </summary>
    public ValueTask<SessionHandle?> ReattachAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            SessionHandle? held = sessions.TryGetValue(handle.Id, out Session? session)
                ? session.Handle
                : null;
            return ValueTask.FromResult(held);
        }
    }

    /// <summary>
    /// Kills the process of every session the executor holds, together with every process it
    /// started, and waits for each to end, so no session outlives the daemon's stop. The usage of an
    /// attempt killed here is not reported: a daemon stop records nothing for the step in progress,
    /// which the next start performs again. Once this has begun, the executor starts no process.
    /// </summary>
    public async ValueTask StopAllAsync(CancellationToken cancellationToken)
    {
        List<Attempt> killed = [];
        lock (gate)
        {
            closed = true;
            foreach ((Guid id, Session session) in sessions)
            {
                stopped.Add(id);
                if (session.Current is { Collected: null } attempt)
                {
                    killed.Add(attempt);
                }
            }

            sessions.Clear();
        }

        foreach (Attempt attempt in killed)
        {
            attempt.Kill();
        }

        await Task.WhenAll(killed.Select(a => a.Ended)).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Starts one claude process for the session: its prompt on standard input, which is then closed,
    /// and its output read into the transcript and the stream as it is printed.
    /// </summary>
    private static async Task<Attempt> StartAsync(Session session, IReadOnlyList<string> arguments, string prompt, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(session.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = session.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach ((string name, string value) in session.Environment)
        {
            start.Environment[name] = value;
        }

        string? directory = Path.GetDirectoryName(session.TranscriptPath);
        if (directory is not null)
        {
            OwnerOnly.EnsureDirectory(directory);
        }

        FileStream transcript = OwnerOnly.Open(session.TranscriptPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"The claude executable '{session.Executable}' could not be started.");
        }
        catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or IOException)
        {
            await transcript.DisposeAsync();
            throw new InvalidOperationException($"The claude executable '{session.Executable}' could not be started: {failure.Message}", failure);
        }

        var attempt = new Attempt(process);
        attempt.Ended = attempt.ReadAsync(transcript);
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // The process ended before it read its prompt; its exit and its error output say why.
        }
        catch
        {
            // Nothing will hold a process whose start failed here, so it does not outlive the failure.
            attempt.Kill();
            throw;
        }
        finally
        {
            attempt.CloseInput();
        }

        return attempt;
    }

    private Session RequireLive(SessionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (gate)
        {
            if (sessions.TryGetValue(handle.Id, out Session? session))
            {
                return session;
            }

            throw stopped.Contains(handle.Id)
                ? new InvalidOperationException($"Session {handle.Id} has been stopped.")
                : new InvalidOperationException($"The claude-code executor holds no session {handle.Id}.");
        }
    }

    private void RefuseOnceClosed()
    {
        lock (gate)
        {
            if (closed)
            {
                throw Closed();
            }
        }
    }

    private static InvalidOperationException Closed() =>
        new("The daemon is stopping, so the claude-code executor starts no claude process.");

    private static string NoResult(Attempt attempt)
    {
        string exit = attempt.ExitCode is { } code ? $"with code {code}" : "with no exit code";
        return attempt.LastErrorLine is { } line
            ? $"The claude process exited {exit} without a result; the last line of its error output was: {Quoted(line)}"
            : $"The claude process exited {exit} without a result and printed no error output.";
    }

    private static string Quoted(string text) => text.Length <= QuotedLength ? text : text[..QuotedLength];

    private static string Dollars(decimal amount) => string.Create(CultureInfo.InvariantCulture, $"${amount:0.00##}");

    /// <summary>One session: what every one of its processes is started with, and what its collects have taken so far.</summary>
    private sealed class Session(
        SessionHandle handle,
        string executable,
        string workingDirectory,
        string persona,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        decimal ceilingUsd,
        string transcriptPath)
    {
        public SessionHandle Handle { get; } = handle;

        public string Executable { get; } = executable;

        public string WorkingDirectory { get; } = workingDirectory;

        /// <summary>The persona file's content, written at the spawn and again before every resume.</summary>
        public string Persona { get; } = persona;

        public IReadOnlyList<string> Arguments { get; } = arguments;

        public IReadOnlyDictionary<string, string> Environment { get; } = environment;

        public decimal CeilingUsd { get; } = ceilingUsd;

        public string TranscriptPath { get; } = transcriptPath;

        /// <summary>The cost of every attempt a collect returned.</summary>
        public decimal CollectedCostUsd { get; set; }

        /// <summary>The session's total cost as the last collected result reported it.</summary>
        public decimal ReportedTotalCostUsd { get; set; }

        /// <summary>The latest process, collected or not.</summary>
        public Attempt? Current { get; set; }
    }

    /// <summary>One claude process of a session, and what it printed.</summary>
    private sealed class Attempt(Process process)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TaskCompletionSource inputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClaudeStream Stream { get; } = new();

        /// <summary>Completes once the process has exited and its output has been read.</summary>
        public Task Ended { get; set; } = Task.CompletedTask;

        public CollectedAnswer? Collected { get; set; }

        public int? ExitCode { get; private set; }

        public string? LastErrorLine { get; private set; }

        /// <summary>How long the process ran, up to its exit or up to now while it runs.</summary>
        public TimeSpan Elapsed => clock.Elapsed;

        /// <summary>Closes the process's input after its prompt; the process is disposed only once this has run.</summary>
        public void CloseInput()
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception closed) when (closed is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Already closed by the process ending, or left mid-write by a cancelled write; either
                // way the failure that brought it here is the one reported.
            }
            finally
            {
                inputClosed.TrySetResult();
            }
        }

        public void Kill()
        {
            if (Ended.IsCompleted)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception gone) when (gone is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
            {
                // The process ended on its own between the check and the kill.
            }
        }

        public async Task ReadAsync(FileStream transcript)
        {
            try
            {
                await using (transcript)
                {
                    Task output = ReadOutputAsync(transcript);
                    Task errors = ReadErrorsAsync();
                    await process.WaitForExitAsync(CancellationToken.None);
                    clock.Stop();
                    ExitCode = process.ExitCode;

                    // A process the session started may hold the output open after the session's own
                    // process exits; the read gives up on it rather than wait for it.
                    try
                    {
                        await Task.WhenAll(output, errors).WaitAsync(DrainGrace);
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
            finally
            {
                await inputClosed.Task;
                process.Dispose();
            }
        }

        private async Task ReadOutputAsync(FileStream transcript)
        {
            byte[] newline = [(byte)'\n'];
            try
            {
                while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    await transcript.WriteAsync(Utf8.GetBytes(line));
                    await transcript.WriteAsync(newline);
                    await transcript.FlushAsync();
                    Stream.Read(line);
                }
            }
            catch (ObjectDisposedException)
            {
                // The read gave up on output held open past the process's exit and closed the transcript.
            }
        }

        private async Task ReadErrorsAsync()
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    LastErrorLine = line.Trim();
                }
            }
        }
    }
}
