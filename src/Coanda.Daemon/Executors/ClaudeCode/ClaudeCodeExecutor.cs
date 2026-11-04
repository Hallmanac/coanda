using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flows;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// Runs each judgment as a headless Claude Code session: a <c>claude</c> process in print mode with
/// stream-json output, started in the flow's work directory, given its prompt on standard input and
/// nothing after it. A resume starts another process on the same session id with the same
/// arguments, working directory, and environment. Every process writes its stream output straight
/// into the session's transcript in the flow's transcript directory, and its error output into a
/// file of the session's own; nothing a session prints passes through the daemon. Collecting waits
/// for the process to end and reads its result from the transcript. Every process carries the
/// session's tool guard in its settings document, so its tools cannot reach the node home or a
/// Claude Code config directory outside the session's own work and conversation directories.
/// </summary>
/// <remarks>
/// Each process carries a spend cap, <c>--max-budget-usd</c>: the spawn's cost ceiling for the
/// spawn, and that ceiling less the cost already collected for the session for a resume, because
/// the CLI does not count an earlier process's spend against a resumed process's cap. The CLI
/// checks its cap between turns, so a single turn can overshoot it. The adapter sets no time limit;
/// the daemon's wall-clock limit is authoritative. Stopping a session kills its process tree; the
/// adapter never kills one when the daemon stops or the executor is dropped. Each process is started
/// detached (<see cref="DetachedProcess"/>), so it outlives the daemon, and each session keeps a
/// record under the node home's <see cref="NodeHome.SessionsPath"/>, rewritten as it changes: what
/// its processes are started with, what its collects have taken, and its latest process's id, start
/// time, and where its output begins in the transcript. A new daemon's executor, holding nothing in
/// memory, finds a session again from its record: one whose process still runs, or exited after
/// printing its result, is held, and one whose process exited without a result, or whose recorded
/// process id now names another process, is not, and its record is let go. A stopped session's
/// record is removed and only its id is kept, so a second stop of it still succeeds.
/// </remarks>
public sealed class ClaudeCodeExecutor : IAgentExecutor
{
    /// <summary>The executable started when the node configuration names none: <c>claude</c>, looked up on the PATH.</summary>
    public const string DefaultExecutable = "claude";

    /// <summary>How much of the process's error output, and of an error result's text, a failure quotes.</summary>
    public const int QuotedLength = 500;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions RecordFormat = new(JsonSerializerDefaults.Web);

    private readonly NodeHome home;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly string? coaDirectory;
    private readonly string coaExecutable;
    private readonly IReadOnlyList<string> profileDirectories;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Session> sessions = [];
    private readonly HashSet<Guid> stopped = [];

    // What the attempts before a retry returned, for each session let go on that retry, until a reattach takes it.
    private readonly Dictionary<Guid, Measurement> lost = [];

    /// <param name="executable">The configured path of the claude executable, or a bare name looked up on the PATH.</param>
    /// <param name="home">The node home, which says where each flow's transcripts go and where sessions are recorded.</param>
    /// <param name="environment">The environment a session's processes start from, before the adapter removes and sets what it does.</param>
    /// <param name="coaDirectory">
    /// The directory that holds the <c>coa</c> executable, placed first on every session process's
    /// PATH so a session can run <c>coa</c> by name; null leaves the PATH as inherited. The <c>coa</c>
    /// executable in it is also the session's tool guard; when it is null, the guard is the <c>coa</c>
    /// beside this assembly.
    /// </param>
    /// <param name="profiles">
    /// The credential profiles the node's configuration holds, whose config directories every
    /// session's tool guard protects, whichever profile the session itself runs under.
    /// </param>
    public ClaudeCodeExecutor(
        string executable,
        NodeHome home,
        IReadOnlyDictionary<string, string> environment,
        string? coaDirectory = null,
        IReadOnlyList<CredentialProfile>? profiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(environment);
        Executable = executable;
        this.home = home;
        this.environment = environment;
        this.coaDirectory = string.IsNullOrWhiteSpace(coaDirectory) ? null : coaDirectory;
        coaExecutable = CoaExecutable(this.coaDirectory ?? AppContext.BaseDirectory);
        profileDirectories = [.. (profiles ?? []).Select(p => p.ConfigDirectory).OfType<string>()];
    }

    public string Executable { get; }

    /// <summary>The absolute path of the <c>coa</c> executable in <paramref name="directory"/>, which runs the tool guard.</summary>
    public static string CoaExecutable(string directory) =>
        Path.Combine(Path.GetFullPath(directory), OperatingSystem.IsWindows() ? "coa.exe" : "coa");

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

    public ValueTask<SessionHandle> SpawnAsync(
        AgentDefinition agent,
        string context,
        string workingDirectory,
        SpawnCaps caps,
        string answerSchema,
        CredentialProfile? credentialProfile,
        SessionAccess? access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(caps);
        cancellationToken.ThrowIfCancellationRequested();
        ClaudeCodeInvocation.Require(agent);
        string prompt = ClaudeCodeInvocation.Prompt(context, answerSchema);
        IReadOnlyDictionary<string, string> processEnvironment = ClaudeCodeInvocation.Environment(environment, credentialProfile, access, coaDirectory);
        string executable = ClaudeExecutable.Resolve(Executable, processEnvironment);
        if (caps.CostCeilingUsd <= 0m)
        {
            throw new InvalidOperationException(
                $"The spawn's cost ceiling of {Dollars(caps.CostCeilingUsd)} leaves nothing to spend, so no claude process was started.");
        }

        string persona = ClaudeCodeInvocation.Persona(agent.Persona);
        SessionFiles.Write(workingDirectory, SessionFiles.PersonaFile, persona);
        var handle = new SessionHandle(Ids.New(), agent.Name);
        var session = new Session(
            handle,
            executable,
            workingDirectory,
            persona,
            ClaudeCodeInvocation.SessionArguments(
                agent,
                credentialProfile,
                ClaudeCodeInvocation.Guard(home, workingDirectory, credentialProfile, environment, profileDirectories),
                coaExecutable),
            credentialProfile,
            processEnvironment,
            caps.CostCeilingUsd,
            Path.Combine(home.TranscriptDirectoryOf(workingDirectory), $"{handle.Id}.jsonl"));

        cancellationToken.ThrowIfCancellationRequested();
        Attempt first;
        try
        {
            first = Start(session, 1, ClaudeCodeInvocation.SpawnArguments(handle.Id, session.Arguments, caps.CostCeilingUsd), prompt);
        }
        catch
        {
            Forget(handle.Id);
            throw;
        }

        session.Current = first;
        try
        {
            Save(session);
        }
        catch
        {
            // A process no record names could never be found again, so it does not outlive the failure.
            first.Process.KillTree();
            first.Process.Dispose();
            Forget(handle.Id);
            throw;
        }

        lock (gate)
        {
            sessions.Add(handle.Id, session);
        }

        return ValueTask.FromResult(handle);
    }

    public async ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        Session session = RequireLive(handle);
        Attempt attempt = session.Current ?? throw new InvalidOperationException($"Session {handle.Id} has no process to collect.");
        if (attempt.Collected is { } already)
        {
            return already;
        }

        await attempt.Process.WaitForExitAsync(cancellationToken);
        ClaudeStream stream = ReadStream(session, attempt);
        CollectedAnswer answer;
        lock (gate)
        {
            if (attempt.Collected is { } collected)
            {
                return collected;
            }

            ClaudeResult result = stream.Result ?? throw new InvalidOperationException(NoResult(attempt));
            if (result.IsError)
            {
                // Not collected: the attempt stays in progress, so the stop the judgment makes after
                // this failure reports its usage.
                string subtype = result.Subtype is null or "success" ? string.Empty : $" ({result.Subtype})";
                throw new InvalidOperationException(
                    $"The claude CLI ended the session's process with an error result{subtype}: {Quoted(result.ErrorText)}");
            }

            Measurement usage = result.Usage(attempt.PreviousTotalCostUsd);
            if (!attempt.Counted)
            {
                // An attempt a daemon before this one collected already counted its cost.
                session.CollectedCostUsd += usage.CostUsd;
                session.ReportedTotalCostUsd = result.TotalCostUsd ?? attempt.PreviousTotalCostUsd + usage.CostUsd;
                attempt.Counted = true;
            }

            answer = new CollectedAnswer(ClaudeAnswer.Trailing(result.Text), session.TranscriptPath, usage);
            attempt.Collected = answer;
        }

        Save(session);
        return answer;
    }

    public ValueTask ResumeAsync(SessionHandle handle, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session session = RequireLive(handle);
        decimal remaining;
        Attempt previous;
        Measurement previousUsage;
        lock (gate)
        {
            if (session.Current is not { Collected: { } previousAnswer } collected)
            {
                throw new InvalidOperationException($"Session {handle.Id} still has a process whose answer was not collected, so it cannot be resumed.");
            }

            previous = collected;
            previousUsage = previousAnswer.Usage;
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
        Attempt next = Start(session, previous.Ordinal + 1, ClaudeCodeInvocation.ResumeArguments(handle.Id, session.Arguments, remaining), message);
        bool live;
        lock (gate)
        {
            live = sessions.ContainsKey(handle.Id);
            if (live)
            {
                // What the previous attempt's collect returned is now an earlier attempt's usage.
                session.EarlierUsage = Judgment.Sum(session.EarlierUsage, previousUsage);
                session.Current = next;
            }
        }

        if (live)
        {
            try
            {
                Save(session);
                previous.Process.Dispose();
                return ValueTask.CompletedTask;
            }
            catch
            {
                next.Process.KillTree();
                throw;
            }
        }

        // The session was stopped while this process started; nothing would hold it after the stop.
        next.Process.KillTree();
        next.Process.Dispose();
        throw new InvalidOperationException($"Session {handle.Id} has been stopped.");
    }

    /// <summary>Refuses every call: sending into a live session is not supported by this executor yet.</summary>
    public ValueTask SendAsync(SessionHandle handle, string message, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The claude-code executor does not support sending into a live session yet.");

    /// <summary>
    /// Kills the session's process together with every process it started, removes its record, and
    /// reports its attempt in progress: the result's usage when the process printed one that no
    /// collect returned (an error result), otherwise the tokens and count of the assistant messages
    /// it printed, with no cost, and its duration up to the stop. A process killed while it ran
    /// reports that much even when it had printed no assistant message, since its turn in progress
    /// may already have been billed; one that had already ended with nothing printed reports
    /// nothing. Stopping a session whose process has already ended, or one already stopped, succeeds.
    /// A session this executor has not met yet is found from its record first.
    /// </summary>
    public async ValueTask<StopReport?> StopAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (gate)
        {
            if (stopped.Contains(handle.Id))
            {
                return null;
            }
        }

        Session session = Find(handle.Id) ?? throw new InvalidOperationException($"The claude-code executor holds no session {handle.Id}.");
        Attempt? attempt;
        lock (gate)
        {
            if (stopped.Contains(handle.Id))
            {
                return null;
            }

            if (!sessions.Remove(handle.Id))
            {
                throw new InvalidOperationException($"The claude-code executor holds no session {handle.Id}.");
            }

            stopped.Add(handle.Id);
            attempt = session.Current;
        }

        try
        {
            if (attempt is null || attempt.Collected is not null || attempt.Counted)
            {
                return null;
            }

            // A process still running when it is killed may have been billed for a turn it had not printed yet.
            bool killedRunning = !attempt.Process.HasExited;
            attempt.Process.KillTree();
            await attempt.Process.WaitForExitAsync(cancellationToken);
            ClaudeStream stream = ReadStream(session, attempt);
            if (stream.Result is { } result)
            {
                return new StopReport(result.Usage(attempt.PreviousTotalCostUsd));
            }

            return stream.MessageCount == 0 && !killedRunning
                ? null
                : new StopReport(new Measurement(stream.MessageTokens, 0m, stream.MessageCount, attempt.Elapsed), CostReported: false);
        }
        finally
        {
            attempt?.Process.Dispose();
            Forget(handle.Id);
        }
    }

    /// <summary>
    /// The sessions this executor holds that have not been stopped: those it started or found, and
    /// every other session recorded under the node home that it finds held.
    /// </summary>
    public ValueTask<IReadOnlyList<SessionHandle>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(home.SessionsPath))
        {
            foreach (string record in Directory.EnumerateFiles(home.SessionsPath, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(record), out Guid id))
                {
                    _ = Find(id);
                }
            }
        }

        lock (gate)
        {
            IReadOnlyList<SessionHandle> live = [.. sessions.Values.Select(s => s.Handle)];
            return ValueTask.FromResult(live);
        }
    }

    /// <summary>
    /// Returns a session this executor holds and has not stopped, with the attempt its latest
    /// process is and the usage its earlier attempts' collects returned, finding it from its record
    /// when this executor has not met it, as after a daemon restart. A session its record showed on
    /// a retry, let go because that retry cannot answer, is lost with the usage the record held for
    /// the attempts before it; null for any other session that is not held.
    /// </summary>
    public ValueTask<Reattachment?> ReattachAsync(SessionHandle handle, SessionAccess? access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        if (Find(handle.Id) is not { } session)
        {
            lock (gate)
            {
                return lost.Remove(handle.Id, out Measurement? earlier)
                    ? ValueTask.FromResult<Reattachment?>(new LostSession(handle, earlier))
                    : ValueTask.FromResult<Reattachment?>(null);
            }
        }

        lock (gate)
        {
            if (access is not null)
            {
                session.Environment = ClaudeCodeInvocation.Environment(environment, session.Profile, access, coaDirectory);
            }

            return session.Current is { } attempt
                ? ValueTask.FromResult<Reattachment?>(new ReattachedSession(session.Handle, attempt.Ordinal, session.EarlierUsage, attempt.Process.HasExited))
                : ValueTask.FromResult<Reattachment?>(null);
        }
    }

    /// <summary>
    /// The live session with the id: the one in memory, or the one its record names when its process
    /// still runs, or exited after printing its result. A record whose process exited without a
    /// result, whose process id now names another process, or that cannot be read, is let go. Null
    /// for a stopped session or one that is not held.
    /// </summary>
    private Session? Find(Guid id)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(id, out Session? held))
            {
                return held;
            }

            if (stopped.Contains(id))
            {
                return null;
            }
        }

        Session? found = Load(id);
        lock (gate)
        {
            if (sessions.TryGetValue(id, out Session? raced))
            {
                found?.Current?.Process.Dispose();
                return raced;
            }

            if (found is null || stopped.Contains(id))
            {
                return null;
            }

            sessions.Add(id, found);
            return found;
        }
    }

    private Session? Load(Guid id)
    {
        string path = home.SessionRecordPath(id);
        SessionRecord? record;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            record = JsonSerializer.Deserialize<SessionRecord>(file, RecordFormat);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (record is not { Attempt: { } recorded } || record.Id != id || string.IsNullOrEmpty(recorded.Identity))
        {
            Forget(id);
            return null;
        }

        DetachedProcess? process = DetachedProcess.Find(recorded.ProcessId, recorded.Identity) switch
        {
            DetachedProcess.Found.Running running => running.Process,
            DetachedProcess.Found.Gone => DetachedProcess.Exited(recorded.ProcessId, recorded.Identity),
            _ => null,
        };
        var session = new Session(
            new SessionHandle(record.Id, record.Agent),
            record.Executable,
            record.WorkingDirectory,
            record.Persona,
            record.Arguments,
            record.Profile,
            ClaudeCodeInvocation.Environment(environment, record.Profile, access: null, coaDirectory),
            record.CeilingUsd,
            record.TranscriptPath)
        {
            CollectedCostUsd = record.CollectedCostUsd,
            ReportedTotalCostUsd = record.ReportedTotalCostUsd,
            EarlierUsage = record.EarlierUsage ?? ReattachedSession.Nothing,
        };

        if (process is not null)
        {
            session.Current = new Attempt(recorded.Ordinal, process, recorded.TranscriptOffset, recorded.ErrorPath, recorded.StartedAt, recorded.PreviousTotalCostUsd)
            {
                Counted = recorded.Collected,
            };
        }

        // A process that exited printed all it will; without a result there is nothing to collect.
        if (session.Current is not { } attempt || attempt.Process.HasExited && ReadStream(session, attempt).Result is null)
        {
            session.Current?.Process.Dispose();

            // A retry's earlier attempts were collected, and nothing recorded their usage at the daemon stop.
            if (recorded.Ordinal > 1)
            {
                lock (gate)
                {
                    lost[id] = session.EarlierUsage;
                }
            }

            Forget(id);
            return null;
        }

        return session;
    }

    /// <summary>
    /// Starts one claude process for the session: its prompt written to a file it reads as its
    /// standard input, its output appended to the session's transcript, and its error output to a
    /// file of the attempt's own.
    /// </summary>
    private Attempt Start(Session session, int ordinal, IReadOnlyList<string> arguments, string prompt)
    {
        OwnerOnly.EnsureDirectory(home.SessionsPath);
        string stem = Path.Combine(home.SessionsPath, $"{session.Handle.Id}.{ordinal.ToString(CultureInfo.InvariantCulture)}");
        string inputPath = stem + ".in";
        string errorPath = stem + ".err";
        using (FileStream written = OwnerOnly.Open(inputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            written.Write(Utf8.GetBytes(prompt));
        }

        string? directory = Path.GetDirectoryName(session.TranscriptPath);
        if (directory is not null)
        {
            OwnerOnly.EnsureDirectory(directory);
        }

        using FileStream transcript = OwnerOnly.Open(session.TranscriptPath, FileMode.Append, FileAccess.Write, Inherited(FileShare.ReadWrite | FileShare.Delete));
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, Inherited(FileShare.Read | FileShare.Delete));
        using FileStream errors = OwnerOnly.Open(errorPath, FileMode.Create, FileAccess.Write, Inherited(FileShare.ReadWrite | FileShare.Delete));
        long offset = transcript.Length;
        DetachedProcess process;
        try
        {
            process = DetachedProcess.Start(
                session.Executable, arguments, session.Environment, session.WorkingDirectory, input.SafeFileHandle, transcript.SafeFileHandle, errors.SafeFileHandle);
        }
        catch (Exception failure) when (failure is Win32Exception or IOException or InvalidOperationException)
        {
            throw new InvalidOperationException($"The claude executable '{session.Executable}' could not be started: {failure.Message}", failure);
        }

        return new Attempt(ordinal, process, offset, errorPath, DateTimeOffset.UtcNow, session.ReportedTotalCostUsd);
    }

    /// <summary>A handle a process is started with must be inheritable on Windows; elsewhere the spawn hands it over itself.</summary>
    private static FileShare Inherited(FileShare share) => OperatingSystem.IsWindows() ? share | FileShare.Inheritable : share;

    /// <summary>What the attempt's process printed to the transcript, from where its output began.</summary>
    private static ClaudeStream ReadStream(Session session, Attempt attempt)
    {
        var stream = new ClaudeStream();
        try
        {
            using var file = new FileStream(session.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Min(attempt.TranscriptOffset, file.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(file, Utf8);
            while (reader.ReadLine() is { } line)
            {
                stream.Read(line);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        return stream;
    }

    /// <summary>Saves the session's record, replacing the one before it whole.</summary>
    private void Save(Session session)
    {
        SessionRecord record;
        lock (gate)
        {
            record = session.ToRecord();
        }

        string path = home.SessionRecordPath(session.Handle.Id);
        string written = path + ".tmp";
        using (FileStream file = OwnerOnly.Open(written, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, record, RecordFormat);
        }

        File.Move(written, path, overwrite: true);
    }

    /// <summary>Removes the session's record and the input and error files of its processes; its transcript stays.</summary>
    private void Forget(Guid id)
    {
        if (!Directory.Exists(home.SessionsPath))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(home.SessionsPath, $"{id}.*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
            {
                // A file a process still holds open on Windows; it is only left behind.
            }
        }
    }

    private Session RequireLive(SessionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (Find(handle.Id) is { } session)
        {
            return session;
        }

        lock (gate)
        {
            throw stopped.Contains(handle.Id)
                ? new InvalidOperationException($"Session {handle.Id} has been stopped.")
                : new InvalidOperationException($"The claude-code executor holds no session {handle.Id}.");
        }
    }

    private static string NoResult(Attempt attempt)
    {
        string exit = attempt.Process.ExitCode is { } code ? $"with code {code}" : "with no exit code";
        return attempt.LastErrorLine() is { } line
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
        CredentialProfile? profile,
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

        /// <summary>The credential profile of the spawn, which a later daemon rebuilds the environment from.</summary>
        public CredentialProfile? Profile { get; } = profile;

        /// <summary>
        /// The environment the session's next process starts with. A session found from its record
        /// has none of the access its spawn carried until a reattach hands it fresh access.
        /// </summary>
        public IReadOnlyDictionary<string, string> Environment { get; set; } = environment;

        public decimal CeilingUsd { get; } = ceilingUsd;

        public string TranscriptPath { get; } = transcriptPath;

        /// <summary>The cost of every attempt a collect returned.</summary>
        public decimal CollectedCostUsd { get; set; }

        /// <summary>The session's total cost as the last collected result reported it.</summary>
        public decimal ReportedTotalCostUsd { get; set; }

        /// <summary>The usage the collects of every attempt before the latest returned.</summary>
        public Measurement EarlierUsage { get; set; } = ReattachedSession.Nothing;

        /// <summary>The latest process, collected or not.</summary>
        public Attempt? Current { get; set; }

        public SessionRecord ToRecord() => new(
            Handle.Id,
            Handle.Agent,
            Executable,
            WorkingDirectory,
            Persona,
            Arguments,
            Profile,
            CeilingUsd,
            TranscriptPath,
            CollectedCostUsd,
            ReportedTotalCostUsd,
            EarlierUsage,
            Current is { } attempt
                ? new AttemptRecord(
                    attempt.Ordinal,
                    attempt.Process.Id,
                    attempt.Process.Identity,
                    attempt.TranscriptOffset,
                    attempt.ErrorPath,
                    attempt.StartedAt,
                    attempt.PreviousTotalCostUsd,
                    attempt.Counted)
                : null);
    }

    /// <summary>One claude process of a session: where its output begins in the transcript, and whether a collect has taken it.</summary>
    private sealed class Attempt(int ordinal, DetachedProcess process, long transcriptOffset, string errorPath, DateTimeOffset startedAt, decimal previousTotalCostUsd)
    {
        private DateTimeOffset? endedAt;

        /// <summary>The attempt's place in the session, from one for the spawn.</summary>
        public int Ordinal { get; } = ordinal;

        public DetachedProcess Process { get; } = process;

        public long TranscriptOffset { get; } = transcriptOffset;

        public string ErrorPath { get; } = errorPath;

        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>The session's reported total cost when this attempt started, which its own cost is measured from.</summary>
        public decimal PreviousTotalCostUsd { get; } = previousTotalCostUsd;

        /// <summary>What a collect in this daemon returned, or null until one has.</summary>
        public CollectedAnswer? Collected { get; set; }

        /// <summary>Whether a collect, in this daemon or one before it, has counted the attempt's cost against the session.</summary>
        public bool Counted { get; set; }

        /// <summary>How long the process ran, up to when its exit was first seen, or up to now while it runs.</summary>
        public TimeSpan Elapsed
        {
            get
            {
                if (endedAt is null && Process.HasExited)
                {
                    endedAt = DateTimeOffset.UtcNow;
                }

                return (endedAt ?? DateTimeOffset.UtcNow) - StartedAt;
            }
        }

        /// <summary>The last line of the process's error output that is not blank, trimmed, or null when there is none.</summary>
        public string? LastErrorLine()
        {
            try
            {
                using var file = new FileStream(ErrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file, Utf8);
                string? last = null;
                while (reader.ReadLine() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        last = line.Trim();
                    }
                }

                return last;
            }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>A session's record under the node home: everything a later daemon needs to collect, resume, or stop it.</summary>
    private sealed record SessionRecord(
        Guid Id,
        string Agent,
        string Executable,
        string WorkingDirectory,
        string Persona,
        IReadOnlyList<string> Arguments,
        CredentialProfile? Profile,
        decimal CeilingUsd,
        string TranscriptPath,
        decimal CollectedCostUsd,
        decimal ReportedTotalCostUsd,
        Measurement? EarlierUsage,
        AttemptRecord? Attempt);

    /// <summary>The record of a session's latest process.</summary>
    private sealed record AttemptRecord(
        int Ordinal,
        int ProcessId,
        string Identity,
        long TranscriptOffset,
        string ErrorPath,
        DateTimeOffset StartedAt,
        decimal PreviousTotalCostUsd,
        bool Collected);
}
