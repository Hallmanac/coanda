namespace Coanda.Sdk.Contract;

/// <summary>
/// One event kind plus equality filters: an event matches when it is of the kind and every filter's
/// field holds exactly the filter's value.
/// </summary>
public sealed record Watch(string Kind, IReadOnlyList<WatchFilter> Filters);
