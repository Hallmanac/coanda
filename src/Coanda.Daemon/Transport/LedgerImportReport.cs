namespace Coanda.Daemon.Transport;

/// <summary>Why the import of an origin's lines stopped.</summary>
public enum ImportFaultKind
{
    /// <summary>The line does not parse as a line.</summary>
    Unparsable,

    /// <summary>The line's signature is not its signer's over the rest of it.</summary>
    BadSignature,

    /// <summary>The line names another origin than the node its ref is named for.</summary>
    WrongOrigin,

    /// <summary>The line names another project id than this node's registration of the project.</summary>
    WrongProject,

    /// <summary>The line's sequence is not the next one: a gap, a repeat, or a step back.</summary>
    WrongSequence,

    /// <summary>The line's previous hash is not the hash of the line before it.</summary>
    WrongPreviousHash,

    /// <summary>The ref's line at the sequence this node holds is not the line it holds: the chain has forked.</summary>
    HeldHashMismatch,

    /// <summary>The ref ends before the sequence this node holds for the origin.</summary>
    Rewind,

    /// <summary>The project has no root key record on this node, so no other origin's line can be trusted.</summary>
    NoTrustedRoot,

    /// <summary>The line's signer key may not sign for its origin by the identity records this node holds.</summary>
    UntrustedSigner,

    /// <summary>The line's event is not in this build's current shape.</summary>
    EarlierShape,

    /// <summary>
    /// The line's event holds what this build's type of it does not, a member it does not know or a value it
    /// writes otherwise, as a later build's shape would, so storing it would change it.
    /// </summary>
    LaterShape,

    /// <summary>The line passed every check and the store refused to append it.</summary>
    StoreRefused,

    /// <summary>The ref could not be read from the clone.</summary>
    Unreadable,
}

/// <summary>What stopped an origin's import: the line's sequence, when one can be named, the fault, and why.</summary>
public sealed record ImportFault(long? Sequence, ImportFaultKind Kind, string Reason);

/// <summary>
/// What one import did for one origin of a project: how many of its lines it stored, the sequence this node
/// now holds for it (none when it holds no line of it), and the fault that stopped it, none when every line
/// its refs hold is stored.
/// </summary>
public sealed record OriginImport(Guid Origin, int Stored, long? Held, ImportFault? Fault);

/// <summary>
/// What one import of one project did: each origin it read, in the order of their ids, and every reason a
/// clone could not be fetched or read, none when every clone was.
/// </summary>
public sealed record LedgerImportReport(string Project, IReadOnlyList<OriginImport> Origins, IReadOnlyList<string> Failures);
