namespace Coanda.Daemon.Transport;

/// <summary>
/// The one root key this node trusts for a project when it imports other origins' lines: one document in
/// the project's tenant, kept by this node alone. It is a document and never an event, so it is never on a
/// line, and import never takes the key it trusts from a fetched line. A project with none imports no line
/// from another origin. The join writes it with the key of the owner root it verified, and the owner's node
/// writes it with its own key when its first vouch records the owner root; nothing else in the daemon does.
/// </summary>
public sealed class LedgerRoot
{
    public const string DocumentId = "ledger-root";

    public string Id { get; set; } = DocumentId;

    /// <summary>The trusted root key, 32 bytes in standard base64 with padding.</summary>
    public string Key { get; set; } = string.Empty;
}
