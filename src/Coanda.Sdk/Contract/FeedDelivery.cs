namespace Coanda.Sdk.Contract;

/// <summary>
/// Core events delivered to a module (<c>feed-delivery.schema.json</c>), in sequence order and at
/// least once, so a module reacts to an event it has seen before as it did the first time.
/// </summary>
public sealed record FeedDelivery(IReadOnlyList<FeedEvent> Events);
