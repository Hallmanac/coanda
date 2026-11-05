namespace Coanda.Daemon.Transport;

/// <summary>Where a reader already stands in an origin's chain: the sequence of the last line it holds, and that line's hash.</summary>
public sealed record HeldPosition(long Sequence, string Hash);

public enum ChainOutcome
{
    /// <summary>Every line of the run verified.</summary>
    Verified,

    /// <summary>A line of the run failed; <see cref="ChainVerification.Fault"/> names it.</summary>
    Broken,

    /// <summary>Every line verified, but the run ends before the held sequence: the origin's chain is shorter than the one the reader holds.</summary>
    Rewind,
}

public enum ChainFaultKind
{
    /// <summary>The line does not parse as a line.</summary>
    Unparsable,

    /// <summary>The line's signature is not its signer's over the rest of it.</summary>
    BadSignature,

    /// <summary>The line is signed by a key the reader does not accept for the origin.</summary>
    UnacceptedSigner,

    /// <summary>The line names another origin than the run's.</summary>
    WrongOrigin,

    /// <summary>The line names another project than the run's.</summary>
    WrongProject,

    /// <summary>The line's sequence is not the expected one: a gap, a repeat, a step back, or a first line past where the run may start.</summary>
    WrongSequence,

    /// <summary>The line's previous hash is not the hash of the line before it, or, for the origin's first line, not the absence section 6 names.</summary>
    WrongPreviousHash,

    /// <summary>The line at the held sequence does not hash to the held hash.</summary>
    HeldHashMismatch,
}

/// <summary>
/// The first bad line of a run: its sequence (the one it names, or, when it does not parse, the one
/// expected at its place, or none when nothing can be expected there), its place in the run counted
/// from 0, its fault, and why.
/// </summary>
public sealed record ChainFault(long? Sequence, int Index, ChainFaultKind Kind, string Reason);

/// <summary>
/// What verifying a run found. <see cref="VerifiedLines"/> counts the lines reported verified from
/// the start of the run, and <see cref="VerifiedThrough"/> and <see cref="VerifiedThroughHash"/> name
/// the last of them, none when no line is reported verified.
/// </summary>
public sealed record ChainVerification(
    ChainOutcome Outcome,
    int VerifiedLines,
    long? VerifiedThrough,
    string? VerifiedThroughHash,
    ChainFault? Fault);
