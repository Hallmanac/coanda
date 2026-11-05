namespace Coanda.Sdk.Contract;

/// <summary>One equality filter of a watch: the event's <paramref name="Field"/> must equal <paramref name="Value"/> exactly.</summary>
public sealed record WatchFilter(string Field, string Value);
