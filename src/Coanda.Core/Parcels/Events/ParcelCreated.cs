namespace Coanda.Core.Parcels.Events;

public sealed record ParcelCreated(
    Guid ParcelId,
    string Project,
    string TypeName,
    string TypeHash,
    string State,
    string Title,
    string CreatedBy,
    DateTimeOffset CreatedAt);
