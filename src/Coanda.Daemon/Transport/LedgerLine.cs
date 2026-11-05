namespace Coanda.Daemon.Transport;

/// <summary>
/// The store's record of one signed line of a project's ledger, kept in the project's tenant: the line
/// exactly as written, and beside it what a reader looks a line up by. It is keyed by the store event the
/// line carries, so an event is on at most one line, under one origin. The export writes one for every
/// line this node authors, and the import (<see cref="LedgerImport"/>) writes the same record for a line from
/// another origin, with the line as it arrived, in the same save as the event, keyed by the id this node's
/// store gave it; either way the ledger's files are written from these records, never from the events again,
/// so a line's bytes never change once it is recorded. The line's signer key is the <c>signer</c> member of
/// <see cref="Line"/>.
/// </summary>
public sealed class LedgerLine
{
    /// <summary>The id the store gave the event the line carries.</summary>
    public Guid Id { get; set; }

    /// <summary>The node that authored and signed the line.</summary>
    public Guid Origin { get; set; }

    /// <summary>The origin's sequence in the project, from 1.</summary>
    public long Sequence { get; set; }

    /// <summary>The hash of the origin's line before this one in the project, or null for its first.</summary>
    public string? PreviousHash { get; set; }

    /// <summary>The names of the repos that carry the event, as the line was signed with them, or null for project-wide.</summary>
    public IReadOnlyList<string>? Tag { get; set; }

    /// <summary>The line's signature in standard base64 with padding.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>The line's hash (<see cref="SignedLine.HashOf"/>).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// The month file the line is written in, as <c>yyyy-MM</c>: the UTC month of the event's recorded
    /// time, or the segment of the origin's line before it when that is later, so reading the segments in
    /// name order gives the chain in sequence order even across a clock stepped back over a month's end.
    /// </summary>
    public string Segment { get; set; } = string.Empty;

    /// <summary>The line as written, without its line ending.</summary>
    public string Line { get; set; } = string.Empty;

    /// <summary>
    /// The event's sequence in this node's store, which is where the next export of its origin reads from; 0 for a
    /// line of another origin, which the export never reads from, since the store gives the sequence only as the
    /// event and this record are saved together.
    /// </summary>
    public long StoreSequence { get; set; }
}
