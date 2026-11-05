namespace Coanda.Daemon.Transport;

/// <summary>
/// Verifies a run of lines from one origin in one project: each line parses, is signed by a key the
/// reader accepts for the origin, names the run's origin and project, carries the expected sequence,
/// and names the hash of the line before it, so any prefix of an origin's chain verifies and a rewind
/// or a fork shows as the chain breaking (design section 6).
/// </summary>
public static class ChainVerifier
{
    /// <summary>
    /// Verifies <paramref name="lines"/>, each a line's bytes with no line ending, in order. Without a
    /// held position the run starts at the origin's first line. With one, the run may start at any
    /// sequence from 1 to the one after the held sequence; the line at the held sequence must hash to
    /// the held hash, and a run starting just after it must name the held hash as its first line's
    /// previous hash. A run that verifies in full but ends before the held sequence is a rewind. For a
    /// broken run the lines before the first bad one are reported verified, unless the bad line is the
    /// one at the held sequence, in which case none is: nothing before it is tied to what the reader holds.
    /// An empty run verifies, with no line.
    /// </summary>
    public static ChainVerification Verify(
        IReadOnlyList<byte[]> lines,
        Guid projectId,
        Guid origin,
        IReadOnlyCollection<string> acceptedSigners,
        HeldPosition? held = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(acceptedSigners);
        if (held is not null && (held.Sequence < 1 || !SignedLine.IsHash(held.Hash)))
        {
            throw new ArgumentException("A held position is a sequence from 1 and a line hash.", nameof(held));
        }

        SignedLine? last = null;
        for (int index = 0; index < lines.Count; index++)
        {
            long? expected = last is not null
                ? last.Content.Sequence + 1
                : held is null ? 1 : null;
            SignedLine? line = SignedLine.Parse(lines[index], out string? unparsable);
            (ChainFaultKind Kind, string Reason)? fault = line is null
                ? (ChainFaultKind.Unparsable, $"The line does not parse as a line: {unparsable}.")
                : FaultOf(line, last, projectId, origin, acceptedSigners, held);
            if (fault is { } found)
            {
                long? sequence = line?.Content.Sequence ?? expected;
                // The place the line stands at, which a repeat or a step back does not move.
                long? place = expected ?? sequence;
                bool atHeld = held is not null && place == held.Sequence;
                return new ChainVerification(
                    ChainOutcome.Broken,
                    atHeld ? 0 : index,
                    atHeld ? null : last?.Content.Sequence,
                    atHeld ? null : last?.Hash,
                    new ChainFault(sequence, index, found.Kind, found.Reason));
            }

            last = line;
        }

        ChainOutcome outcome = held is not null && last is not null && last.Content.Sequence < held.Sequence
            ? ChainOutcome.Rewind
            : ChainOutcome.Verified;
        return new ChainVerification(outcome, lines.Count, last?.Content.Sequence, last?.Hash, null);
    }

    private static (ChainFaultKind Kind, string Reason)? FaultOf(
        SignedLine line,
        SignedLine? last,
        Guid projectId,
        Guid origin,
        IReadOnlyCollection<string> acceptedSigners,
        HeldPosition? held)
    {
        LineContent content = line.Content;
        long sequence = content.Sequence;
        return (line, last, held) switch
        {
            _ when !line.SignatureVerifies => (ChainFaultKind.BadSignature, $"The signature is not {line.Signer}'s over the rest of the line."),
            _ when !acceptedSigners.Contains(line.Signer) => (ChainFaultKind.UnacceptedSigner, $"The line is signed by {line.Signer}, which is not a key accepted for origin {origin}."),
            _ when content.Origin != origin => (ChainFaultKind.WrongOrigin, $"The line is origin {content.Origin}'s, not {origin}'s."),
            _ when content.ProjectId != projectId => (ChainFaultKind.WrongProject, $"The line is project {content.ProjectId}'s, not {projectId}'s."),
            (_, null, null) when sequence != 1 => (ChainFaultKind.WrongSequence, $"The run starts at line {sequence}, but with no held position it starts at the origin's first line, 1."),
            (_, null, { } at) when sequence > at.Sequence + 1 => (ChainFaultKind.WrongSequence, $"The run starts at line {sequence}, past {at.Sequence + 1}, the one after the held line {at.Sequence}."),
            (_, { } before, _) when sequence != before.Content.Sequence + 1 => (ChainFaultKind.WrongSequence, SequenceReason(sequence, before.Content.Sequence)),
            _ when sequence == 1 && content.PreviousHash is not null => (ChainFaultKind.WrongPreviousHash, $"The origin's first line names {content.PreviousHash} as its previous hash, where a first line names none."),
            _ when sequence > 1 && content.PreviousHash is null => (ChainFaultKind.WrongPreviousHash, $"Line {sequence} names no previous hash, as only the origin's first line does."),
            (_, { } before, _) when content.PreviousHash != before.Hash => (ChainFaultKind.WrongPreviousHash, $"The line names {content.PreviousHash} as its previous hash, but the line before it hashes to {before.Hash}."),
            (_, null, { } at) when sequence == at.Sequence + 1 && content.PreviousHash != at.Hash => (ChainFaultKind.WrongPreviousHash, $"The line names {content.PreviousHash} as its previous hash, but the held line {at.Sequence} hashes to {at.Hash}."),
            (_, _, { } at) when sequence == at.Sequence && line.Hash != at.Hash => (ChainFaultKind.HeldHashMismatch, $"Line {sequence} hashes to {line.Hash}, but the held line {at.Sequence} hashes to {at.Hash}: the origin's chain has forked from the one held."),
            _ => null,
        };
    }

    private static string SequenceReason(long sequence, long before) =>
        (sequence - before) switch
        {
            > 1 => $"Line {sequence} follows line {before}, a gap where {before + 1} is expected.",
            0 => $"Line {sequence} repeats the line before it, where {before + 1} is expected.",
            _ => $"Line {sequence} steps back from line {before}, where {before + 1} is expected.",
        };
}
