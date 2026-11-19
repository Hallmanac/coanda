namespace Coanda.Daemon.Triggers;

/// <summary>A bound flow about to start on a parcel because the last parcel blocking one of its entries closed.</summary>
internal sealed record WaitedEntryStart(string Project, Guid ParcelId, string Flow);
