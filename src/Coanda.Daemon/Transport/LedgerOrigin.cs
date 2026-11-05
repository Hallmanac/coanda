namespace Coanda.Daemon.Transport;

/// <summary>
/// The node id this store exports its lines under, one document in the node tenant written before the
/// first line is. A store whose node identity has since changed exports nothing: a second origin for the
/// same history would sign every event again under a chain no reader can tie to the first.
/// </summary>
public sealed class LedgerOrigin
{
    public const string DocumentId = "ledger-origin";

    public string Id { get; set; } = DocumentId;

    public Guid NodeId { get; set; }
}
