namespace Coanda.Sdk.Contract;

/// <summary>
/// Core events delivered to a module (<c>feed-delivery.schema.json</c>), in sequence order and at
/// least once, so a module reacts to an event it has seen before as it did the first time. A flow
/// run's ended or stopped event arrives only after the core's worker for that flow run has returned and
/// after any stop call on a dispatch the stopped flow run held, so nothing the core runs for the flow run
/// is still running in its work path by then.
/// </summary>
public sealed record FeedDelivery(IReadOnlyList<FeedEvent> Events);
