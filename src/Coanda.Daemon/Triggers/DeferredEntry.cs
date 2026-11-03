namespace Coanda.Daemon.Triggers;

/// <summary>
/// A bound entry the trigger loop could not start yet because the parcel already had a live flight,
/// most often the flight whose own step moved it. One document per parcel in the parcel's project,
/// so it survives a restart; the loop starts the binding when that flight's release reaches it, if
/// the parcel still sits in <see cref="State"/>, and forgets the entry either way.
/// </summary>
public sealed class DeferredEntry
{
    /// <summary>The parcel's id: a parcel holds at most one deferred entry, its latest.</summary>
    public Guid Id { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    /// <summary>The id of the event that entered the state, which names the binding's principal.</summary>
    public Guid EntryId { get; set; }
}
