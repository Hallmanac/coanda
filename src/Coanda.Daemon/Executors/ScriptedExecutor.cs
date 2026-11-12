using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Executors;

/// <summary>
/// An executor that answers from a script of canned entries keyed by agent name, so flow runs and
/// agent steps run without a vendor or a network. The nth answer collected for an agent, counting
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
    private readonly List<ScriptedReattach> reattaches = [];
    private readonly Dictionary<Guid, Measurement> lost = [];

    public IReadOnlyList<ScriptedSpawn> Spawns => Snapshot(spawns);

    public IReadOnlyList<ScriptedMessage> Resumes => Snapshot(resumes);

    public IReadOnlyList<ScriptedMessage> Sends => Snapshot(sends);

    public IReadOnlyList<SessionHandle> Stops => Snapshot(stops);

    /// <summary>Every reattach of a session the executor held, with the access it was given.</summary>
    public IReadOnlyList<ScriptedReattach> Reattaches => Snapshot(reattaches);

    public ValueTask<SessionHandle> SpawnAsync(
        AgentDefinition agent,
        string context,
        string workingDirectory,
        SpawnCaps caps,
        AnswerSchema answerSchema,
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
            spawns.Add(new ScriptedSpawn(handle, agent.Name, context, workingDirectory, caps, answerSchema.Name, credentialProfile, access));
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
                session.Earlier = AgentStep.Sum(session.Earlier, answered.Usage);
            }

            session.Current = null;
            session.Attempt++;
            session.Finished = false;
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

    /// <summary>
    /// The session when it is held and not stopped; a session <see cref="Lose"/> let go on its retry
    /// is lost once, with what its first attempt's collect returned; null otherwise.
    /// </summary>
    public ValueTask<Reattachment?> ReattachAsync(SessionHandle handle, SessionAccess? access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (lost.Remove(handle.Id, out Measurement? earlier))
            {
                return ValueTask.FromResult<Reattachment?>(new LostSession(handle, earlier));
            }

            ReattachedSession? held = sessions.TryGetValue(handle.Id, out Session? session) && !session.Stopped
                ? new ReattachedSession(session.Handle, session.Attempt, session.Earlier, session.Finished)
                : null;
            if (held is not null)
            {
                reattaches.Add(new ScriptedReattach(held.Handle, access));
            }

            return ValueTask.FromResult<Reattachment?>(held);
        }
    }

    /// <summary>
    /// Marks the session's latest attempt as having ended with its answer while no daemon ran, so a
    /// reattach reports it finished; a resume starts an attempt that has not.
    /// </summary>
    public void Finish(SessionHandle handle)
    {
        lock (gate)
        {
            RequireLive(handle).Finished = true;
        }
    }

    /// <summary>
    /// Lets the session go as though its process ended without an answer while no daemon ran; a
    /// session on its retry is then reattached once as lost, with its earlier attempts' usage.
    /// </summary>
    public void Lose(SessionHandle handle)
    {
        lock (gate)
        {
            Session session = RequireLive(handle);
            sessions.Remove(session.Handle.Id);
            if (session.Attempt > 1)
            {
                lost[session.Handle.Id] = session.Earlier;
            }
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

        /// <summary>Whether the latest attempt ended with its answer while no daemon ran, as <see cref="Finish"/> says.</summary>
        public bool Finished { get; set; }

        public bool Stopped { get; set; }
    }
}
