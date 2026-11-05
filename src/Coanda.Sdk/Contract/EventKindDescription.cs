namespace Coanda.Sdk.Contract;

/// <summary>One external event kind a module can report, module-prefixed, with the fields a watch may filter on.</summary>
public sealed record EventKindDescription(string Kind, IReadOnlyList<string> WatchableFields);
