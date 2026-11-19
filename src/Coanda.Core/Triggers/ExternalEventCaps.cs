namespace Coanda.Core.Triggers;

/// <summary>
/// The longest external id <see cref="TriggerDecider"/> records, counted in UTF-16 characters as
/// <see cref="string.Length"/> does and as <see cref="Parcels.ParcelCaps"/> counts. The cap binds new
/// recordings on this node; an event already in the record, or one a ledger import appends, is never
/// measured against it.
/// </summary>
public static class ExternalEventCaps
{
    /// <summary>
    /// An external id, as given. A GitHub node id is a few dozen characters, a webhook delivery id is a
    /// 36-character UUID, and a schedule tick's fixed id is a name and a time, so each sits well inside it.
    /// </summary>
    public const int ExternalIdLength = 1_000;
}
