namespace Coanda.Daemon.Triggers;

/// <summary>
/// The entries the trigger loop read on a parcel while this node's own claim on it was not yet held, which it
/// handles again once the claim is held or lost: a start that waits on a push and a fetch is not dropped for
/// them. One document per parcel in the parcel's project, so it survives a restart; it is never exported.
/// </summary>
public sealed class HeldBackEntries
{
    /// <summary>The parcel's id: every entry held back on a parcel waits in its one document.</summary>
    public Guid Id { get; set; }

    /// <summary>The global sequences of the entries held back, in the order the loop read them.</summary>
    public List<long> Sequences { get; set; } = [];
}
