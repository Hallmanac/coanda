using Coanda.Core.Flows;

namespace Coanda.Daemon.Executors;

/// <summary>
/// A session a reattach found still held, and where it stands: its handle, the place of its latest
/// attempt in it (one for the spawn, and one more for every resume since), and the usage a collect
/// returned for every attempt before that one, which a judgment picking the session up again counts
/// as its own. The attempts before the latest are none for a session still on its spawn, so its
/// earlier usage is <see cref="Nothing"/>.
/// </summary>
public sealed record ReattachedSession(SessionHandle Handle, int Attempt, Measurement Earlier) : Reattachment(Handle)
{
    /// <summary>The usage of no attempt at all.</summary>
    public static readonly Measurement Nothing = new(0, 0m, 0, TimeSpan.Zero);

    /// <summary>A session still on the attempt its spawn started, so nothing came before it.</summary>
    public static ReattachedSession OnSpawn(SessionHandle handle) => new(handle, 1, Nothing);
}
