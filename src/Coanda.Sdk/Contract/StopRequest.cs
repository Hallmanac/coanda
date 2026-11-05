namespace Coanda.Sdk.Contract;

/// <summary>The dispatch a module is to stop because a person stopped its flow run (<c>stop.schema.json</c>).</summary>
public sealed record StopRequest(Guid DispatchId);
