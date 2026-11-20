using Coanda.Core;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Names one agent session an executor holds. It is plain data, so the runner can keep it across a
/// daemon restart and hand it back to <see cref="IAgentExecutor.ReattachAsync"/>. A handle made by
/// <see cref="ForInbox"/> instead names the inbox of a live session the executor did not start: it
/// is send-only, and an executor reads from, continues, ends, or reattaches no session through it.
/// </summary>
public sealed record SessionHandle(Guid Id, string Agent)
{
    /// <summary>The inbox this handle names, or null for a session the executor spawned.</summary>
    public SessionInbox? Inbox { get; private init; }

    /// <summary>
    /// A send-only handle naming <paramref name="inbox"/>, the inbox of a live session no executor
    /// spawned, under <paramref name="name"/>, which stands where a spawned session's agent name does.
    /// Sending to it needs nothing an executor recorded, so any executor instance delivers to it.
    /// </summary>
    public static SessionHandle ForInbox(string name, SessionInbox inbox)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(inbox);
        return new SessionHandle(Ids.New(), name) { Inbox = inbox };
    }

    /// <summary>
    /// Refuses a handle that names an inbox for anything but a send: <paramref name="executor"/>
    /// names the executor and <paramref name="action"/> what it was asked to do with the session.
    /// </summary>
    public void RequireSpawned(string executor, string action)
    {
        if (Inbox is { } inbox)
        {
            throw new InvalidOperationException(
                $"The {executor} executor cannot {action} session {Id}: its handle names the inbox at {inbox.Address} of a session the executor did not start, and the executor only sends to such a handle.");
        }
    }
}
