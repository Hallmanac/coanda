namespace Coanda.Sdk.Contract;

/// <summary>The dispatch the daemon asks a module about after a restart (<c>status.schema.json#/definitions/request</c>).</summary>
public sealed record StatusRequest(Guid DispatchId);
