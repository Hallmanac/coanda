using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flows;

namespace Coanda.Daemon.Executors;

/// <summary>
/// An executor that answers from a script of canned entries keyed by agent name, so flows and
/// judgments run without a vendor or a network. The nth answer collected for an agent, counting
/// answers collected after a resume, is that agent's nth entry; collecting a session again before
/// it is resumed returns the same answer without advancing the count. It records every spawn,
/// resume, send, and stop for a test to read back, and enforces no caps.
/// </summary>
public sealed class ScriptedExecutor(IReadOnlyDictionary<string, IReadOnlyList<ScriptEntry>> script) : IAgentExecutor
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Session> sessions = [];
    private readonly Dictionary<string, int> collected = new(StringComparer.Ordinal);
    private readonly List<ScriptedSpawn> spawns = [];
    private readonly List<ScriptedMessage> resumes = [];
    private readonly List<ScriptedMessage> sends = [];
    private readonly List<SessionHandle> stops = [];

    public IReadOnlyList<ScriptedSpawn> Spawns => Snapshot(spawns);

    public IReadOnlyList<ScriptedMessage> Resumes => Snapshot(resumes);

    public IReadOnlyList<ScriptedMessage> Sends => Snapshot(sends);

    public IReadOnlyList<SessionHandle> Stops => Snapshot(stops);

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
        cancellationToken.ThrowIfCancellationRequested();

        var handle = new SessionHandle(Ids.New(), agent.Name);
        lock (gate)
        {
            sessions.Add(handle.Id, new Session(handle));
            spawns.Add(new ScriptedSpawn(handle, agent.Name, context, workingDirectory, caps, answerSchema, credentialProfile, access));
        }

        return ValueTask.FromResult(handle);
    }

    public ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            Session session = RequireLive(handle);
            string agent = session.Handle.Agent;
            if (session.Current is null)
            {
                int ordinal = collected.GetValueOrDefault(agent) + 1;
                IReadOnlyList<ScriptEntry> entries = script.GetValueOrDefault(agent) ?? [];
                if (ordinal > entries.Count)
                {
                    throw new InvalidOperationException(
                        $"The script holds no entry {ordinal} for agent '{agent}'; it holds {entries.Count}.");
                }

                ScriptEntry entry = entries[ordinal - 1];
                session.Current = new CollectedAnswer(entry.Answer, entry.TranscriptPath, entry.Usage);
                collected[agent] = ordinal;
            }

            return ValueTask.FromResult(session.Current);
        }
    }

    public ValueTask ResumeAsync(SessionHandle handle, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            Session session = RequireLive(handle);
            if (session.Current is { } answered)
            {
                session.Earlier = Judgment.Sum(session.Earlier, answered.Usage);
            }

            session.Current = null;
            session.Attempt++;
            resumes.Add(new ScriptedMessage(session.Handle, message));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync(SessionHandle handle, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            sends.Add(new ScriptedMessage(RequireLive(handle).Handle, message));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Reports no usage: a scripted attempt costs nothing until its entry is collected.</summary>
    public ValueTask<StopReport?> StopAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            Session session = RequireLive(handle);
            session.Stopped = true;
            stops.Add(session.Handle);
        }

        return ValueTask.FromResult<StopReport?>(null);
    }

    public ValueTask<IReadOnlyList<SessionHandle>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<SessionHandle> live = [.. sessions.Values.Where(s => !s.Stopped).Select(s => s.Handle)];
            return ValueTask.FromResult(live);
        }
    }

    public ValueTask<ReattachedSession?> ReattachAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ReattachedSession? held = sessions.TryGetValue(handle.Id, out Session? session) && !session.Stopped
                ? new ReattachedSession(session.Handle, session.Attempt, session.Earlier)
                : null;
            return ValueTask.FromResult(held);
        }
    }

    private Session RequireLive(SessionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!sessions.TryGetValue(handle.Id, out Session? session))
        {
            throw new InvalidOperationException($"The scripted executor holds no session {handle.Id}.");
        }

        return session.Stopped
            ? throw new InvalidOperationException($"Session {handle.Id} has been stopped.")
            : session;
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> records)
    {
        lock (gate)
        {
            return [.. records];
        }
    }

    private sealed class Session(SessionHandle handle)
    {
        public SessionHandle Handle { get; } = handle;

        /// <summary>The answer collected since the last spawn or resume, or null until one is.</summary>
        public CollectedAnswer? Current { get; set; }

        /// <summary>The place of the latest attempt in the session: one for the spawn, one more for every resume.</summary>
        public int Attempt { get; set; } = 1;

        /// <summary>The usage of every answer collected before the latest attempt.</summary>
        public Measurement Earlier { get; set; } = ReattachedSession.Nothing;

        public bool Stopped { get; set; }
    }
}
