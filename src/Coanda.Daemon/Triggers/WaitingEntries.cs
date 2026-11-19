namespace Coanda.Daemon.Triggers;

/// <summary>
/// The entries of a parcel into a stage its type binds a flow to whose start waits until every parcel blocking the
/// parcel at the entry is closed. One document per parcel in the parcel's project, so a wait survives a restart; the
/// trigger loop handles an entry again each time one of its blockers closes, and the entry leaves the document once
/// its flow has started or the loop has decided nothing starts for it. Each entry is named by its version in the
/// parcel's stream rather than by a global sequence, so a record export carries the document as it is stored. It is
/// never written to the ledger: a wait is a judgement made from this node's view, which no other node reads.
/// </summary>
public sealed class WaitingEntries
{
    /// <summary>The parcel's id: every entry of a parcel that waits waits in its one document.</summary>
    public Guid Id { get; set; }

    /// <summary>The entries that wait, in the order the loop first found them blocked.</summary>
    public List<WaitingEntry> Entries { get; set; } = [];
}

/// <summary>
/// An entry that waits: the version of the parcel's stream its entry event holds, and every parcel the parcel listed
/// as blocking it as it stood at the entry, whether or not each was still open when the entry began to wait.
/// </summary>
public sealed record WaitingEntry(long Version, IReadOnlyList<Guid> Blockers);
